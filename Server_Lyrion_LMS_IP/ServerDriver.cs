// ---------------------------------------------------------------------------
//  Server_Lyrion_LMS_IP - Lyrion Server driver (Driver 1 of 4)
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
using LyrionCommunity.Crestron.Lyrion.Server.Lifecycle;
using LyrionCommunity.Crestron.Lyrion.Server.Protocol;
using LyrionCommunity.Crestron.Lyrion.Server.Registry;
using LyrionCommunity.Crestron.Lyrion.Server.Services;
using LyrionCommunity.Crestron.Lyrion.Server.Transport;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Server
{
    /// <summary>
    /// Root V2 entity for Driver 1. Owns the LMS transport clients, the
    /// player registry, the connectivity FSM, and the Lyrion Server service
    /// implementation. Has no Crestron Home room assignment — its only
    /// public surface is the service exposed via
    /// <see cref="LyrionServerServiceRegistry"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Naming history — read this before grepping.</b> Through
    /// 1.0.9 this driver, its project, its assembly, and its package were all
    /// called <c>Gateway_Lyrion_LMS_IP</c>, the class was <c>GatewayDriver</c>,
    /// the namespace was <c>…Lyrion.Gateway</c>, and the shared contract was
    /// <c>ILyrionGatewayService</c>. "Gateway" described its role — the one
    /// process that fronts LMS for the other three drivers. But the name an
    /// installer actually sees in the Crestron Home Setup app and Configure
    /// Pro is the <c>BaseModel</c> in <c>Driver.json</c>: <b>Lyrion Server</b>.
    /// Having the package called Gateway and the device called Server sent
    /// people looking for a driver that did not exist, so in 1.0.10 the code
    /// was renamed to match the user-facing name. The rename is purely
    /// lexical: no behaviour changed, the driver GUID and
    /// <c>DependencyGroup</c> are the same, and the only runtime by-name lookup
    /// (<c>Lyrion_Common.dll</c>, in <c>EntryPoint</c>) was never affected.</para>
    /// <para><b>Two meanings of "Server".</b> That rename introduced an
    /// ambiguity the old name avoided. In this codebase <i>the Lyrion
    /// Server</i> means this driver; <i>LMS</i> or <i>the server</i> means the
    /// Lyrion Media Server it connects to. So <see cref="ServerConnectivityFsm"/>,
    /// <c>_serverConnected</c>, <c>ServerConnectivityChanged</c>, and the
    /// CONNECTED / DISCONNECTED log lines are all about <b>LMS</b>, not about
    /// this driver. Log prefixes were changed to "Lyrion Server:" and the
    /// connectivity messages to say "LMS" for the same reason. When adding
    /// code, keep that convention: qualify the driver as "Lyrion Server" and
    /// the media server as "LMS".</para>
    /// </remarks>
    public sealed class ServerDriver : ReflectedAttributeDriverEntity, IDisposable
    {
        private static readonly TimeSpan MetadataFreezeTtl = TimeSpan.FromSeconds(30);

        private readonly Action<string> _log;
        private readonly object _gate = new object();

        private readonly PlayerRegistry _registry;
        /// <summary>
        /// Keep-alive interval (seconds) for the per-player subscribing status
        /// query. LMS pushes a fresh status on every change and at least this
        /// often, making it the authoritative source for power and playback
        /// state regardless of which discrete notification LMS emits. Pushes
        /// that carry no change raise no events (the registry is change-gated),
        /// so the steady-state cost is one parse per player per interval with
        /// no UI updates and no logging. Mirrors the value used by LMS Material.
        /// </summary>
        private const int StatusSubscribeSeconds = 30;

        private readonly LyrionServerServiceImpl _service;
        private readonly ServerConnectivityFsm _fsm;

        // volatile: written under _gate but read locklessly from the SDK
        // command thread, the FSM timer thread, and the CLI receive thread.
        // ARM hardware (potential Crestron target) requires the acquire barrier
        // that volatile provides; x86 happens to be safe without it.
        private volatile LmsCliClient _cli;

        // CLI event delegates stored as fields so they can be unsubscribed
        // cleanly during transport rebuild. Anonymous lambdas would leak the
        // FSM/log references onto the old client until it is GC'd.
        private Action<LmsConnectionState> _cliStateHandler;
        private Action<string> _cliAuthHandler;

        // volatile for the same reason as _cli. The send helpers snapshot
        // both fields atomically under _gate to close the TOCTOU window where
        // teardown could null _lifetime between the _cli read and the token read.
        private volatile CancellationTokenSource _lifetime = new CancellationTokenSource();
        private Timer _freezePump;
        private Timer _reconcileTimer;
        private Timer _resubscribeTimer;

        private string _host;
        private int _httpPort = 9000;
        private int _cliPort = 9090;
        private string _username;
        private string _password;

        private volatile bool _disposed;
        private volatile bool _serverConnected;

        // Diagnostic-only entity properties.
        [EntityProperty(Id = "lyrion:connectionState")]
        public string ConnectionState { get; private set; } = "Disconnected";

        [EntityProperty(Id = "lyrion:serverVersion")]
        public string ServerVersion { get; private set; } = string.Empty;

        public ServerDriver(DriverControllerCreationArgs args, DriverImplementationResources resources)
            : base(DriverController.RootControllerId)
        {
            _log = BuildLogger();
            _registry = new PlayerRegistry();
            _service = new LyrionServerServiceImpl(
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

            LyrionServerServiceRegistry.Register(_service);
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
            LmsCliClient oldCli;
            CancellationTokenSource oldLifetime;
            lock (_gate)
            {
                if (_disposed) return;
                DetachAndCaptureTransport_NoLock(out oldCli, out oldLifetime);

                // A rebuild is a hard connectivity boundary: this method
                // forces _serverConnected=false and the registry disconnected
                // below, so the FSM must agree, or the new socket's Connected
                // can never be committed. Before 1.0.12 it did not: the old
                // client's handler was detached above before it could report
                // Disconnected, the FSM stayed committed=Connected, the new
                // client's Connected matched it, TryCommit published nothing,
                // and the driver sat "disconnected" with a live socket —
                // every command dropped, every player unavailable, no log —
                // after any installer re-save of the LMS settings, until LMS
                // itself dropped for >5 s.
                _fsm.Reset();

                var lifetime = new CancellationTokenSource();
                _lifetime = lifetime;

                var cli = new LmsCliClient(_host, _cliPort, _username, _password, _log);
                _cliStateHandler = s =>
                {
                    // The per-player "status ... subscribe:N" subscriptions live
                    // on the CLI socket and die with it, while "listen 1" is
                    // re-sent per connection by LmsCliClient. The FSM smooths
                    // away flaps shorter than its stability window, so a fast
                    // drop/reconnect never re-commits CONNECTED and never runs
                    // ReconcileBoundPlayers — leaving the status subscriptions
                    // silently dead until the next committed reconnect. Re-arm
                    // them off the RAW transition so they always follow the
                    // socket. Change-gating in the registry keeps the resulting
                    // status responses silent when nothing actually moved.
                    if (s == LmsConnectionState.Connected) ResubscribeBoundPlayers();
                    _fsm.OnRawTransition(s);
                };
                _cliAuthHandler = msg => _log("Lyrion Server ERROR auth: " + msg);

                cli.MessageReceived += OnCliMessage;
                cli.ConnectionStateChanged += _cliStateHandler;
                cli.AuthenticationFailed += _cliAuthHandler;

                _cli = cli;

                _ = cli.StartAsync(lifetime.Token);
            }

            // Dispose OUTSIDE _gate: oldCli.Dispose() can block up to ~3s
            // waiting for the worker task, and holding _gate that long would
            // stall SendCliLineSync, OnSmoothedServerConnectivity's reconcile
            // scheduling, and any other lock-takers.
            DisposeOldTransport(oldCli, oldLifetime);

            _registry.SetServerConnected(false);
        }

        private void TeardownTransport()
        {
            LmsCliClient oldCli;
            CancellationTokenSource oldLifetime;
            lock (_gate)
            {
                DetachAndCaptureTransport_NoLock(out oldCli, out oldLifetime);
                _fsm.Reset(); // same boundary as RebuildTransport
            }
            DisposeOldTransport(oldCli, oldLifetime);
            _registry.SetServerConnected(false);
        }

        /// <summary>
        /// Atomically nulls the transport fields, detaches event handlers, and
        /// cancels the lifetime CTS. Returns the captured references so the
        /// caller can dispose them OUTSIDE _gate.
        /// </summary>
        private void DetachAndCaptureTransport_NoLock(
            out LmsCliClient oldCli, out CancellationTokenSource oldLifetime)
        {
            oldLifetime = _lifetime;
            oldCli = _cli;
            var oldStateHandler = _cliStateHandler;
            var oldAuthHandler = _cliAuthHandler;

            _lifetime = null;
            _cli = null;
            _cliStateHandler = null;
            _cliAuthHandler = null;
            _serverConnected = false;

            // Cancel oldLifetime first so the linked token inside oldCli is
            // signaled before the caller starts disposing.
            if (oldLifetime != null)
            {
                try { oldLifetime.Cancel(); }
                catch (ObjectDisposedException) { }
            }

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
            }
        }

        private static void DisposeOldTransport(LmsCliClient oldCli, CancellationTokenSource oldLifetime)
        {
            // Dispose CLI first so its inner Wait completes before the linked
            // source it depends on is released. Both calls are bounded (~3s and
            // O(1) respectively) and tolerant of double-cancel/double-dispose.
            if (oldCli != null) try { oldCli.Dispose(); } catch { }
            if (oldLifetime != null) try { oldLifetime.Dispose(); } catch { }
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
            // Timer.Dispose does not block in-flight callbacks. Guard against
            // the freeze pump firing after Dispose() began nulling fields.
            if (_disposed) return;
            try { _registry.SweepFrozenMetadata(MetadataFreezeTtl); }
            catch { }

            // Same 1s pump advances the elapsed position for playing players so
            // the Helper's time display counts up between status snapshots.
            try { _registry.TickPlayingPositions(); }
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

                case LmsMessageKind.PlayersResponse:
                    ApplyPlayersResponse(message.Tokens);
                    return;

                case LmsMessageKind.ListenAck:
                case LmsMessageKind.LoginAck:
                case LmsMessageKind.GlobalRaw:
                    return;
            }

            if (string.IsNullOrEmpty(message.Mac)) return;
            if (!_registry.IsBound(message.Mac)) return;

            switch (message.Kind)
            {
                case LmsMessageKind.StatusResponse:
                    ApplyStatusResponse(message.Mac, message.Tokens);
                    return;

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
                        _registry.NoteMetadata(message.Mac, song.Title, null, null, -1, -1, 0);
                        // Trigger a full status query so we pick up artist /
                        // album / track number / duration on the next CLI cycle.
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
                                _registry.NoteLifecycle(message.Mac, PlayerLifecycleState.Offline);
                                break;

                            case "forget":
                                // Per CLAUDE.md: mark InvalidSession, rediscover
                                // once, retry once. If still failing → Offline.
                                if (_registry.NoteInvalidSession(message.Mac))
                                {
                                    _ = SendCliForPlayer(message.Mac, LmsCliCommands.QueryStatus(message.Mac));
                                }
                                else
                                {
                                    _registry.NoteLifecycle(message.Mac, PlayerLifecycleState.Offline);
                                }
                                break;
                        }
                    }
                    break;
            }
        }

        // ===== FSM callback =====

        private void OnSmoothedServerConnectivity(LogicalConnectivityState committed)
        {
            // The FSM timer can fire this callback after Dispose() began. Without
            // this guard the body would touch _service, _registry, _cli, and the
            // reconcile timer in the middle of teardown.
            if (_disposed) return;

            var connected = committed == LogicalConnectivityState.Connected;
            _serverConnected = connected;
            ConnectionState = committed.ToString().ToUpperInvariant();

            try
            {
                NotifyPropertyChanged("lyrion:connectionState", new DriverEntityValue(ConnectionState));
            }
            catch { }

            _service.RaiseServerConnectivityChanged(connected);

            if (connected)
            {
                // Single INFO line per reconnect with registry size and CLI
                // lifecycle counters. Cheap to emit once per reconnect; lets
                // installers spot record accumulation (Unbind not called by a
                // consumer) or reconnect storms.
                LmsCliClient cliSnap;
                lock (_gate) { cliSnap = _cli; }
                var connects = cliSnap?.ConnectCount ?? 0;
                var disconnects = cliSnap?.DisconnectCount ?? 0;
                try
                {
                    _log("Lyrion Server: reconcile players=" + _registry.Count
                        + " connects=" + connects + " disconnects=" + disconnects);
                }
                catch { }

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

        /// <summary>
        /// Re-open the per-player subscribing status queries after a raw CLI
        /// reconnect. Deferred onto a timer rather than run inline: the state
        /// event fires on the CLI worker thread immediately after the socket
        /// comes up, before <c>login</c> / <c>listen 1</c> have been written,
        /// and <see cref="SendCliForPlayer"/> takes <c>_gate</c> — which the
        /// attaching thread may still hold. A short delay puts the queries
        /// safely after the connection preamble and off the CLI thread.
        /// </summary>
        private void ResubscribeBoundPlayers()
        {
            lock (_gate)
            {
                if (_disposed) return;
                try { _resubscribeTimer?.Dispose(); } catch { }
                _resubscribeTimer = new Timer(_ =>
                {
                    if (_disposed) return;
                    try
                    {
                        foreach (var mac in _registry.BoundMacs())
                        {
                            _ = SendCliForPlayer(mac, LmsCliCommands.QueryStatus(mac, StatusSubscribeSeconds));
                        }
                    }
                    catch { }
                }, null, TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan);
            }
        }

        private void ReconcileBoundPlayers()
        {
            var macs = _registry.BoundMacs();

            // CLAUDE.md §14: refresh the full player list first so playerids
            // are reconciled before the per-MAC status queries start
            // overwriting cached records.
            SendCliLineSync(LmsCliCommands.QueryPlayers(0, 999));

            foreach (var mac in macs)
            {
                // Open a subscribing status query: the prior subscription died
                // with the old CLI connection, so this both re-syncs now and
                // keeps pushing full status on every subsequent change.
                _ = SendCliForPlayer(mac, LmsCliCommands.QueryStatus(mac, StatusSubscribeSeconds));
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
                    if (_disposed) return;
                    try { _registry.RepublishAll(macs); }
                    catch { }
                }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
            }
        }

        private void OnPlayerBound(string mac)
        {
            // If the server is already connected when a Source/Helper/Receiver
            // driver binds, immediately request a fresh status for that MAC.
            if (string.IsNullOrEmpty(mac)) return;
            if (_serverConnected)
            {
                // Open a subscribing status query so this player keeps pushing
                // full status (power/mode/metadata) on every change from now on.
                _ = SendCliForPlayer(mac, LmsCliCommands.QueryStatus(mac, StatusSubscribeSeconds));
            }
        }

        // ===== CLI send helpers =====

        private bool SendCliLineSync(string line)
        {
            // Snapshot _cli and _lifetime atomically under _gate so a concurrent
            // teardown cannot null _lifetime between the two reads (which would
            // produce CancellationToken.None and leave the send uncancellable).
            LmsCliClient cli;
            CancellationToken token;
            lock (_gate)
            {
                cli = _cli;
                if (cli == null) return false;
                token = _lifetime?.Token ?? CancellationToken.None;
            }
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
            LmsCliClient cli;
            CancellationToken token;
            lock (_gate)
            {
                cli = _cli;
                if (cli == null) return Task.FromResult(false);
                token = _lifetime?.Token ?? CancellationToken.None;
            }
            var send = cli.SendLineAsync(line, token);
            send.ContinueWith(
                t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return send;
        }

        // ===== Diagnostic state =====

        private void ApplyStatusResponse(string mac, string[] tokens)
        {
            // Status responses echo the command prefix as the first few tokens
            // (e.g. "<mac> status - 1 ..."), followed by key:value pairs. We
            // start scanning from index 2 to skip the MAC and "status" tokens.
            var kv = LmsCliParser.ExtractKeyValues(tokens, 2);

            // Lifecycle. A status reply proves the server knows the player,
            // not that the player is reachable: the subscription keeps pushing
            // keep-alives for a client that has disconnected, and those carry
            // player_connected:0. Honour it when present; treat absence as
            // Online (older/odd replies) so a missing key can never strand a
            // player as unavailable.
            var online = !kv.TryGetValue("player_connected", out var connectedStr) || connectedStr != "0";
            _registry.NoteLifecycle(mac, online ? PlayerLifecycleState.Online : PlayerLifecycleState.Offline);

            // Power BEFORE mode. NotePlaybackState's playback-derived power
            // raise is a fallback for players with no explicit power state; if
            // it ran first, a reply carrying mode:play together with power:0
            // (LMS pauses ~1 ms after "power 0", and a push can land between;
            // synced slaves; a player started server-side while off) raised
            // PowerStateChanged(true) and then NoteExplicitPower flipped it
            // straight back — an ON/OFF pair from one message, the 1.0.5
            // bounce-back class, repeated on every keep-alive while it held.
            // With the explicit value noted first, the derivation sees the
            // authoritative state when it runs.
            if (kv.TryGetValue("power", out var powerStr))
            {
                _registry.NoteExplicitPower(mac, powerStr == "1");
            }

            // Playback mode
            if (kv.TryGetValue("mode", out var mode))
            {
                switch (mode)
                {
                    case "play":
                        _registry.NotePlaybackState(mac, LyrionPlaybackState.Playing);
                        break;
                    case "pause":
                        _registry.NotePlaybackState(mac, LyrionPlaybackState.Paused);
                        break;
                    case "stop":
                        _registry.NotePlaybackState(mac, LyrionPlaybackState.Stopped);
                        break;
                }
            }

            // Volume — the status response uses "mixer volume" as two tokens
            // that get merged by the CLI into a single "mixer volume:NN" token,
            // but ExtractKeyValues sees the key as "mixer volume". LMS also
            // sometimes returns it simply as "volume" depending on the tags
            // requested, so we check both.
            string volStr = null;
            if (!kv.TryGetValue("mixer volume", out volStr))
            {
                kv.TryGetValue("volume", out volStr);
            }
            if (volStr != null && int.TryParse(volStr, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var vol))
            {
                _registry.NoteVolume(mac, vol);
            }

            // Mute — not always present in every status response
            // Not a standard tag in "status - 1 tags:..." but may appear via
            // "mixer muting" prefset. We handle it if present.

            // Shuffle
            if (kv.TryGetValue("playlist shuffle", out var shuffleStr)
                || kv.TryGetValue("shuffle", out shuffleStr))
            {
                if (int.TryParse(shuffleStr, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var sh))
                {
                    _registry.NoteShuffle(mac, sh);
                }
            }

            // Repeat
            if (kv.TryGetValue("playlist repeat", out var repeatStr)
                || kv.TryGetValue("repeat", out repeatStr))
            {
                if (int.TryParse(repeatStr, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var rp))
                {
                    _registry.NoteRepeat(mac, rp);
                }
            }

            // Metadata — tags requested: g=genre, a=artist, l=album, d=duration,
            // t=tracknum, o=type, N=remote_title, r=bitrate, y=year, u=url. Cover
            // art is not displayable in Crestron Home for a third-party source,
            // so artwork tags are neither requested nor parsed.
            var title = TryGet(kv, "title") ?? TryGet(kv, "remote_title");
            var artist = TryGet(kv, "artist");
            var album = TryGet(kv, "album");

            // Track number is authoritative from a full status reply: absent
            // (e.g. radio streams) means "no track number", so default to 0.
            int trackNumber = 0;
            if (kv.TryGetValue("tracknum", out var tnStr) &&
                int.TryParse(tnStr, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var tnVal) && tnVal > 0)
            {
                trackNumber = tnVal;
            }

            int duration = -1;
            if (kv.TryGetValue("duration", out var durStr) &&
                double.TryParse(durStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var durVal))
            {
                duration = (int)durVal;
            }

            int position = -1;
            if (kv.TryGetValue("time", out var timeStr) &&
                double.TryParse(timeStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var timeVal))
            {
                position = (int)timeVal;
            }

            _registry.NoteMetadata(mac, title, artist, album, trackNumber, duration, position);

            // Player name — shown as the source-name header in the Helper UI.
            // Change-gated in the registry, so a repeated name raises nothing.
            var playerName = TryGet(kv, "player_name");
            if (!string.IsNullOrEmpty(playerName))
            {
                _registry.NoteName(mac, playerName);
            }

            // Player capabilities — "can_seek" indicates a real player; LMS
            // also returns "player_connected", etc. We note canPowerOff based
            // on "canpoweroff" if present.
            if (kv.TryGetValue("canpoweroff", out var cpStr))
            {
                _registry.SetCapabilities(mac, cpStr == "1", null);
            }

            // Last, by design: only now has every field of this reply been
            // noted, so only now is it honest to say the player has been
            // observed. Consumers gate their bind-time force-publish on this
            // (LyrionPlayerSnapshot.IsObserved) — never on availability, which
            // flipped true at the top of this method before power was parsed.
            _registry.NoteStatusApplied(mac);
        }

        private void ApplyPlayersResponse(string[] tokens)
        {
            // Per CLAUDE.md §14, on reconnect we must re-resolve player ids
            // for every configured MAC. LMS returns a flat token stream
            // delimited by repeated "playerindex:N" markers; within each
            // block "playerid:<id>" identifies the player. For hardware
            // players the id IS the MAC, so any bound MAC that matches a
            // playerid is confirmed present on the server.
            if (tokens == null) return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            const string Prefix = "playerid:";
            for (var i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i];
                if (string.IsNullOrEmpty(t)) continue;
                if (!t.StartsWith(Prefix, StringComparison.Ordinal)) continue;

                var value = t.Substring(Prefix.Length);
                if (value.Length == 0) continue;

                var normalized = MacAddress.Normalize(value);
                if (normalized != null) seen.Add(normalized);

                if (_registry.IsBound(value))
                {
                    _registry.SetPlayerId(value, value);
                }
            }

            // Warn about any bound MAC the server did not report. The most
            // likely cause is a typo in the Source/Helper/Receiver driver's
            // configured MAC; without this log the installer sees the driver's
            // "Bound to MAC" success message and assumes everything is fine.
            var bound = _registry.BoundMacs();
            foreach (var mac in bound)
            {
                if (!seen.Contains(mac))
                {
                    _log("Lyrion Server WARNING: bound player " + mac + " not present on LMS (check MAC for typos)");
                }
            }
        }

        private static string TryGet(IDictionary<string, string> kv, string key)
        {
            return kv.TryGetValue(key, out var val) ? val : null;
        }

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
            // Trace.WriteLine (not Debug.WriteLine): the TRACE constant is
            // defined in both Debug and Release builds, so these calls are
            // compiled into production. Debug.WriteLine is stripped in Release
            // and would leave installers with no log output at all.
            return message =>
            {
                try { Trace.WriteLine("[Lyrion.Server " + DateTime.UtcNow.ToString("HH:mm:ss.fff") + "] " + message); }
                catch { }
            };
        }

        public override void Dispose()
        {
            if (_disposed) { base.Dispose(); return; }
            _disposed = true;

            // Tell consumers the server is gone BEFORE the service disappears:
            // this publishes the effective off/stopped edges and
            // AvailabilityChanged(false) for every player, so a Source that
            // was reporting ON is lowered now rather than left asserting a
            // dead server's last state — and so a replacement Lyrion Server's
            // blank record finds consumers already at off, where a bind-time
            // UpdatePower(false) is a no-op instead of a fabricated edge.
            // RebuildTransport and TeardownTransport already did this;
            // Dispose did not.
            try { _registry.SetServerConnected(false); } catch { }

            try { LyrionServerServiceRegistry.Unregister(_service); } catch { }

            try { _freezePump?.Dispose(); } catch { }
            _freezePump = null;

            Timer reconcile;
            Timer resubscribe;
            lock (_gate)
            {
                reconcile = _reconcileTimer;
                _reconcileTimer = null;
                resubscribe = _resubscribeTimer;
                _resubscribeTimer = null;
            }
            try { reconcile?.Dispose(); } catch { }
            try { resubscribe?.Dispose(); } catch { }

            try { _fsm.Dispose(); } catch { }

            // Detach and dispose using the same helper as RebuildTransport so
            // the lock window stays tiny and the ~3s CLI dispose wait happens
            // outside _gate. Dispose is expected to be synchronous; we accept
            // blocking the caller here but not concurrent SDK lock-takers.
            LmsCliClient cliToDispose;
            CancellationTokenSource ctsToDispose;
            lock (_gate) { DetachAndCaptureTransport_NoLock(out cliToDispose, out ctsToDispose); }
            DisposeOldTransport(cliToDispose, ctsToDispose);

            base.Dispose();
        }
    }
}
