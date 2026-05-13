// ---------------------------------------------------------------------------
//  Gateway_Lyrion_LMS_IP - Lyrion Server gateway driver (Driver 1 of 3)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;
using LyrionCommunity.Crestron.Lyrion.Gateway.Lifecycle;
using LyrionCommunity.Crestron.Lyrion.Gateway.Protocol;
using LyrionCommunity.Crestron.Lyrion.Gateway.Registry;
using LyrionCommunity.Crestron.Lyrion.Gateway.Services;
using LyrionCommunity.Crestron.Lyrion.Gateway.Transport;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Gateway
{
    /// <summary>
    /// Root V2 entity for Driver 1. Owns the LMS transport clients, the
    /// player registry, the connectivity FSM, and the gateway service
    /// implementation. Has no Crestron Home room assignment — its only
    /// public surface is the service exposed via
    /// <see cref="LyrionGatewayServiceRegistry"/>.
    /// </summary>
    public sealed class GatewayDriver : ReflectedAttributeDriverEntity, IDisposable
    {
        private static readonly TimeSpan MetadataFreezeTtl = TimeSpan.FromSeconds(30);

        private readonly Action<string> _log;
        private readonly object _gate = new object();

        private readonly PlayerRegistry _registry;
        private readonly LyrionGatewayServiceImpl _service;
        private readonly ServerConnectivityFsm _fsm;

        private LmsCliClient _cli;
        private LmsJsonRpcClient _rpc;

        // CLI event delegates stored as fields so they can be unsubscribed
        // cleanly during transport rebuild. Anonymous lambdas would leak the
        // FSM/log references onto the old client until it is GC'd.
        private Action<LmsConnectionState> _cliStateHandler;
        private Action<string> _cliAuthHandler;

        private CancellationTokenSource _lifetime = new CancellationTokenSource();
        private Timer _freezePump;
        private Timer _reconcileTimer;

        private string _host;
        private int _httpPort = 9000;
        private int _cliPort = 9090;
        private string _username;
        private string _password;

        private volatile bool _disposed;
        private volatile bool _serverConnected;

        // Diagnostic-only entity properties.
        [EntityProperty(Id = "lyrion:connectionState")]
        public LogicalConnectivityState ConnectionState { get; private set; } = LogicalConnectivityState.Disconnected;

        [EntityProperty(Id = "lyrion:serverVersion")]
        public string ServerVersion { get; private set; } = string.Empty;

        public GatewayDriver(DriverControllerCreationArgs args, DriverImplementationResources resources)
            : base(DriverController.RootControllerId)
        {
            _log = BuildLogger();
            _registry = new PlayerRegistry();
            _service = new LyrionGatewayServiceImpl(
                _registry,
                SendCliLineSync,
                () => _serverConnected,
                OnPlayerBound);

            _fsm = new ServerConnectivityFsm(_log);
            _fsm.SmoothedTransition += OnSmoothedServerConnectivity;

            var cfgArgs = DataDrivenConfigurationControllerArgs.FromResources(args, resources, ControllerId);
            ConfigurationController = new DelegateDataDrivenConfigurationController(
                cfgArgs,
                ApplyConfigurationItems,
                null,
                null);

            LyrionGatewayServiceRegistry.Register(_service);
        }

        internal DataDrivenConfigurationController ConfigurationController { get; }

        // ===== Configuration =====

        private ConfigurationItemErrors ApplyConfigurationItems(
            DataDrivenConfigurationController.ApplyConfigurationAction action,
            string stepId,
            IDictionary<string, DriverEntityValue?> values)
        {
            switch (action)
            {
                case DataDrivenConfigurationController.ApplyConfigurationAction.ApplyAll:
                case DataDrivenConfigurationController.ApplyConfigurationAction.ApplyStep:
                    {
                        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

                        ReadStringIfPresent(values, "_Host_", ref _host);
                        ReadIntIfPresent(values, "_HttpPort_", ref _httpPort);
                        ReadIntIfPresent(values, "_CliPort_", ref _cliPort);
                        ReadStringIfPresent(values, "_Username_", ref _username);
                        ReadStringIfPresent(values, "_Password_", ref _password);

                        if (!string.IsNullOrEmpty(_host) && (_httpPort <= 0 || _httpPort > 65535))
                            errors["_HttpPort_"] = "HTTP port must be between 1 and 65535.";
                        if (!string.IsNullOrEmpty(_host) && (_cliPort <= 0 || _cliPort > 65535))
                            errors["_CliPort_"] = "CLI port must be between 1 and 65535.";

                        if (errors.Count > 0) return new ConfigurationItemErrors(errors, null);

                        if (!string.IsNullOrEmpty(_host))
                        {
                            RebuildTransport();
                            EnsureFreezePumpRunning();
                        }

                        return null;
                    }

                case DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues:
                    {
                        if (values.ContainsKey("_Host_")
                            || values.ContainsKey("_HttpPort_")
                            || values.ContainsKey("_CliPort_")
                            || values.ContainsKey("_Username_")
                            || values.ContainsKey("_Password_"))
                        {
                            TeardownTransport();
                        }
                        return null;
                    }
            }
            return null;
        }

        private static void ReadStringIfPresent(IDictionary<string, DriverEntityValue?> values, string key, ref string target)
        {
            if (values.TryGetValue(key, out var v) && v.HasValue)
                target = v.Value.GetValue<string>() ?? string.Empty;
        }

        private static void ReadIntIfPresent(IDictionary<string, DriverEntityValue?> values, string key, ref int target)
        {
            if (values.TryGetValue(key, out var v) && v.HasValue)
            {
                var asLong = v.Value.GetValue<long>();
                if (asLong < int.MinValue) asLong = int.MinValue;
                if (asLong > int.MaxValue) asLong = int.MaxValue;
                target = (int)asLong;
            }
        }

        // ===== Transport lifecycle =====

        private void RebuildTransport()
        {
            lock (_gate)
            {
                if (_disposed) return;
                TeardownTransport_NoLock();

                var lifetime = new CancellationTokenSource();
                _lifetime = lifetime;

                var cli = new LmsCliClient(_host, _cliPort, _username, _password, _log);
                _cliStateHandler = s => _fsm.OnRawTransition(s);
                _cliAuthHandler = msg => _log("Gateway ERROR auth: " + msg);

                cli.MessageReceived += OnCliMessage;
                cli.ConnectionStateChanged += _cliStateHandler;
                cli.AuthenticationFailed += _cliAuthHandler;

                var rpc = new LmsJsonRpcClient(_host, _httpPort, _username, _password, TimeSpan.FromSeconds(15), _log);

                _cli = cli;
                _rpc = rpc;

                _ = cli.StartAsync(lifetime.Token);
            }
        }

        private void TeardownTransport()
        {
            lock (_gate) { TeardownTransport_NoLock(); }
        }

        private void TeardownTransport_NoLock()
        {
            var oldLifetime = _lifetime;
            var oldCli = _cli;
            var oldRpc = _rpc;
            var oldStateHandler = _cliStateHandler;
            var oldAuthHandler = _cliAuthHandler;

            // Only allocate a replacement lifetime if we are still live; on
            // final dispose the caller drops the field and we avoid leaking
            // an undisposed CTS.
            _lifetime = _disposed ? null : new CancellationTokenSource();
            _cli = null;
            _rpc = null;
            _cliStateHandler = null;
            _cliAuthHandler = null;

            if (oldCli != null)
            {
                try { oldCli.MessageReceived -= OnCliMessage; } catch { }
                if (oldStateHandler != null)
                {
                    try { oldCli.ConnectionStateChanged -= oldStateHandler; } catch { }
                }
                if (oldAuthHandler != null)
                {
                    try { oldCli.AuthenticationFailed -= oldAuthHandler; } catch { }
                }
                try
                {
                    _ = oldCli.StopAsync(TimeSpan.FromSeconds(2)).ContinueWith(
                        _ => { try { oldCli.Dispose(); } catch { } },
                        TaskScheduler.Default);
                }
                catch { }
            }

            if (oldLifetime != null)
            {
                try { oldLifetime.Cancel(); }
                catch (ObjectDisposedException) { }
                _ = Task.Run(() => { try { oldLifetime.Dispose(); } catch { } });
            }

            _ = oldRpc;

            _serverConnected = false;
            _registry.SetServerConnected(false);
        }

        private void EnsureFreezePumpRunning()
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_freezePump != null) return;
                _freezePump = new Timer(_ => SweepFrozenMetadata(), null,
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }

        private void SweepFrozenMetadata()
        {
            try { _registry.SweepFrozenMetadata(MetadataFreezeTtl); }
            catch { }
        }

        // ===== CLI events =====

        private void OnCliMessage(LmsMessage message)
        {
            if (message.Kind == LmsMessageKind.Empty) return;

            switch (message.Kind)
            {
                case LmsMessageKind.ServerVersion:
                    if (message.Payload is string version) UpdateServerVersion(version);
                    return;

                case LmsMessageKind.ListenAck:
                case LmsMessageKind.LoginAck:
                case LmsMessageKind.PlayersResponse:
                case LmsMessageKind.GlobalRaw:
                    return;
            }

            if (string.IsNullOrEmpty(message.Mac)) return;
            if (!_registry.IsBound(message.Mac)) return;

            switch (message.Kind)
            {
                case LmsMessageKind.Play:
                    _registry.NotePlaybackState(message.Mac, LyrionPlaybackState.Playing);
                    break;

                case LmsMessageKind.Pause:
                    if (message.Payload is bool isPaused)
                    {
                        _registry.NotePlaybackState(message.Mac,
                            isPaused ? LyrionPlaybackState.Paused : LyrionPlaybackState.Playing);
                    }
                    break;

                case LmsMessageKind.Stop:
                    _registry.NotePlaybackState(message.Mac, LyrionPlaybackState.Stopped);
                    break;

                case LmsMessageKind.Volume:
                    if (message.Payload is int v) _registry.NoteVolume(message.Mac, v);
                    break;

                case LmsMessageKind.Mute:
                    if (message.Payload is bool m) _registry.NoteMute(message.Mac, m);
                    break;

                case LmsMessageKind.Power:
                    if (message.Payload is bool p) _registry.NoteExplicitPower(message.Mac, p);
                    break;

                case LmsMessageKind.Time:
                    if (message.Payload is double sec) _registry.NotePosition(message.Mac, (int)sec);
                    break;

                case LmsMessageKind.Repeat:
                    if (message.Payload is int r) _registry.NoteRepeat(message.Mac, r);
                    break;

                case LmsMessageKind.Shuffle:
                    if (message.Payload is int s) _registry.NoteShuffle(message.Mac, s);
                    break;

                case LmsMessageKind.NewSong:
                    if (message.Payload is NewSongPayload song)
                    {
                        _registry.NoteMetadata(message.Mac, song.Title, null, null, null, -1, 0);
                        // Trigger a full status query so we pick up artist /
                        // album / artwork / duration on the next CLI cycle.
                        _ = SendCliForPlayer(message.Mac, LmsCliCommands.QueryStatus(message.Mac));
                    }
                    break;

                case LmsMessageKind.Client:
                    if (message.Payload is string sub)
                    {
                        switch (sub)
                        {
                            case "new":
                            case "reconnect":
                                _registry.NoteLifecycle(message.Mac, PlayerLifecycleState.Online);
                                _ = SendCliForPlayer(message.Mac, LmsCliCommands.QueryStatus(message.Mac));
                                break;

                            case "disconnect":
                            case "forget":
                                _registry.NoteLifecycle(message.Mac, PlayerLifecycleState.Offline);
                                break;
                        }
                    }
                    break;
            }
        }

        // ===== FSM callback =====

        private void OnSmoothedServerConnectivity(LogicalConnectivityState committed)
        {
            var connected = committed == LogicalConnectivityState.Connected;
            _serverConnected = connected;
            ConnectionState = committed;

            try
            {
                NotifyPropertyChanged("lyrion:connectionState", new DriverEntityValue((long)ConnectionState));
            }
            catch { }

            if (connected)
            {
                // Reconnect is a hard state boundary: re-issue listen + a full
                // status query for every bound MAC and let the responses
                // recompute state in the registry. Re-publish all derived
                // state once those updates have settled.
                _registry.SetServerConnected(true);
                ReconcileBoundPlayers();
            }
            else
            {
                _registry.SetServerConnected(false);
            }
        }

        private void ReconcileBoundPlayers()
        {
            var macs = _registry.BoundMacs();
            foreach (var mac in macs)
            {
                _ = SendCliForPlayer(mac, LmsCliCommands.QueryStatus(mac));
            }

            // Defer republish until status responses have had time to arrive.
            // Republishing immediately would surface pre-disconnect state to
            // Driver 2/3 (CLAUDE.md "MUST NOT trust cached or incremental
            // state"). 2 seconds is long enough for the CLI round trip but
            // short enough that any UI flicker is bounded.
            ScheduleReconcileRepublish(macs);
        }

        private void ScheduleReconcileRepublish(IReadOnlyList<string> macs)
        {
            lock (_gate)
            {
                if (_disposed) return;
                try { _reconcileTimer?.Dispose(); } catch { }
                _reconcileTimer = new Timer(_ =>
                {
                    try { _registry.RepublishAll(macs); }
                    catch { }
                }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            }
        }

        private void OnPlayerBound(string mac)
        {
            // If the server is already connected when a Media/Volume driver
            // binds, immediately request a fresh status for that MAC.
            if (string.IsNullOrEmpty(mac)) return;
            if (_serverConnected)
            {
                _ = SendCliForPlayer(mac, LmsCliCommands.QueryStatus(mac));
            }
        }

        // ===== CLI send helpers =====

        private bool SendCliLineSync(string line)
        {
            var cli = _cli;
            if (cli == null) return false;
            var token = _lifetime?.Token ?? CancellationToken.None;
            // Observe the task so OperationCanceledException during transport
            // teardown does not become an unobserved task exception.
            cli.SendLineAsync(line, token).ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return true;
        }

        private Task<bool> SendCliForPlayer(string mac, string line)
        {
            var cli = _cli;
            if (cli == null) return Task.FromResult(false);
            var token = _lifetime?.Token ?? CancellationToken.None;
            var send = cli.SendLineAsync(line, token);
            send.ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return send;
        }

        // ===== Diagnostic state =====

        private void UpdateServerVersion(string version)
        {
            version = version ?? string.Empty;
            if (string.Equals(ServerVersion, version, StringComparison.Ordinal)) return;
            ServerVersion = version;
            try { NotifyPropertyChanged("lyrion:serverVersion", new DriverEntityValue(version)); }
            catch { }
        }

        private static Action<string> BuildLogger()
        {
            return message =>
            {
                try { Debug.WriteLine("[Lyrion.Gateway] " + message); }
                catch { }
            };
        }

        public override void Dispose()
        {
            if (_disposed) { base.Dispose(); return; }
            _disposed = true;

            try { LyrionGatewayServiceRegistry.Unregister(_service); } catch { }

            try { _freezePump?.Dispose(); } catch { }
            _freezePump = null;

            Timer reconcile;
            lock (_gate)
            {
                reconcile = _reconcileTimer;
                _reconcileTimer = null;
            }
            try { reconcile?.Dispose(); } catch { }

            try { _fsm.Dispose(); } catch { }
            try { TeardownTransport(); } catch { }

            // TeardownTransport sets _lifetime = null when _disposed is true,
            // but the original lifetime was disposed inside the teardown
            // (cancelled and then disposed off-thread). Nothing further to
            // dispose here; the field is null.
            base.Dispose();
        }
    }
}
