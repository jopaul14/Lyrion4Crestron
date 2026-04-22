// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;
using LyrionCommunity.Crestron.Lyrion.Protocol;

namespace LyrionCommunity.Crestron.Lyrion.Transport
{
    /// <summary>
    /// Connection state reported by <see cref="LmsCliClient"/>.
    /// </summary>
    /// <remarks>
    /// Exposed on the root driver as the <c>lyrion:connectionState</c> property;
    /// the <see cref="EntityDataTypeAttribute"/> allows the SDK to round-trip
    /// the enum value correctly to the Crestron application layer.
    /// </remarks>
    [EntityDataType(Id = "lyrion:ConnectionState")]
    public enum LmsConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Faulted
    }

    /// <summary>
    /// Persistent async TCP client for the LMS CLI (Telnet) protocol.
    /// </summary>
    /// <remarks>
    /// The client owns its connection lifecycle. <see cref="StartAsync"/> kicks
    /// off a background task that maintains the connection, reconnecting with
    /// exponential backoff (initial 2s, doubled up to 180s) whenever the
    /// socket disconnects or faults. <see cref="StopAsync"/> triggers a
    /// cooperative shutdown via cancellation and awaits completion.
    /// <para/>
    /// Thread-safety: all public members are safe to call from any thread. A
    /// single <see cref="SemaphoreSlim"/> serializes socket writes so lines
    /// never interleave. The receive loop runs on a single worker task and
    /// raises events synchronously - handlers should be fast / non-blocking.
    /// <para/>
    /// Memory: no unbounded buffers or queues. The line-assembly buffer is
    /// capped at <see cref="MaxLineBytes"/>; oversize lines are dropped with a
    /// warning rather than growing the buffer without limit.
    /// </remarks>
    internal sealed class LmsCliClient : IDisposable
    {
        /// <summary>Cap on bytes buffered while waiting for a newline. Safety limit.</summary>
        private const int MaxLineBytes = 64 * 1024;

        private const int InitialBackoffMs = 2000;
        private const int MaxBackoffMs = 180 * 1000;

        private readonly string _host;
        private readonly int _port;
        private readonly string _username;
        private readonly string _password;
        private readonly Action<string> _log;

        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        // Mutable state - _stateLock guards only the public-facing State property and
        // the ConnectionStateChanged event; the socket/stream lifetime is managed by
        // the worker loop (single owner), which publishes via Interlocked.Exchange.
        private readonly object _stateLock = new object();
        private LmsConnectionState _state = LmsConnectionState.Disconnected;

        private CancellationTokenSource _cts;
        private Task _workerTask;

        // Current live stream, only read/written by the worker loop except for a
        // final Dispose in StopAsync. Guarded by Interlocked for the disposal path.
        private Stream _stream;
        private TcpClient _tcpClient;

        public LmsCliClient(string host, int port, string username, string password, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Host is required.", nameof(host));
            }

            if (port <= 0 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            _host = host;
            _port = port;
            _username = username ?? string.Empty;
            _password = password ?? string.Empty;
            _log = log ?? (_ => { });
        }

        // ---------------------------------------------------------------- Events

        /// <summary>Raised once per complete CLI line received from the server.</summary>
        public event Action<LmsMessage> MessageReceived;

        /// <summary>Raised whenever the connection transitions between states.</summary>
        public event Action<LmsConnectionState> ConnectionStateChanged;

        /// <summary>Current connection state. Read-only snapshot.</summary>
        public LmsConnectionState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
        }

        // ---------------------------------------------------------------- Lifecycle

        /// <summary>
        /// Start the background connect/receive loop. Idempotent: a second
        /// call while already running is a no-op.
        /// </summary>
        public Task StartAsync(CancellationToken externalToken)
        {
            if (_workerTask != null && !_workerTask.IsCompleted)
            {
                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _workerTask = Task.Run(() => RunAsync(_cts.Token));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Cooperatively stop the client. Waits up to <paramref name="timeout"/>
        /// for the worker loop to exit. Safe to call multiple times.
        /// </summary>
        public async Task StopAsync(TimeSpan timeout)
        {
            var cts = _cts;
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed; fine.
                }
            }

            CloseSocket();

            var worker = _workerTask;
            if (worker != null)
            {
                var completed = await Task.WhenAny(worker, Task.Delay(timeout)).ConfigureAwait(false);
                if (completed != worker)
                {
                    _log("LmsCliClient worker did not exit within timeout; abandoning.");
                }
            }
        }

        public void Dispose()
        {
            try
            {
                StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch
            {
                // Dispose must not throw.
            }

            _cts?.Dispose();
            _writeLock.Dispose();
        }

        // ---------------------------------------------------------------- Sending

        /// <summary>
        /// Send a single CLI command line (newline will be appended). Returns
        /// true on success, false if the socket is not currently connected or
        /// the send failed. Never throws on transport-level errors.
        /// </summary>
        public async Task<bool> SendLineAsync(string commandLine, CancellationToken ct)
        {
            if (commandLine == null)
            {
                return false;
            }

            var stream = _stream;
            if (stream == null || _state != LmsConnectionState.Connected)
            {
                _log("LmsCliClient: dropped command, not connected: " + commandLine);
                return false;
            }

            // Compose line + "\r\n" in one buffer to write atomically under the lock.
            var bytes = Encoding.UTF8.GetBytes(commandLine + "\r\n");

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log("LmsCliClient: write failed: " + ex.Message);
                // Force the receive loop to notice the broken connection.
                CloseSocket();
                return false;
            }
            finally
            {
                // Catch ObjectDisposedException from the semaphore when StopAsync races us.
                try
                {
                    _writeLock.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        // ---------------------------------------------------------------- Worker

        private async Task RunAsync(CancellationToken ct)
        {
            var backoff = InitialBackoffMs;

            while (!ct.IsCancellationRequested)
            {
                SetState(LmsConnectionState.Connecting);

                TcpClient tcp = null;
                NetworkStream stream = null;

                try
                {
                    tcp = new TcpClient { NoDelay = true };
                    await ConnectWithCancellationAsync(tcp, _host, _port, ct).ConfigureAwait(false);
                    stream = tcp.GetStream();
                    _tcpClient = tcp;
                    _stream = stream;

                    SetState(LmsConnectionState.Connected);
                    backoff = InitialBackoffMs; // reset on every successful connect
                    _log($"LmsCliClient: connected to {_host}:{_port}");

                    if (!string.IsNullOrEmpty(_username))
                    {
                        await SendLineAsync(LmsCliCommands.Login(_username, _password), ct).ConfigureAwait(false);
                    }

                    await SendLineAsync(LmsCliCommands.ListenAll(), ct).ConfigureAwait(false);
                    await SendLineAsync(LmsCliCommands.QueryServerVersion(), ct).ConfigureAwait(false);

                    await ReceiveLoopAsync(stream, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown.
                    break;
                }
                catch (Exception ex)
                {
                    _log("LmsCliClient: connect/receive error: " + ex.Message);
                }
                finally
                {
                    TeardownCurrentConnection(tcp, stream);
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                SetState(LmsConnectionState.Faulted);

                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Exponential backoff, clamped.
                backoff = Math.Min(backoff * 2, MaxBackoffMs);
            }

            SetState(LmsConnectionState.Disconnected);
            _log("LmsCliClient: worker exited cleanly.");
        }

        private async Task ConnectWithCancellationAsync(TcpClient client, string host, int port, CancellationToken ct)
        {
            // TcpClient.ConnectAsync doesn't support CancellationToken on .NET Framework,
            // so compose it manually - cancellation forces the socket closed.
            var connectTask = client.ConnectAsync(host, port);
            using (ct.Register(() =>
            {
                try
                {
                    client.Close();
                }
                catch
                {
                    // Best-effort; the connect task will then throw.
                }
            }))
            {
                await connectTask.ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
        }

        private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
        {
            var readBuffer = new byte[8192];
            var lineBuffer = new MemoryStream(1024);

            while (!ct.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Socket was torn down while we were reading.
                    return;
                }

                if (bytesRead <= 0)
                {
                    // Peer closed.
                    return;
                }

                for (var i = 0; i < bytesRead; i++)
                {
                    var b = readBuffer[i];
                    if (b == (byte)'\n')
                    {
                        EmitLine(lineBuffer);
                        lineBuffer.SetLength(0);
                    }
                    else if (b != (byte)'\r')
                    {
                        if (lineBuffer.Length >= MaxLineBytes)
                        {
                            _log($"LmsCliClient: dropping oversize line ({lineBuffer.Length} bytes)");
                            lineBuffer.SetLength(0);
                            // Resync on the next newline.
                            continue;
                        }
                        lineBuffer.WriteByte(b);
                    }
                }
            }
        }

        private void EmitLine(MemoryStream lineBuffer)
        {
            if (lineBuffer.Length == 0)
            {
                return;
            }

            string line;
            try
            {
                line = Encoding.UTF8.GetString(lineBuffer.GetBuffer(), 0, (int)lineBuffer.Length);
            }
            catch (Exception ex)
            {
                _log("LmsCliClient: failed to decode line as UTF-8: " + ex.Message);
                return;
            }

            LmsMessage message;
            try
            {
                message = LmsCliParser.Parse(line);
            }
            catch (Exception ex)
            {
                _log("LmsCliClient: parser threw on line: " + line + " -- " + ex);
                return;
            }

            var handler = MessageReceived;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(message);
            }
            catch (Exception ex)
            {
                _log("LmsCliClient: message handler threw: " + ex);
            }
        }

        // ---------------------------------------------------------------- Helpers

        private void SetState(LmsConnectionState newState)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = _state != newState;
                if (changed)
                {
                    _state = newState;
                }
            }

            if (!changed)
            {
                return;
            }

            var handler = ConnectionStateChanged;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(newState);
            }
            catch (Exception ex)
            {
                _log("LmsCliClient: state change handler threw: " + ex);
            }
        }

        private void TeardownCurrentConnection(TcpClient tcp, Stream stream)
        {
            _stream = null;
            _tcpClient = null;

            try
            {
                stream?.Dispose();
            }
            catch
            {
                // No-op: disposal of a broken socket can throw.
            }

            try
            {
                tcp?.Close();
            }
            catch
            {
                // No-op.
            }
        }

        private void CloseSocket()
        {
            var tcp = _tcpClient;
            _tcpClient = null;
            var stream = _stream;
            _stream = null;

            try
            {
                stream?.Dispose();
            }
            catch
            {
            }

            try
            {
                tcp?.Close();
            }
            catch
            {
            }
        }
    }
}
