// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
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
using LyrionCommunity.Crestron.Lyrion.Definitions;
using LyrionCommunity.Crestron.Lyrion.Models;
using LyrionCommunity.Crestron.Lyrion.Protocol;
using LyrionCommunity.Crestron.Lyrion.Transport;

namespace LyrionCommunity.Crestron.Lyrion
{
    /// <summary>
    /// Root driver entity representing the Lyrion Media Server itself. Owns the
    /// CLI and JSON-RPC transport clients and the dictionary of player
    /// <see cref="ManagedDevices"/> that are exposed to Crestron.
    /// </summary>
    /// <remarks>
    /// Lifecycle:
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///       Crestron invokes <see cref="EntryPoint"/> which builds this entity.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       The Crestron host pushes the configured values from Driver.json
    ///       through <see cref="ApplyConfigurationItems"/> in one or more
    ///       batches.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Once all three required fields (<c>_Host_</c>, <c>_CliPort_</c>,
    ///       <c>_HttpPort_</c>) and the <c>_Players_</c> list are known, the
    ///       driver (re-)builds the transport clients and ManagedDevices.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Dispose tears down the transport clients and cancels all pending
    ///       async work. The <see cref="ManagedDevices"/> collection is treated
    ///       as immutable per the V2 Entity Model contract: we always
    ///       copy-modify-reassign rather than mutating in place.
    ///     </description>
    ///   </item>
    /// </list>
    /// Thread-safety: the CLI receive thread calls <see cref="OnCliMessageReceived"/>
    /// which does a quick lookup into <see cref="_playersByMac"/> (guarded by
    /// <see cref="_playersLock"/>) and forwards to the matching
    /// <see cref="PlayerEntity"/>. State changes on the root entity
    /// (<c>lyrion:connectionState</c>, <c>lyrion:serverVersion</c>) go through
    /// <see cref="NotifyPropertyChanged"/>.
    /// </remarks>
    public sealed class DriverMain : ReflectedAttributeDriverEntity, IDisposable
    {
        // ---------------------------------------------------------------- Fields

        private readonly Action<string> _log;
        private readonly object _transportLock = new object();
        private readonly object _playersLock = new object();

        // Map from MAC (lowercase, colon-separated) -> PlayerEntity for O(1)
        // dispatch of CLI notifications. Rebuilt on every config apply.
        private Dictionary<string, PlayerEntity> _playersByMac =
            new Dictionary<string, PlayerEntity>(StringComparer.OrdinalIgnoreCase);

        // Current transport clients. Rebuilt when connection settings change.
        private LmsCliClient _cli;
        private LmsJsonRpcClient _rpc;

        // Outer cancellation source controlling the lifetime of all transport
        // clients. Cancelled in Dispose.
        private CancellationTokenSource _lifetime = new CancellationTokenSource();

        // Latest known config values - retained so partial updates don't lose
        // previously-applied settings.
        private string _host;
        private int _cliPort = 9090;
        private int _httpPort = 9000;
        private string _username;
        private string _password;
        private string _rawPlayers = string.Empty;

        private volatile bool _disposed;

        // ---------------------------------------------------------------- Ctor

        public DriverMain(DriverControllerCreationArgs args, DriverImplementationResources resources)
            : base(DriverController.RootControllerId)
        {
            _log = BuildLogger();

            var cfgArgs = DataDrivenConfigurationControllerArgs.FromResources(
                args,
                resources,
                ControllerId);

            ConfigurationController = new DelegateDataDrivenConfigurationController(
                cfgArgs,
                ApplyConfigurationItems,
                null,
                null);

            ConnectionState = LmsConnectionState.Disconnected;
            ServerVersion = string.Empty;
            ManagedDevices = new Dictionary<string, PlatformManagedDevice>();
        }

        internal DataDrivenConfigurationController ConfigurationController { get; }

        // ---------------------------------------------------------------- Properties

        /// <summary>Current CLI connection state (Disconnected/Connecting/Connected/Faulted).</summary>
        [EntityProperty(Id = "lyrion:connectionState")]
        public LmsConnectionState ConnectionState { get; private set; }

        /// <summary>LMS server version string, populated from the <c>version</c> response.</summary>
        [EntityProperty(Id = "lyrion:serverVersion")]
        public string ServerVersion { get; private set; }

        /// <summary>Dictionary of managed player devices, keyed by ControllerId.</summary>
        [EntityProperty(
            Id = "platform:managedDevices",
            Type = DriverEntityValueType.DeviceDictionary,
            ItemTypeRef = "platform:ManagedDevice"
        )]
        public IDictionary<string, PlatformManagedDevice> ManagedDevices { get; private set; }

        // ---------------------------------------------------------------- Configuration

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
                        ReadIntIfPresent(values, "_CliPort_", ref _cliPort);
                        ReadIntIfPresent(values, "_HttpPort_", ref _httpPort);
                        ReadStringIfPresent(values, "_Username_", ref _username);
                        ReadStringIfPresent(values, "_Password_", ref _password);
                        ReadStringIfPresent(values, "_Players_", ref _rawPlayers);

                        if (!string.IsNullOrEmpty(_host) && (_cliPort <= 0 || _cliPort > 65535))
                        {
                            errors["_CliPort_"] = "CLI port must be between 1 and 65535.";
                        }

                        if (!string.IsNullOrEmpty(_host) && (_httpPort <= 0 || _httpPort > 65535))
                        {
                            errors["_HttpPort_"] = "HTTP port must be between 1 and 65535.";
                        }

                        // Validate player list (surfaces per-line parse errors
                        // without preventing partial apply).
                        IReadOnlyList<string> playerErrors;
                        var players = PlayerConfig.ParseList(_rawPlayers, out playerErrors);
                        if (playerErrors != null && playerErrors.Count > 0)
                        {
                            var message = string.Join(" | ", new List<string>(playerErrors).ToArray());
                            errors["_Players_"] = message;
                        }

                        if (errors.Count > 0)
                        {
                            return new ConfigurationItemErrors(errors, null);
                        }

                        // Apply the new settings only if we have everything we need.
                        if (!string.IsNullOrEmpty(_host))
                        {
                            RebuildTransportAndPlayers(players);
                        }

                        return null;
                    }

                case DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues:
                    {
                        // The host is walking the user back to a previous step;
                        // tear down the active transport until new values arrive.
                        if (values.ContainsKey("_Host_")
                            || values.ContainsKey("_CliPort_")
                            || values.ContainsKey("_HttpPort_")
                            || values.ContainsKey("_Username_")
                            || values.ContainsKey("_Password_")
                            || values.ContainsKey("_Players_"))
                        {
                            TeardownTransport();
                            TeardownPlayers();
                        }
                        return null;
                    }
            }

            return null;
        }

        private static void ReadStringIfPresent(
            IDictionary<string, DriverEntityValue?> values,
            string key,
            ref string target)
        {
            DriverEntityValue? value;
            if (values.TryGetValue(key, out value) && value.HasValue)
            {
                target = value.Value.GetValue<string>() ?? string.Empty;
            }
        }

        private static void ReadIntIfPresent(
            IDictionary<string, DriverEntityValue?> values,
            string key,
            ref int target)
        {
            DriverEntityValue? value;
            if (values.TryGetValue(key, out value) && value.HasValue)
            {
                // Numeric values come through as long; clamp to int range.
                var asLong = value.Value.GetValue<long>();
                if (asLong < int.MinValue) asLong = int.MinValue;
                if (asLong > int.MaxValue) asLong = int.MaxValue;
                target = (int)asLong;
            }
        }

        // ---------------------------------------------------------------- Transport + player wiring

        private void RebuildTransportAndPlayers(IReadOnlyList<PlayerConfig> players)
        {
            lock (_transportLock)
            {
                if (_disposed)
                {
                    return;
                }

                // Rebuild transports first so new player entities get the new delegates.
                TeardownTransport();

                var lifetime = new CancellationTokenSource();
                _lifetime = lifetime;

                var cli = new LmsCliClient(_host, _cliPort, _username, _password, _log);
                cli.MessageReceived += OnCliMessageReceived;
                cli.ConnectionStateChanged += OnConnectionStateChanged;

                var rpc = new LmsJsonRpcClient(
                    _host,
                    _httpPort,
                    _username,
                    _password,
                    TimeSpan.FromSeconds(15),
                    _log);

                _cli = cli;
                _rpc = rpc;

                RebuildPlayers(players);

                // Kick off the CLI worker. Fire-and-forget: StartAsync returns a
                // completed task once the worker is scheduled.
                _ = cli.StartAsync(lifetime.Token);
            }
        }

        private void TeardownTransport()
        {
            CancellationTokenSource oldLifetime;
            LmsCliClient oldCli;
            LmsJsonRpcClient oldRpc;

            lock (_transportLock)
            {
                oldLifetime = _lifetime;
                oldCli = _cli;
                oldRpc = _rpc;

                _lifetime = new CancellationTokenSource();
                _cli = null;
                _rpc = null;
            }

            if (oldCli != null)
            {
                oldCli.MessageReceived -= OnCliMessageReceived;
                oldCli.ConnectionStateChanged -= OnConnectionStateChanged;

                try
                {
                    // Fire-and-forget shutdown - config apply runs on the SDK
                    // thread and we don't want to block it on socket teardown.
                    var stop = oldCli.StopAsync(TimeSpan.FromSeconds(2));
                    _ = stop.ContinueWith(
                        _ =>
                        {
                            try { oldCli.Dispose(); } catch { /* ignore */ }
                        },
                        TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    _log("DriverMain: CLI teardown threw: " + ex.Message);
                }
            }

            if (oldLifetime != null)
            {
                try
                {
                    oldLifetime.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore.
                }

                // Dispose asynchronously so we don't race with in-flight work.
                _ = Task.Run(() =>
                {
                    try { oldLifetime.Dispose(); } catch { /* ignore */ }
                });
            }

            // RPC client is stateless; nothing to dispose.
            _ = oldRpc;

            UpdateConnectionState(LmsConnectionState.Disconnected);
            UpdateServerVersion(string.Empty);
        }

        private void RebuildPlayers(IReadOnlyList<PlayerConfig> players)
        {
            // Build a fresh map and wrapper collection; the ManagedDevices
            // collection is treated as immutable per CLAUDE.md.
            var newPlayersByMac = new Dictionary<string, PlayerEntity>(
                players != null ? players.Count : 0,
                StringComparer.OrdinalIgnoreCase);
            var newManagedDevices = new Dictionary<string, PlatformManagedDevice>(
                players != null ? players.Count : 0,
                StringComparer.Ordinal);
            var controllersToAdd = new List<ConfigurableDriverEntity>(
                players != null ? players.Count : 0);

            if (players != null)
            {
                foreach (var cfg in players)
                {
                    var mac = cfg.MacAddress;
                    var entity = new PlayerEntity(
                        cfg.ControllerId,
                        mac,
                        SendCliForPlayer,
                        SendRpcForPlayer,
                        _log);

                    newPlayersByMac[mac] = entity;
                    controllersToAdd.Add(new ConfigurableDriverEntity(entity.ControllerId, entity, null));

                    newManagedDevices[entity.ControllerId] = new PlatformManagedDevice(
                        DeviceUxCategory.Speaker,
                        cfg.FriendlyName,
                        "Lyrion Community",
                        "Player",
                        mac);
                }
            }

            // Determine the diff vs. the previous set so we can un-register
            // old sub-controllers.
            Dictionary<string, PlayerEntity> oldPlayersByMac;
            IDictionary<string, PlatformManagedDevice> oldManagedDevices;
            lock (_playersLock)
            {
                oldPlayersByMac = _playersByMac;
                oldManagedDevices = ManagedDevices;

                _playersByMac = newPlayersByMac;
                ManagedDevices = newManagedDevices;
            }

            // Ids to remove = anything that was present before but isn't now.
            var controllersToRemove = new List<string>();
            if (oldManagedDevices != null)
            {
                foreach (var kvp in oldManagedDevices)
                {
                    if (!newManagedDevices.ContainsKey(kvp.Key))
                    {
                        controllersToRemove.Add(kvp.Key);
                    }
                }
            }

            try
            {
                UpdateSubControllers(
                    controllersToAdd.Count > 0 ? controllersToAdd : null,
                    controllersToRemove.Count > 0 ? controllersToRemove : null);
            }
            catch (Exception ex)
            {
                _log("DriverMain: UpdateSubControllers threw: " + ex.Message);
            }

            // Publish the new collection so the application sees the updates.
            try
            {
                NotifyPropertyChanged(
                    "platform:managedDevices",
                    CreateValueForEntries(ManagedDevices));
            }
            catch (Exception ex)
            {
                _log("DriverMain: NotifyPropertyChanged(managedDevices) threw: " + ex.Message);
            }

            _ = oldPlayersByMac; // No explicit disposal needed; no owned resources per player.
        }

        private void TeardownPlayers()
        {
            lock (_playersLock)
            {
                _playersByMac = new Dictionary<string, PlayerEntity>(StringComparer.OrdinalIgnoreCase);
            }

            var toRemove = new List<string>();
            var previousDevices = ManagedDevices;
            if (previousDevices != null)
            {
                foreach (var kvp in previousDevices)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            ManagedDevices = new Dictionary<string, PlatformManagedDevice>();

            if (toRemove.Count > 0)
            {
                try
                {
                    UpdateSubControllers(null, toRemove);
                }
                catch (Exception ex)
                {
                    _log("DriverMain: UpdateSubControllers (removal) threw: " + ex.Message);
                }
            }

            try
            {
                NotifyPropertyChanged(
                    "platform:managedDevices",
                    CreateValueForEntries(ManagedDevices));
            }
            catch (Exception ex)
            {
                _log("DriverMain: NotifyPropertyChanged(managedDevices, cleared) threw: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- Transport delegates

        /// <summary>Delegate used by <see cref="PlayerEntity"/> to post a CLI line.</summary>
        private Task<bool> SendCliForPlayer(string cliLine)
        {
            var cli = _cli;
            if (cli == null)
            {
                _log("DriverMain: dropped CLI send - no active transport: " + cliLine);
                return Task.FromResult(false);
            }

            var lifetime = _lifetime;
            var ct = lifetime != null ? lifetime.Token : CancellationToken.None;
            return cli.SendLineAsync(cliLine, ct);
        }

        /// <summary>Delegate used by <see cref="PlayerEntity"/> to POST a JSON-RPC request.</summary>
        private Task<LmsRpcResult> SendRpcForPlayer(string jsonBody, CancellationToken ct)
        {
            var rpc = _rpc;
            if (rpc == null)
            {
                return Task.FromResult(LmsRpcResult.Failure("RPC client not initialized."));
            }

            // Link caller token (if any) with the driver lifetime token so that
            // Dispose cancels any in-flight browse requests.
            var lifetime = _lifetime;
            if (lifetime == null)
            {
                return rpc.SendAsync(jsonBody, ct);
            }

            if (ct.CanBeCanceled)
            {
                var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
                return SendRpcWithLinkedToken(rpc, jsonBody, linked);
            }

            return rpc.SendAsync(jsonBody, lifetime.Token);
        }

        private static async Task<LmsRpcResult> SendRpcWithLinkedToken(
            LmsJsonRpcClient rpc,
            string jsonBody,
            CancellationTokenSource linked)
        {
            try
            {
                return await rpc.SendAsync(jsonBody, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                try { linked.Dispose(); } catch { /* ignore */ }
            }
        }

        // ---------------------------------------------------------------- CLI events

        private void OnCliMessageReceived(LmsMessage message)
        {
            if (message.Kind == LmsMessageKind.Empty)
            {
                return;
            }

            // Global notifications handled here. Player-scoped messages routed.
            switch (message.Kind)
            {
                case LmsMessageKind.ServerVersion:
                    if (message.Payload is string version)
                    {
                        UpdateServerVersion(version);
                    }
                    return;

                case LmsMessageKind.ListenAck:
                case LmsMessageKind.LoginAck:
                case LmsMessageKind.PlayersResponse:
                case LmsMessageKind.GlobalRaw:
                    return;
            }

            if (string.IsNullOrEmpty(message.Mac))
            {
                return;
            }

            PlayerEntity player;
            lock (_playersLock)
            {
                _playersByMac.TryGetValue(message.Mac, out player);
            }

            if (player == null)
            {
                // Notification for a player we weren't configured to expose;
                // ignore silently (LMS sends events for every known player).
                return;
            }

            try
            {
                player.ApplyPlayerMessage(message);
            }
            catch (Exception ex)
            {
                _log("DriverMain: ApplyPlayerMessage threw: " + ex.Message);
            }

            // When a player (re)connects to LMS, pull a fresh status snapshot
            // so we can backfill state that LMS only reports on full status.
            if (message.Kind == LmsMessageKind.Client && message.Payload is string sub)
            {
                if (sub == "new" || sub == "reconnect")
                {
                    _ = SendCliForPlayer(LmsCliCommands.QueryStatus(message.Mac));
                }
            }
        }

        private void OnConnectionStateChanged(LmsConnectionState newState)
        {
            UpdateConnectionState(newState);

            if (newState != LmsConnectionState.Connected)
            {
                return;
            }

            // On (re)connect, issue a status query for each known player so we
            // re-sync now-playing / volume / power after a drop.
            List<string> macs;
            lock (_playersLock)
            {
                macs = new List<string>(_playersByMac.Keys);
            }

            foreach (var mac in macs)
            {
                _ = SendCliForPlayer(LmsCliCommands.QueryStatus(mac));
            }
        }

        // ---------------------------------------------------------------- State helpers

        private void UpdateConnectionState(LmsConnectionState next)
        {
            if (ConnectionState == next)
            {
                return;
            }

            ConnectionState = next;

            try
            {
                NotifyPropertyChanged("lyrion:connectionState", new DriverEntityValue((long)next));
            }
            catch (Exception ex)
            {
                _log("DriverMain: NotifyPropertyChanged(connectionState) threw: " + ex.Message);
            }
        }

        private void UpdateServerVersion(string version)
        {
            version = version ?? string.Empty;
            if (string.Equals(ServerVersion, version, StringComparison.Ordinal))
            {
                return;
            }

            ServerVersion = version;

            try
            {
                NotifyPropertyChanged("lyrion:serverVersion", new DriverEntityValue(version));
            }
            catch (Exception ex)
            {
                _log("DriverMain: NotifyPropertyChanged(serverVersion) threw: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- Logging

        /// <summary>
        /// Build a lightweight logger. The V2 SDK exposes a richer
        /// <c>IComponentLogger</c> via <see cref="DispatchingDeviceController"/>,
        /// but this entity is constructed before that controller exists. For
        /// v1 we write through <see cref="Debug.WriteLine"/> which the Crestron
        /// host forwards to the driver diagnostics channel in development builds.
        /// </summary>
        private static Action<string> BuildLogger()
        {
            return message =>
            {
                try
                {
                    Debug.WriteLine("[Lyrion] " + message);
                }
                catch
                {
                    // Logging must never throw.
                }
            };
        }

        // ---------------------------------------------------------------- Dispose

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                TeardownTransport();
            }
            catch (Exception ex)
            {
                _log("DriverMain: Dispose TeardownTransport threw: " + ex.Message);
            }

            try
            {
                TeardownPlayers();
            }
            catch (Exception ex)
            {
                _log("DriverMain: Dispose TeardownPlayers threw: " + ex.Message);
            }
        }
    }
}
