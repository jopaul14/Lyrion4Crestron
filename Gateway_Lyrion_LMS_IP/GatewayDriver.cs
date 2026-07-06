// ---------------------------------------------------------------------------
//  Gateway_Lyrion_LMS_IP - Lyrion Server gateway driver (Driver 1 of 4)
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

        private readonly LyrionGatewayServiceImpl _service;
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
            LmsCliClient oldCli;
            CancellationTokenSource oldLifetime;
            lock (_gate)
            {
                if (_disposed) return;
                DetachAndCaptureTransport_NoLock(out oldCli, out oldLifetime);

                var lifetime = new CancellationTokenSource();
                _lifetime = lifetime;

                var cli = new LmsCliClient(_host, _cliPort, _username, _password, _log);
                _cliStateHandler = s => _fsm.OnRawTransition(s);
                _cliAuthHandler = msg => _log("Gateway ERROR auth: " + msg);

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
            lock (_gate) { DetachAndCaptureTransport_NoLock(out oldCli, out oldLifetime); }
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
                    _log("Gateway: reconcile players=" + _registry.Count
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

            // Lifecycle: if we got a status response the player is reachable.
            _registry.NoteLifecycle(mac, PlayerLifecycleState.Online);

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

            // Power
            if (kv.TryGetValue("power", out var powerStr))
            {
                _registry.NoteExplicitPower(mac, powerStr == "1");
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
                    _log("Gateway WARNING: bound player " + mac + " not present on LMS (check MAC for typos)");
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
                try { Trace.WriteLine("[Lyrion.Gateway " + DateTime.UtcNow.ToString("HH:mm:ss.fff") + "] " + message); }
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
