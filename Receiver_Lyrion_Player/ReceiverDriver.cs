// ---------------------------------------------------------------------------
//  Receiver_Lyrion_Player - Lyrion Receiver (Driver 4 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Diagnostics;
using Crestron.RAD.Common.Enums;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.DeviceTypes.RADAVReceiver;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Receiver
{
    public class ReceiverDriver : ABasicAVReceiver, ICloudConnected
    {
        private const int DefaultVolumeStep = 2;

        private readonly Action<string> _log;
        private readonly object _gate = new object();

        // Serialises the bind-time snapshot read+apply, every event handler,
        // and Dispose's unbind — see the matching comment in SourceDriver.
        // Lock order: _applyGate, then _gate; never the reverse.
        private readonly object _applyGate = new object();

        // Last availability reported for the bound player, so Connect() can
        // restore it instead of forcing Connected=true over it. Starts true
        // (the framework's pre-bind default).
        private bool _lastAvailability = true;

        private ReceiverProtocol _protocol;
        private string _configuredMac;
        private string _boundMac;
        private int _volumeStep = DefaultVolumeStep;
        private ILyrionServerService _server;
        private volatile bool _disposed;

        public ReceiverDriver()
        {
            _log = BuildLogger();
        }

        // ===== ICloudConnected =====

        public void Initialize()
        {
            ConnectionTransport = new ReceiverTransport();

            _protocol = new ReceiverProtocol(ConnectionTransport, Id);
            _protocol.MacAddressReceived += OnMacAddressReceived;
            _protocol.VolumeStepReceived += OnVolumeStepReceived;
            _protocol.PowerOnRequested += OnPowerOnRequested;
            _protocol.PowerOffRequested += OnPowerOffRequested;
            _protocol.PowerToggleRequested += OnPowerToggleRequested;
            _protocol.MuteOnRequested += OnMuteOnRequested;
            _protocol.MuteOffRequested += OnMuteOffRequested;
            _protocol.SetVolumeRequested += OnSetVolumeRequested;
            _protocol.VolumeUpRequested += OnVolumeUpRequested;
            _protocol.VolumeDownRequested += OnVolumeDownRequested;
            ReceiverProtocol = _protocol;
            ReceiverProtocol.Initialize(AvrData);

            LyrionServerServiceRegistry.Subscribe(OnServerAvailable);
        }

        public override void Connect()
        {
            // Re-run by the framework after any MAC edit; must not override
            // the availability already learned (see SourceDriver.Connect).
            // See SourceDriver.Connect: _lastAvailability alone, never
            // `!bound || available`.
            bool available;
            lock (_gate) { available = _lastAvailability; }
            Connected = available;
        }

        // ===== Configuration =====

        private void OnMacAddressReceived(string rawMac)
        {
            var canon = MacAddress.Normalize(rawMac);
            if (canon == null)
            {
                UnbindInvalidMac(rawMac);
                return;
            }

            lock (_gate) { _configuredMac = canon; }
            TryBindToServer();
        }

        // A cleared or unparseable MAC is an unbind, not a no-op — see
        // SourceDriver.UnbindInvalidMac.
        private void UnbindInvalidMac(string rawMac)
        {
            lock (_applyGate)
            {
                ILyrionServerService svc;
                string previous;
                lock (_gate)
                {
                    _configuredMac = null;
                    previous = _boundMac;
                    _boundMac = null;
                    svc = _server;
                }
                if (previous == null) return;

                if (svc != null) { try { svc.UnbindPlayer(previous); } catch { } }
                // The whole view goes blank — this driver represents no player
                // now. Fields first, availability last (loss order).
                UpdateVolume(0, force: true);
                UpdateMute(false, force: true);
                UpdatePower(false, force: true);
                UpdateAvailability(false);
                _log("Receiver WARNING: player MAC '" + (rawMac ?? string.Empty) + "' is not valid; unbound from " + previous);
            }
        }

        private void OnVolumeStepReceived(int step)
        {
            _volumeStep = step;
            // Publish so other consumers (the Helper's Vol+/- buttons) match the
            // same step. Dropped if not yet bound; TryBindToServer re-publishes.
            InvokeOnServer((svc, mac) => svc.SetVolumeStep(mac, step));
        }

        // ===== Lyrion Server binding =====

        private void OnServerAvailable(ILyrionServerService service)
        {
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
                service.VolumeChanged += OnVolumeChanged;
                service.MuteChanged += OnMuteChanged;
            }

            if (oldService != null)
            {
                try { oldService.AvailabilityChanged -= OnAvailabilityChanged; } catch { }
                try { oldService.PowerStateChanged -= OnPowerStateChanged; } catch { }
                try { oldService.VolumeChanged -= OnVolumeChanged; } catch { }
                try { oldService.MuteChanged -= OnMuteChanged; } catch { }
            }

            TryBindToServer();
        }

        private void TryBindToServer()
        {
            // Whole bind under _applyGate — see SourceDriver.TryBindToServer.
            lock (_applyGate)
            {
                ILyrionServerService svc;
                string mac;
                string previousMac;
                lock (_gate)
                {
                    svc = _server;
                    mac = _configuredMac;
                    if (svc == null || string.IsNullOrEmpty(mac) || string.Equals(_boundMac, mac, StringComparison.Ordinal))
                    {
                        return;
                    }
                    previousMac = _boundMac; // null when nothing was bound
                    _boundMac = mac;
                }

                if (previousMac != null)
                {
                    svc.UnbindPlayer(previousMac);
                }

                if (svc.BindPlayer(mac))
                {
                    _log("Receiver: Bound to MAC " + mac);

                    // (Re)publish the configured step to the freshly-bound registry
                    // so consumers can match it after a Lyrion Server reload/reconnect.
                    svc.SetVolumeStep(mac, _volumeStep);

                    if (svc.TryGetSnapshot(mac, out var snap))
                    {
                        ApplySnapshot(snap);
                    }
                }
            }
        }

        private void ApplySnapshot(LyrionPlayerSnapshot snap)
        {
            // Same two rules as SourceDriver.ApplySnapshot: touch fields only
            // for an OBSERVED snapshot (never call UpdatePower for an
            // unobserved one — un-forced false still passes the gate when this
            // driver holds ON), and apply in the registry's order (restore:
            // availability then fields; loss: fields then availability).
            if (snap.IsAvailable)
            {
                UpdateAvailability(true);
                if (snap.IsObserved)
                {
                    UpdatePower(snap.IsPoweredOn, force: true);
                    UpdateVolume(snap.Volume, force: true);
                    UpdateMute(snap.Muted, force: true);
                }
            }
            else
            {
                if (snap.IsObserved)
                {
                    UpdatePower(snap.IsPoweredOn, force: true);
                    UpdateVolume(snap.Volume, force: true);
                    UpdateMute(snap.Muted, force: true);
                }
                UpdateAvailability(false);
            }
        }

        // ===== Lyrion Server event handlers =====

        // Handlers apply under _applyGate with IsMine checked inside it.

        private void OnAvailabilityChanged(string mac, bool isAvailable)
        {
            lock (_applyGate)
            {
                if (!IsMine(mac)) return;
                UpdateAvailability(isAvailable);
            }
        }

        private void OnPowerStateChanged(string mac, bool isOn)
        {
            lock (_applyGate)
            {
                if (!IsMine(mac)) return;
                UpdatePower(isOn);
            }
        }

        private void OnVolumeChanged(string mac, int level)
        {
            lock (_applyGate)
            {
                if (!IsMine(mac)) return;
                UpdateVolume(level);
            }
        }

        private void OnMuteChanged(string mac, bool muted)
        {
            lock (_applyGate)
            {
                if (!IsMine(mac)) return;
                UpdateMute(muted);
            }
        }

        // ===== Feedback into the RAD framework =====

        private void UpdateAvailability(bool isAvailable)
        {
            lock (_gate) { _lastAvailability = isAvailable; }
            Connected = isAvailable;
            SendStateChangeEvent(AvrStateObjects.Connection);
        }

        private void UpdatePower(bool isOn, bool force = false)
        {
            if (!force && PowerIsOn == isOn) return;
            PowerIsOn = isOn;
            SendStateChangeEvent(isOn ? AvrStateObjects.PoweredOn : AvrStateObjects.PoweredOff);
            SendStateChangeEvent(AvrStateObjects.Power);
        }

        private void UpdateVolume(int level, bool force = false)
        {
            if (level < 0) level = 0;
            if (level > 100) level = 100;
            var vol = (uint)level;
            if (!force && VolumePercent == vol) return;
            VolumePercent = vol;
            SendStateChangeEvent(AvrStateObjects.Volume);
        }

        private void UpdateMute(bool muted, bool force = false)
        {
            if (!force && Muted == muted) return;
            Muted = muted;
            SendStateChangeEvent(AvrStateObjects.Mute);
        }

        // ===== Commands (routed to the Lyrion Server via protocol events) =====

        private void OnPowerOnRequested() => InvokeOnServer((svc, mac) => svc.PowerOn(mac));
        private void OnPowerOffRequested() => InvokeOnServer((svc, mac) => svc.PowerOff(mac));
        private void OnPowerToggleRequested() => InvokeOnServer((svc, mac) => svc.PowerToggle(mac));

        private void OnMuteOnRequested() => InvokeOnServer((svc, mac) => svc.SetMute(mac, true));
        private void OnMuteOffRequested() => InvokeOnServer((svc, mac) => svc.SetMute(mac, false));

        private void OnSetVolumeRequested(uint volume)
        {
            InvokeOnServer((svc, mac) => svc.SetVolume(mac, (int)volume));
        }

        private void OnVolumeUpRequested()
        {
            var step = _volumeStep;
            InvokeOnServer((svc, mac) => svc.VolumeUp(mac, step));
        }

        private void OnVolumeDownRequested()
        {
            var step = _volumeStep;
            InvokeOnServer((svc, mac) => svc.VolumeDown(mac, step));
        }

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
            return msg =>
            {
                try { Trace.WriteLine("[Lyrion.Receiver " + DateTime.UtcNow.ToString("HH:mm:ss.fff") + "] " + msg); }
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
                try { _protocol.VolumeStepReceived -= OnVolumeStepReceived; } catch { }
                try { _protocol.PowerOnRequested -= OnPowerOnRequested; } catch { }
                try { _protocol.PowerOffRequested -= OnPowerOffRequested; } catch { }
                try { _protocol.PowerToggleRequested -= OnPowerToggleRequested; } catch { }
                try { _protocol.MuteOnRequested -= OnMuteOnRequested; } catch { }
                try { _protocol.MuteOffRequested -= OnMuteOffRequested; } catch { }
                try { _protocol.SetVolumeRequested -= OnSetVolumeRequested; } catch { }
                try { _protocol.VolumeUpRequested -= OnVolumeUpRequested; } catch { }
                try { _protocol.VolumeDownRequested -= OnVolumeDownRequested; } catch { }
            }

            lock (_applyGate)
            {
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
                    try { svc.VolumeChanged -= OnVolumeChanged; } catch { }
                    try { svc.MuteChanged -= OnMuteChanged; } catch { }
                    if (!string.IsNullOrEmpty(mac))
                    {
                        try { svc.UnbindPlayer(mac); } catch { }
                    }
                }
            }

            base.Dispose();
        }
    }
}
