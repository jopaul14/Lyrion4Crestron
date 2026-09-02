// ---------------------------------------------------------------------------
//  Source_Lyrion_Player - Lyrion Source (Driver 2 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Diagnostics;
using Crestron.RAD.Common.Enums;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.DeviceTypes.BlurayPlayer;
using LyrionCommunity.Crestron.Lyrion.Service;
using PlayBackStatusEnum = Crestron.RAD.Common.Enums.PlayBackStatus;

namespace LyrionCommunity.Crestron.Lyrion.Source
{
    /// <summary>
    /// Per-room routable audio source, surfaced to Crestron Home as a RAD
    /// Bluray Player. Exposes only the transport and power controls the
    /// Bluray Player type supports natively (Play / Pause / Stop /
    /// ForwardSkip / ReverseSkip / Power). Never opens a socket to LMS — every
    /// command is forwarded to the Lyrion Server service, and all
    /// feedback (availability, power, playback) arrives from that service.
    /// Volume, metadata, shuffle, repeat, and seek live in the companion
    /// Helper and Receiver drivers, not here.
    /// </summary>
    public class SourceDriver : ABasicBlurayPlayer, ICloudConnected
    {
        private readonly Action<string> _log;
        private readonly object _gate = new object();

        private SourceProtocol _protocol;
        private string _configuredMac;
        private string _boundMac;
        private ILyrionServerService _server;
        private volatile bool _disposed;

        public SourceDriver()
        {
            _log = BuildLogger();
        }

        // ===== ICloudConnected =====

        public void Initialize()
        {
            ConnectionTransport = new SourceTransport();

            _protocol = new SourceProtocol(ConnectionTransport, Id);
            _protocol.MacAddressReceived += OnMacAddressReceived;
            _protocol.PowerOnRequested += OnPowerOnRequested;
            _protocol.PowerOffRequested += OnPowerOffRequested;
            _protocol.PowerToggleRequested += OnPowerToggleRequested;
            BlurayPlayerProtocol = _protocol;
            BlurayPlayerProtocol.Initialize(BlurayPlayerData);

            LyrionServerServiceRegistry.Subscribe(OnServerAvailable);
        }

        public override void Connect()
        {
            Connected = true;
        }

        // ===== Configuration =====

        private void OnMacAddressReceived(string rawMac)
        {
            var canon = MacAddress.Normalize(rawMac);
            if (canon == null) return;

            lock (_gate) { _configuredMac = canon; }
            TryBindToServer();
        }

        // ===== Lyrion Server binding =====

        private void OnServerAvailable(ILyrionServerService service)
        {
            // May be invoked more than once: on initial subscription and again
            // whenever the Lyrion Server driver reloads and registers a fresh service.
            // Detach from the old service and rebind to the new one.
            if (_disposed) return;

            ILyrionServerService oldService;
            lock (_gate)
            {
                if (_disposed) return;
                if (ReferenceEquals(_server, service)) return;

                oldService = _server;
                _server = service;
                _boundMac = null;

                service.AvailabilityChanged += OnAvailabilityChanged;
                service.PowerStateChanged += OnPowerStateChanged;
                service.PlaybackStateChanged += OnPlaybackStateChanged;
            }

            if (oldService != null)
            {
                try { oldService.AvailabilityChanged -= OnAvailabilityChanged; } catch { }
                try { oldService.PowerStateChanged -= OnPowerStateChanged; } catch { }
                try { oldService.PlaybackStateChanged -= OnPlaybackStateChanged; } catch { }
            }

            TryBindToServer();
        }

        private void TryBindToServer()
        {
            ILyrionServerService svc;
            string mac;
            string previousMac = null;
            lock (_gate)
            {
                svc = _server;
                mac = _configuredMac;
                if (svc == null || string.IsNullOrEmpty(mac) || string.Equals(_boundMac, mac, StringComparison.Ordinal))
                {
                    return;
                }
                if (_boundMac != null && !string.Equals(_boundMac, mac, StringComparison.Ordinal))
                {
                    previousMac = _boundMac;
                }
                _boundMac = mac;
            }

            if (previousMac != null)
            {
                svc.UnbindPlayer(previousMac);
            }

            if (svc.BindPlayer(mac))
            {
                _log("Source: Bound to MAC " + mac);

                if (svc.TryGetSnapshot(mac, out var snap))
                {
                    ApplySnapshot(snap);
                }
            }
        }

        private void ApplySnapshot(LyrionPlayerSnapshot snap)
        {
            // Force the emits: the bind-time snapshot must reach Crestron Home
            // even when a value matches the framework default and would
            // otherwise be swallowed by the change-gate.
            UpdateAvailability(snap.IsAvailable);
            UpdatePower(snap.IsPoweredOn, force: true);
            UpdatePlayback(snap.PlaybackState, force: true);
        }

        // ===== Lyrion Server event handlers =====

        private void OnAvailabilityChanged(string mac, bool isAvailable)
        {
            if (!IsMine(mac)) return;
            UpdateAvailability(isAvailable);
        }

        private void OnPowerStateChanged(string mac, bool isOn)
        {
            if (!IsMine(mac)) return;
            UpdatePower(isOn);
        }

        private void OnPlaybackStateChanged(string mac, LyrionPlaybackState state)
        {
            if (!IsMine(mac)) return;
            UpdatePlayback(state);
        }

        // ===== Feedback into the RAD framework =====

        private void UpdateAvailability(bool isAvailable)
        {
            Connected = isAvailable;
            SendStateChangeEvent(BlurayPlayerStateObjects.Connection);

            if (!isAvailable)
            {
                // When the player drops off the server, stop reporting stale
                // power/playback so the routing graph reflects reality.
                UpdatePlayback(LyrionPlaybackState.Stopped);
                UpdatePower(false);
            }
        }

        private void UpdatePower(bool isOn, bool force = false)
        {
            if (!force && PowerIsOn == isOn) return;
            PowerIsOn = isOn;
            SendStateChangeEvent(isOn ? BlurayPlayerStateObjects.PoweredOn : BlurayPlayerStateObjects.PoweredOff);
            SendStateChangeEvent(BlurayPlayerStateObjects.Power);
        }

        private void UpdatePlayback(LyrionPlaybackState state, bool force = false)
        {
            var mapped = Map(state);
            if (!force && PlayBackStatus == mapped) return;
            PlayBackStatus = mapped;
            SendStateChangeEvent(BlurayPlayerStateObjects.PlayBackStatus);
        }

        private static PlayBackStatusEnum Map(LyrionPlaybackState state)
        {
            switch (state)
            {
                case LyrionPlaybackState.Playing: return PlayBackStatusEnum.Play;
                case LyrionPlaybackState.Paused: return PlayBackStatusEnum.Pause;
                default: return PlayBackStatusEnum.Stop;
            }
        }

        // ===== Commands (routed to the Lyrion Server) =====

        // Transport commands are virtual on the driver and intercepted here.
        public override void Play() => InvokeOnServer((svc, mac) => svc.Play(mac));
        public override void Pause() => InvokeOnServer((svc, mac) => svc.Pause(mac));
        public override void Stop() => InvokeOnServer((svc, mac) => svc.Stop(mac));
        public override void ForwardSkip() => InvokeOnServer((svc, mac) => svc.Next(mac));
        public override void ReverseSkip() => InvokeOnServer((svc, mac) => svc.Previous(mac));

        // Power commands are non-virtual on the driver; they arrive via the
        // protocol's power events (wired up in Initialize).
        private void OnPowerOnRequested() => InvokeOnServer((svc, mac) => svc.PowerOn(mac));
        private void OnPowerOffRequested() => InvokeOnServer((svc, mac) => svc.PowerOff(mac));
        private void OnPowerToggleRequested() => InvokeOnServer((svc, mac) => svc.PowerToggle(mac));

        // ===== Helpers =====

        private bool IsMine(string mac)
        {
            string bound;
            lock (_gate) { bound = _boundMac; }
            return string.Equals(bound, mac, StringComparison.OrdinalIgnoreCase);
        }

        private void InvokeOnServer(Action<ILyrionServerService, string> action)
        {
            ILyrionServerService svc;
            string mac;
            lock (_gate)
            {
                svc = _server;
                mac = _boundMac;
            }
            if (svc == null || string.IsNullOrEmpty(mac)) return;
            try { action(svc, mac); } catch { }
        }

        private static Action<string> BuildLogger()
        {
            // Trace.WriteLine (not Debug.WriteLine): the TRACE constant is
            // defined in both Debug and Release builds, so these calls survive
            // Release compilation.
            return msg =>
            {
                try { Trace.WriteLine("[Lyrion.Source " + DateTime.UtcNow.ToString("HH:mm:ss.fff") + "] " + msg); }
                catch { }
            };
        }

        public override void Dispose()
        {
            if (_disposed) { base.Dispose(); return; }
            _disposed = true;

            try { LyrionServerServiceRegistry.Unsubscribe(OnServerAvailable); } catch { }

            if (_protocol != null)
            {
                try { _protocol.MacAddressReceived -= OnMacAddressReceived; } catch { }
                try { _protocol.PowerOnRequested -= OnPowerOnRequested; } catch { }
                try { _protocol.PowerOffRequested -= OnPowerOffRequested; } catch { }
                try { _protocol.PowerToggleRequested -= OnPowerToggleRequested; } catch { }
            }

            ILyrionServerService svc;
            string mac;
            lock (_gate)
            {
                svc = _server;
                mac = _boundMac;
                _server = null;
                _boundMac = null;
            }

            if (svc != null)
            {
                try { svc.AvailabilityChanged -= OnAvailabilityChanged; } catch { }
                try { svc.PowerStateChanged -= OnPowerStateChanged; } catch { }
                try { svc.PlaybackStateChanged -= OnPlaybackStateChanged; } catch { }
                if (!string.IsNullOrEmpty(mac))
                {
                    try { svc.UnbindPlayer(mac); } catch { }
                }
            }

            base.Dispose();
        }
    }
}
