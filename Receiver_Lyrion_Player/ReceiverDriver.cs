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

        private ReceiverProtocol _protocol;
        private string _configuredMac;
        private string _boundMac;
        private int _volumeStep = DefaultVolumeStep;
        private ILyrionGatewayService _gateway;
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

            LyrionGatewayServiceRegistry.Subscribe(OnGatewayAvailable);
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
            TryBindToGateway();
        }

        private void OnVolumeStepReceived(int step)
        {
            _volumeStep = step;
        }

        // ===== Gateway binding =====

        private void OnGatewayAvailable(ILyrionGatewayService service)
        {
            if (_disposed) return;

            ILyrionGatewayService oldService;
            lock (_gate)
            {
                if (_disposed) return;
                if (ReferenceEquals(_gateway, service)) return;

                oldService = _gateway;
                _gateway = service;
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

            TryBindToGateway();
        }

        private void TryBindToGateway()
        {
            ILyrionGatewayService svc;
            string mac;
            string previousMac = null;
            lock (_gate)
            {
                svc = _gateway;
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
                _log("Receiver: Bound to MAC " + mac);

                if (svc.TryGetSnapshot(mac, out var snap))
                {
                    ApplySnapshot(snap);
                }
            }
        }

        private void ApplySnapshot(LyrionPlayerSnapshot snap)
        {
            UpdateAvailability(snap.IsAvailable);
            UpdatePower(snap.IsPoweredOn);
            UpdateVolume(snap.Volume);
            UpdateMute(snap.Muted);
        }

        // ===== Gateway event handlers =====

        private void OnAvailabilityChanged(string mac, bool isAvailable)
        {
            if (!IsMine(mac)) return;
            UpdateAvailability(isAvailable);
        }

        private void OnPowerStateChanged(string mac, bool isOn)
        {
            if (!IsMine(mac)) return;
            // DIAGNOSTIC (1.0.2): confirm the gateway power event actually
            // reaches this Receiver. If this line appears in the trace but the
            // Crestron Home power tile does not move, the gap is in the RAD AVR
            // power-feedback display contract, not our event wiring.
            _log("Receiver: gateway PowerStateChanged -> " + (isOn ? "ON" : "OFF") + " (mac " + mac + ")");
            UpdatePower(isOn);
        }

        private void OnVolumeChanged(string mac, int level)
        {
            if (!IsMine(mac)) return;
            UpdateVolume(level);
        }

        private void OnMuteChanged(string mac, bool muted)
        {
            if (!IsMine(mac)) return;
            UpdateMute(muted);
        }

        // ===== Feedback into the RAD framework =====

        private void UpdateAvailability(bool isAvailable)
        {
            Connected = isAvailable;
            SendStateChangeEvent(AvrStateObjects.Connection);
        }

        private void UpdatePower(bool isOn)
        {
            // No early-out on an unchanged value: always (re)assert the state and
            // emit so the bind-time snapshot reaches Crestron Home even when it
            // matches the framework default. Power changes are rare, so the
            // occasional redundant emit is harmless.
            var changed = PowerIsOn != isOn;
            PowerIsOn = isOn;
            SendStateChangeEvent(isOn ? AvrStateObjects.PoweredOn : AvrStateObjects.PoweredOff);
            SendStateChangeEvent(AvrStateObjects.Power);
            // DIAGNOSTIC (1.0.2): records that PowerIsOn was set and the RAD
            // state-change events were emitted.
            _log("Receiver: UpdatePower PowerIsOn=" + isOn + " changed=" + changed
                + " (emitted PoweredOn/Off + Power)");
        }

        private void UpdateVolume(int level)
        {
            if (level < 0) level = 0;
            if (level > 100) level = 100;
            var vol = (uint)level;
            if (VolumePercent == vol) return;
            VolumePercent = vol;
            SendStateChangeEvent(AvrStateObjects.Volume);
        }

        private void UpdateMute(bool muted)
        {
            if (Muted == muted) return;
            Muted = muted;
            SendStateChangeEvent(AvrStateObjects.Mute);
        }

        // ===== Commands (routed to the gateway via protocol events) =====

        private void OnPowerOnRequested() => InvokeOnGateway((svc, mac) => svc.PowerOn(mac));
        private void OnPowerOffRequested() => InvokeOnGateway((svc, mac) => svc.PowerOff(mac));
        private void OnPowerToggleRequested() => InvokeOnGateway((svc, mac) => svc.PowerToggle(mac));

        private void OnMuteOnRequested() => InvokeOnGateway((svc, mac) => svc.SetMute(mac, true));
        private void OnMuteOffRequested() => InvokeOnGateway((svc, mac) => svc.SetMute(mac, false));

        private void OnSetVolumeRequested(uint volume)
        {
            InvokeOnGateway((svc, mac) => svc.SetVolume(mac, (int)volume));
        }

        private void OnVolumeUpRequested()
        {
            var step = _volumeStep;
            InvokeOnGateway((svc, mac) => svc.VolumeUp(mac, step));
        }

        private void OnVolumeDownRequested()
        {
            var step = _volumeStep;
            InvokeOnGateway((svc, mac) => svc.VolumeDown(mac, step));
        }

        // ===== Helpers =====

        private bool IsMine(string mac)
        {
            string bound;
            lock (_gate) { bound = _boundMac; }
            return string.Equals(bound, mac, StringComparison.OrdinalIgnoreCase);
        }

        private void InvokeOnGateway(Action<ILyrionGatewayService, string> action)
        {
            ILyrionGatewayService svc;
            string mac;
            lock (_gate)
            {
                svc = _gateway;
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

            try { LyrionGatewayServiceRegistry.Unsubscribe(OnGatewayAvailable); } catch { }

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

            ILyrionGatewayService svc;
            string mac;
            lock (_gate)
            {
                svc = _gateway;
                mac = _boundMac;
                _gateway = null;
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

            base.Dispose();
        }
    }
}
