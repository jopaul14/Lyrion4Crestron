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

        // Serialises every write of the RAD-facing state (PowerIsOn,
        // PlayBackStatus, Connected): the bind-time snapshot read+apply, each
        // Lyrion Server event handler, and Dispose's unbind. Without it a
        // CLI-thread event delivered between TryGetSnapshot and ApplySnapshot
        // was overwritten by the stale forced snapshot — and because the
        // registry only publishes on change, never corrected — and a Dispose
        // or MAC edit racing an in-flight bind could unbind a MAC this driver
        // had not yet bound (decrementing the Helper/Receiver's shared count)
        // and then leak the late bind. Lock order: _applyGate, then _gate;
        // never the reverse. The registry raises events outside its own lock,
        // so holding this while calling TryGetSnapshot cannot invert.
        private readonly object _applyGate = new object();

        // Last availability the Lyrion Server reported for the bound player,
        // so Connect() — which the framework re-runs after any MAC edit — can
        // restore it instead of forcing Connected=true over it. Starts true
        // (the framework's pre-bind default).
        private bool _lastAvailability = true;

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

            // Align the framework baseline with the registry's: RAD defaults
            // PlayBackStatus to NoDisc (enum 0), which is wrong for an audio
            // player at rest, while an unobserved registry record is Stopped.
            // Setting it once here means a bind never has to force-publish a
            // playback value for a player nobody has observed.
            PlayBackStatus = PlayBackStatusEnum.Stop;

            LyrionServerServiceRegistry.Subscribe(OnServerAvailable);
        }

        public override void Connect()
        {
            // The framework calls this at load and again after any change to
            // a RequiredForConnection attribute (the MAC). An unconditional
            // Connected=true here overrode the availability already learned
            // from the Lyrion Server, and the registry — change-gated on its
            // own unchanged copy — never sent AvailabilityChanged(false) again.
            // Not `!bound || available` (1.0.13): that read an UNBOUND driver as
            // connected and undid UnbindInvalidMac's "offline" as soon as the
            // framework re-ran this for the edit that caused the unbind.
            // _lastAvailability starts true (the pre-bind default) and is
            // driven false by a loss or by an invalid-MAC unbind.
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

        /// <summary>
        /// The installer cleared the MAC or typed something unparseable.
        /// Before 1.0.13 this was silently ignored and the driver stayed bound
        /// to — and kept driving — the previous player. Treat it as an unbind:
        /// release the registry record, report off/stopped then disconnected
        /// (the registry's loss order), and log the one misconfiguration
        /// warning the PRD sanctions. Silent when nothing was bound, so an
        /// unconfigured driver at boot does not log.
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
                if (previous == null) return;

                if (svc != null) { try { svc.UnbindPlayer(previous); } catch { } }
                UpdatePlayback(LyrionPlaybackState.Stopped, force: true);
                UpdatePower(false, force: true);
                UpdateAvailability(false);
                _log("Source WARNING: player MAC '" + (rawMac ?? string.Empty) + "' is not valid; unbound from " + previous);
            }
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
            // The whole bind — commit _boundMac, unbind the previous MAC, bind,
            // read the snapshot, apply it — runs under _applyGate, so no event
            // handler and no concurrent Dispose/MAC edit can interleave with
            // it (see the field comment).
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
                    _log("Source: Bound to MAC " + mac);

                    if (svc.TryGetSnapshot(mac, out var snap))
                    {
                        ApplySnapshot(snap);
                    }
                }
            }
        }

        private void ApplySnapshot(LyrionPlayerSnapshot snap)
        {
            // Two rules, both learned the hard way (RELEASE_NOTES 1.0.11–1.0.13):
            //
            // 1. Touch power/playback ONLY for a snapshot the Lyrion Server has
            //    observed (LyrionPlayerSnapshot.IsObserved — a full status
            //    response applied; NOT IsAvailable, which flips before power
            //    is parsed). For an unobserved record touch nothing but
            //    Connected. Not "call UpdatePower un-forced": an un-forced
            //    false still passes the change-gate when this driver holds
            //    ON, which is exactly the case on a Lyrion Server reload, and
            //    it published a fabricated PoweredOff that a "Power Is Off ->
            //    Room Off" mapping turned into a real power-off.
            //
            // 2. Apply in the registry's own order so Crestron Home never
            //    sees a field edge while this device reports itself
            //    disconnected: an available snapshot is a restore
            //    (availability first, then fields); an unavailable one is a
            //    loss (fields first — they are the effective off/stopped —
            //    then availability).
            //
            // No playback force for unobserved records: Initialize() aligns
            // the RAD baseline (NoDisc) to the registry's (Stopped) once.
            if (snap.IsAvailable)
            {
                UpdateAvailability(true);
                if (snap.IsObserved)
                {
                    UpdatePower(snap.IsPoweredOn, force: true);
                    UpdatePlayback(snap.PlaybackState, force: true);
                }
            }
            else
            {
                if (snap.IsObserved)
                {
                    UpdatePlayback(snap.PlaybackState, force: true);
                    UpdatePower(snap.IsPoweredOn, force: true);
                }
                UpdateAvailability(false);
            }
        }

        // ===== Lyrion Server event handlers =====

        // Each handler applies under _applyGate, and checks IsMine inside it
        // so a bind that completes concurrently cannot be raced by an event
        // for the MAC it is in the middle of binding.

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

        private void OnPlaybackStateChanged(string mac, LyrionPlaybackState state)
        {
            lock (_applyGate)
            {
                if (!IsMine(mac)) return;
                UpdatePlayback(state);
            }
        }

        // ===== Feedback into the RAD framework =====

        private void UpdateAvailability(bool isAvailable)
        {
            // Connection only. "Unavailable implies powered off and stopped"
            // is the registry's derivation (effective state): it publishes the
            // off/stopped edges BEFORE this event on loss and the on/playing
            // edges AFTER it on restore. Deriving it here as well (as this
            // driver did through 1.0.11) kept a second copy the registry could
            // not see, and the change-gate then swallowed the correction.
            lock (_gate) { _lastAvailability = isAvailable; }
            Connected = isAvailable;
            SendStateChangeEvent(BlurayPlayerStateObjects.Connection);
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
            }

            base.Dispose();
        }
    }
}
