// ---------------------------------------------------------------------------
//  Receiver_Lyrion_Player - Lyrion Receiver (Driver 4 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Threading;
using Crestron.RAD.Common.Enums;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.DeviceTypes.RADAVReceiver;
using LyrionCommunity.Crestron.Lyrion.Service;
// Inside the driver the bare name "ReceiverProtocol" is the base class's
// PROPERTY (ABasicAVReceiver.ReceiverProtocol), so the class's constants are
// reached through this alias.
using Proto = LyrionCommunity.Crestron.Lyrion.Receiver.ReceiverProtocol;

namespace LyrionCommunity.Crestron.Lyrion.Receiver
{
    /// <summary>
    /// Optional per-room routing endpoint, surfaced to Crestron Home as a RAD
    /// AV Receiver: volume (0–100), mute, and power for one Lyrion player.
    /// Never opens a socket to LMS — every command goes to the Lyrion Server
    /// service and all feedback comes back from it. Same bind/apply/dispose
    /// choreography as SourceDriver; the rules are repeated where they apply.
    /// </summary>
    public class ReceiverDriver : ABasicAVReceiver, ICloudConnected
    {
        // Volume ramp: one step on press, then one step per interval until
        // release. 300 ms is comfortably above the CLI's turnaround and slow
        // enough that a short hold is a couple of steps, not a leap. The tick
        // cap is a fuse for a Release that never arrives (~12 s of ramp).
        private static readonly TimeSpan RampInterval = TimeSpan.FromMilliseconds(300);
        private const int RampMaxTicks = 40;

        private readonly Action<string> _log;
        private readonly object _gate = new object();

        // Serialises every read and write of the RAD-facing state (PowerIsOn,
        // VolumePercent, Muted, Connected): the bind-time snapshot read+apply,
        // each Lyrion Server event handler, the service swap on a Lyrion
        // Server reload, Connect(), the VolumeStep attribute write, Dispose's
        // unbind and the invalid-MAC unbind. Lock order: _applyGate, then
        // _gate; never the reverse. The registry raises events outside its
        // own lock, so holding this while calling into it cannot invert.
        private readonly object _applyGate = new object();

        // Last availability reported for the bound player, so Connect() can
        // restore it instead of forcing Connected=true over it. Starts true
        // (the framework's pre-bind default); driven false by a loss or by an
        // invalid-MAC unbind.
        private bool _lastAvailability = true;

        private ReceiverProtocol _protocol;
        private string _configuredMac;
        private string _boundMac;
        private int _volumeStep = Proto.DefaultVolumeStep;
        private ILyrionServerService _server;
        private volatile bool _disposed;

        private Timer _rampTimer;
        private int _rampDirection; // +1 up, -1 down, 0 idle
        private int _rampTicks;

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
            _protocol.VolumeReleaseRequested += OnVolumeReleaseRequested;
            ReceiverProtocol = _protocol;
            ReceiverProtocol.Initialize(AvrData);

            LyrionServerServiceRegistry.Subscribe(OnServerAvailable);
        }

        public override void Connect()
        {
            // The framework calls this at load and again after any change to
            // a RequiredForConnection attribute (the MAC). It must re-apply
            // the availability already learned from the Lyrion Server, not
            // force Connected=true over it — the registry is change-gated on
            // its own copy and would never send the loss again. Not
            // `!bound || available` (1.0.13): that read an UNBOUND driver as
            // connected and undid the invalid-MAC unbind. Under _applyGate
            // like every other write of Connected, so it cannot interleave
            // with a loss arriving on the CLI thread and leave a stale true.
            lock (_applyGate)
            {
                bool available;
                lock (_gate) { available = _lastAvailability; }
                Connected = available;
            }
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

        /// <summary>
        /// The installer cleared the MAC or typed something unparseable. It is
        /// an unbind, not a no-op: release the registry record, blank the
        /// whole view (fields first, then Connected — the registry's loss
        /// order) and log the one misconfiguration warning the PRD sanctions.
        /// With nothing bound there is no state to lower, but a NON-BLANK
        /// value still gets the warning: a typo at first setup is the one
        /// moment the installer is looking at the log, and through 1.0.14 it
        /// produced no line at all. An empty attribute at boot stays silent.
        /// </summary>
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
                if (previous == null)
                {
                    if (!string.IsNullOrWhiteSpace(rawMac))
                    {
                        _log("Receiver WARNING: player MAC '" + rawMac + "' is not valid; nothing bound");
                    }
                    return;
                }

                if (svc != null) { try { svc.UnbindPlayer(previous); } catch { } }
                UpdateVolume(0, force: true);
                UpdateMute(false, force: true);
                UpdatePower(false, force: true);
                UpdateAvailability(false);
                _log("Receiver WARNING: player MAC '" + (rawMac ?? string.Empty) + "' is not valid; unbound from " + previous);
            }
        }

        private void OnVolumeStepReceived(int step, string invalidRaw)
        {
            // Under _applyGate: TryBindToServer reads and re-publishes
            // _volumeStep under the same lock, so an attribute edit landing
            // during a Lyrion Server reload cannot leave the registry (and the
            // Helper's Vol+/- buttons) holding a different step than this
            // driver. Dropped by InvokeOnServer if not yet bound; the bind
            // publishes it then.
            lock (_applyGate)
            {
                _volumeStep = step;
                InvokeOnServer((svc, mac) => svc.SetVolumeStep(mac, step));
            }

            if (!string.IsNullOrWhiteSpace(invalidRaw))
            {
                _log("Receiver WARNING: volume step '" + invalidRaw + "' is not valid ("
                    + Proto.MinVolumeStep + "-" + Proto.MaxVolumeStep + "); using " + step);
            }
        }

        // ===== Lyrion Server binding =====

        private void OnServerAvailable(ILyrionServerService service)
        {
            // Invoked on initial subscription and again whenever the Lyrion
            // Server driver reloads and registers a fresh service. The whole
            // swap runs under _applyGate so it cannot interleave with a bind
            // already in flight on the OLD service: that bind either finishes
            // first (and the rebind below replaces what it applied) or sees
            // the new service and binds to it — never half of each, which
            // through 1.0.14 could force-apply a disposed registry's stale
            // snapshot and then leave it standing.
            if (_disposed) return;

            lock (_applyGate)
            {
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
        }

        private void TryBindToServer()
        {
            // The whole bind — commit _boundMac, unbind the previous MAC, bind,
            // publish the step, read the snapshot, apply it — runs under
            // _applyGate so no event handler, Dispose, MAC edit, or service
            // swap can interleave with it.
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
            // Two rules (RELEASE_NOTES 1.0.11–1.0.15):
            //
            // 1. Touch the fields ONLY for a snapshot the Lyrion Server has
            //    observed (IsObserved — a full status response applied; NOT
            //    IsAvailable). For an unobserved record touch nothing but
            //    Connected; an un-forced false still passes the change-gate
            //    when this driver holds ON. Since 1.0.15 a status reply
            //    carries mute too (the sign of the volume), so IsObserved
            //    vouches for all three fields here — through 1.0.14 it did
            //    not vouch for mute, and the forced UpdateMute below could
            //    publish "unmuted" for a muted player.
            //
            // 2. Apply in the registry's own order, so Crestron Home never
            //    sees a field edge while this device reports itself
            //    disconnected: restore = Connected first, then fields;
            //    loss = fields first, then Connected.
            if (snap.IsAvailable) UpdateAvailability(true);
            if (snap.IsObserved)
            {
                UpdatePower(snap.IsPoweredOn, force: true);
                UpdateVolume(snap.Volume, force: true);
                UpdateMute(snap.Muted, force: true);
            }
            if (!snap.IsAvailable) UpdateAvailability(false);
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

        // Volume ramp (see ReceiverProtocol): a press sends one step at once
        // and arms the repeat; the release disarms it. A tap is press+release
        // back to back, so the timer never gets to fire and it stays exactly
        // one step. Each tick is a fresh LMS command, and each one comes back
        // as real VolumeChanged feedback, so nothing here fakes a level.
        private void OnVolumeUpRequested() => StartRamp(+1);
        private void OnVolumeDownRequested() => StartRamp(-1);
        private void OnVolumeReleaseRequested() => StopRamp();

        private void StartRamp(int direction)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _rampDirection = direction;
                _rampTicks = 0;
                if (_rampTimer == null)
                {
                    _rampTimer = new Timer(RampTick, null, RampInterval, RampInterval);
                }
                else
                {
                    _rampTimer.Change(RampInterval, RampInterval);
                }
            }
            SendVolumeStep(direction);
        }

        private void StopRamp()
        {
            lock (_gate)
            {
                _rampDirection = 0;
                try { _rampTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            }
        }

        private void RampTick(object state)
        {
            int direction;
            lock (_gate)
            {
                direction = _rampDirection;
                if (direction == 0 || _disposed) return;
                if (++_rampTicks >= RampMaxTicks)
                {
                    // Fuse: no Release arrived. Stop rather than ramp forever.
                    _rampDirection = 0;
                    try { _rampTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                    return;
                }
            }
            SendVolumeStep(direction);
        }

        private void SendVolumeStep(int direction)
        {
            var step = _volumeStep;
            if (direction > 0) InvokeOnServer((svc, mac) => svc.VolumeUp(mac, step));
            else InvokeOnServer((svc, mac) => svc.VolumeDown(mac, step));
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
                try { _protocol.VolumeReleaseRequested -= OnVolumeReleaseRequested; } catch { }
            }

            // Under _applyGate so an in-flight TryBindToServer either finishes
            // before we unbind (and we unbind what it bound) or sees
            // _boundMac already null and does nothing.
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

                    _rampDirection = 0;
                    try { _rampTimer?.Dispose(); } catch { }
                    _rampTimer = null;
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
