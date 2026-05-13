// ---------------------------------------------------------------------------
//  Gateway_Lyrion_LMS_IP - Lyrion Server gateway driver (Driver 1 of 3)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LyrionCommunity.Crestron.Lyrion.Gateway.Protocol;

namespace LyrionCommunity.Crestron.Lyrion.Gateway.Transport
{
    /// <summary>Connection state reported by <see cref="LmsCliClient"/>.</summary>
    public enum LmsConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Faulted
    }

    /// <summary>
    /// Persistent async TCP client for the LMS CLI (Telnet) protocol.
    /// Reconnect schedule per CLAUDE.md: 2s → 5s → 10s → 30s → 60s (cap).
    /// </summary>
    /// <remarks>
    /// All raw transitions are forwarded to the caller via
    /// <see cref="ConnectionStateChanged"/>. Higher-level smoothing
    /// (oscillation suppression, minimum stable time) is applied above this
    /// layer in <see cref="Lifecycle.ServerConnectivityFsm"/>.
    /// <para/>
    /// Memory: the line-assembly buffer is capped at <see cref="MaxLineBytes"/>;
    /// oversize lines are dropped without growing the buffer further.
    /// </remarks>
    internal sealed class LmsCliClient : IDisposable
    {
        private const int MaxLineBytes = 64 * 1024;

        // CLAUDE.md mandates this exact sequence; values are in seconds.
        private static readonly int[] BackoffSecondsSchedule = new[] { 2, 5, 10, 30, 60 };

        private readonly string _host;
        private readonly int _port;
        private readonly string _username;
        private readonly string _password;
        private readonly Action<string> _log;

        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        // Serializes Start/Stop transitions so the _cts and _workerTask
        // fields cannot be reassigned concurrently. Today only RebuildTransport
        // calls Start, and it holds GatewayDriver._gate — but relying on that
        // invariant from outside is a foot-gun, so we guard locally too.
        private readonly object _startLock = new object();

        private readonly object _stateLock = new object();
        // volatile so SendLineAsync can read _state outside _stateLock without
        // a stale-cache hazard on weakly-ordered hardware. Mutation still
        // happens under _stateLock so the read-modify-write in SetState stays
        // atomic with the equality check.
        private volatile LmsConnectionState _state = LmsConnectionState.Disconnected;

        private CancellationTokenSource _cts;
        private Task _workerTask;

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

        public event Action<LmsMessage> MessageReceived;
        public event Action<LmsConnectionState> ConnectionStateChanged;

        /// <summary>
        /// Raised when LMS rejects our login. Surfaced separately so the FSM
        /// can log it as an error per CLAUDE.md "ERROR LOGGING ONLY".
        /// </summary>
        public event Action<string> AuthenticationFailed;

        public LmsConnectionState State
        {
            get { lock (_stateLock) { return _state; } }
        }

        public Task StartAsync(CancellationToken externalToken)
        {
            lock (_startLock)
            {
                if (_workerTask != null && !_workerTask.IsCompleted)
                {
                    return Task.CompletedTask;
                }

                var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
                _cts = cts;
                _workerTask = Task.Run(() => RunAsync(cts.Token));
                return Task.CompletedTask;
            }
        }

        public async Task StopAsync(TimeSpan timeout)
        {
            var cts = _cts;
            if (cts != null)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
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
            // Bounded Wait rather than GetAwaiter().GetResult(): the latter
            // is a sync-over-async pattern that would deadlock if any
            // continuation downstream were to capture the calling sync
            // context. The 3s outer bound is StopAsync's 2s internal timeout
            // plus a small slack for cancellation/dispose handlers to run.
            try
            {
                var stop = StopAsync(TimeSpan.FromSeconds(2));
                stop.Wait(TimeSpan.FromSeconds(3));
            }
            catch { }

            _cts?.Dispose();
            _writeLock.Dispose();
        }

        public async Task<bool> SendLineAsync(string commandLine, CancellationToken ct)
        {
            if (commandLine == null) return false;

            var bytes = Encoding.UTF8.GetBytes(commandLine + "\r\n");

            // Acquire the write lock before reading _stream so a concurrent
            // CloseSocket() cannot null/dispose the stream between our read
            // and our use of it. This closes the race that would otherwise
            // silently drop commands during reconnect.
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_state != LmsConnectionState.Connected)
                {
                    return false;
                }
                var stream = _stream;
                if (stream == null)
                {
                    return false;
                }

                await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                // Socket torn down underneath us; nothing to close, just bail.
                return false;
            }
            catch (Exception)
            {
                CloseSocket();
                return false;
            }
            finally
            {
                try { _writeLock.Release(); }
                catch (ObjectDisposedException) { }
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                SetState(LmsConnectionState.Connecting);

                TcpClient tcp = null;
                NetworkStream stream = null;

                try
                {
                    tcp = new TcpClient { NoDelay = true };
                    EnableKeepAliveFlag(tcp);
                    await ConnectWithCancellationAsync(tcp, _host, _port, ct).ConfigureAwait(false);
                    TuneKeepAliveInterval(tcp);
                    stream = tcp.GetStream();
                    _tcpClient = tcp;
                    _stream = stream;

                    SetState(LmsConnectionState.Connected);
                    attempt = 0;

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

                if (ct.IsCancellationRequested) break;

                SetState(LmsConnectionState.Faulted);

                var seconds = BackoffSecondsSchedule[Math.Min(attempt, BackoffSecondsSchedule.Length - 1)];
                attempt++;

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            SetState(LmsConnectionState.Disconnected);
        }

        /// <summary>
        /// Enable the bare SO_KEEPALIVE flag. Safe to call pre-connect.
        /// </summary>
        private static void EnableKeepAliveFlag(TcpClient client)
        {
            try
            {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            catch { /* unsupported on some constrained runtimes */ }
        }

        /// <summary>
        /// Tune keepalive timing via IOControl. Must be called AFTER the
        /// socket is connected — <c>IOControlCode.KeepAliveValues</c>
        /// requires a connected socket on Windows / .NET Framework.
        /// </summary>
        private static void TuneKeepAliveInterval(TcpClient client)
        {
            try
            {
                // Windows / Mono: 30s idle, 10s interval. 12-byte payload:
                //   u32 onoff (1) | u32 time-ms (30000) | u32 interval-ms (10000)
                var keepAlive = new byte[12];
                keepAlive[0] = 1;
                BitConverter.GetBytes((uint)30000).CopyTo(keepAlive, 4);
                BitConverter.GetBytes((uint)10000).CopyTo(keepAlive, 8);
                client.Client.IOControl(IOControlCode.KeepAliveValues, keepAlive, null);
            }
            catch { /* not all platforms expose KeepAliveValues; SO_KEEPALIVE alone still helps */ }
        }

        private static async Task ConnectWithCancellationAsync(TcpClient client, string host, int port, CancellationToken ct)
        {
            var connectTask = client.ConnectAsync(host, port);
            using (ct.Register(() =>
            {
                try { client.Close(); }
                catch { }
            }))
            {
                await connectTask.ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
        }

        private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
        {
            var readBuffer = new byte[8192];
            using (var lineBuffer = new MemoryStream(1024))
            {
                while (!ct.IsCancellationRequested)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, ct).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Stream disposed by CloseSocket() during a clean teardown.
                        return;
                    }
                    catch (SocketException)
                    {
                        // Hard reset from the peer.
                        return;
                    }

                    if (bytesRead <= 0) return;

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
                                lineBuffer.SetLength(0);
                                continue;
                            }
                            lineBuffer.WriteByte(b);
                        }
                    }
                }
            }
        }

        private void EmitLine(MemoryStream lineBuffer)
        {
            if (lineBuffer.Length == 0) return;

            string line;
            try
            {
                line = Encoding.UTF8.GetString(lineBuffer.GetBuffer(), 0, (int)lineBuffer.Length);
            }
            catch
            {
                return;
            }

            LmsMessage message;
            try
            {
                message = LmsCliParser.Parse(line);
            }
            catch
            {
                return;
            }

            // LMS returns a single "login" line on auth failure too — but the
            // canonical signal is the connection drop that follows. We surface
            // any explicit error line through AuthenticationFailed so the FSM
            // can log it once and avoid retry-loop chatter.
            if (message.Kind == LmsMessageKind.GlobalRaw
                && message.Tokens != null
                && message.Tokens.Length >= 2
                && string.Equals(message.Tokens[0], "login", StringComparison.OrdinalIgnoreCase)
                && string.Equals(message.Tokens[1], "failed", StringComparison.OrdinalIgnoreCase))
            {
                try { AuthenticationFailed?.Invoke("LMS rejected credentials."); }
                catch { }
                return;
            }

            try { MessageReceived?.Invoke(message); }
            catch (Exception ex) { _log("LmsCliClient: message handler threw: " + ex); }
        }

        private void SetState(LmsConnectionState newState)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = _state != newState;
                if (changed) _state = newState;
            }

            if (!changed) return;

            try { ConnectionStateChanged?.Invoke(newState); }
            catch (Exception ex) { _log("LmsCliClient: state change handler threw: " + ex); }
        }

        private void TeardownCurrentConnection(TcpClient tcp, Stream stream)
        {
            _stream = null;
            _tcpClient = null;

            try { stream?.Dispose(); } catch { }
            try { tcp?.Close(); } catch { }
        }

        private void CloseSocket()
        {
            var tcp = _tcpClient;
            _tcpClient = null;
            var stream = _stream;
            _stream = null;

            try { stream?.Dispose(); } catch { }
            try { tcp?.Close(); } catch { }
        }
    }
}
