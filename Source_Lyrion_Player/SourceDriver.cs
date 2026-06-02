// ---------------------------------------------------------------------------
//  Source_Lyrion_Player - Lyrion Source (Driver 2 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Diagnostics;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.DeviceTypes.BlurayPlayer;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Source
{
    /// <summary>
    /// Per-room routable audio source, surfaced to Crestron Home as a RAD
    /// Bluray Player. Exposes no user-facing controls: it is a silent routing
    /// object that declares one digital and one analog audio output and reports
    /// only its online/offline availability so Crestron Home can route it. All
    /// user controls — transport, power, shuffle, repeat, seek, now-playing
    /// metadata, and volume — live in the companion Helper and Receiver drivers.
    /// Never opens a socket to LMS; availability arrives from the Lyrion Server
    /// gateway service.
    /// </summary>
    public class SourceDriver : ABasicBlurayPlayer, ICloudConnected
    {
        private readonly Action<string> _log;
        private readonly object _gate = new object();

        private SourceProtocol _protocol;
        private string _configuredMac;
        private string _boundMac;
        private ILyrionGatewayService _gateway;
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
            BlurayPlayerProtocol = _protocol;
            BlurayPlayerProtocol.Initialize(BlurayPlayerData);

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

        // ===== Gateway binding =====

        private void OnGatewayAvailable(ILyrionGatewayService service)
        {
            // May be invoked more than once: on initial subscription and again
            // whenever the Gateway driver reloads and registers a fresh service.
            // Detach from the old service and rebind to the new one.
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
            }

            if (oldService != null)
            {
                try { oldService.AvailabilityChanged -= OnAvailabilityChanged; } catch { }
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
                _log("Source: Bound to MAC " + mac);

                if (svc.TryGetSnapshot(mac, out var snap))
                {
                    UpdateAvailability(snap.IsAvailable);
                }
            }
        }

        // ===== Gateway event handlers =====

        private void OnAvailabilityChanged(string mac, bool isAvailable)
        {
            if (!IsMine(mac)) return;
            UpdateAvailability(isAvailable);
        }

        // ===== Feedback into the RAD framework =====

        private void UpdateAvailability(bool isAvailable)
        {
            // The Source contributes no controls; the only state Crestron Home
            // needs from it is whether the routable source is online.
            Connected = isAvailable;
            SendStateChangeEvent(BlurayPlayerStateObjects.Connection);
        }

        // ===== Helpers =====

        private bool IsMine(string mac)
        {
            string bound;
            lock (_gate) { bound = _boundMac; }
            return string.Equals(bound, mac, StringComparison.OrdinalIgnoreCase);
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

            try { LyrionGatewayServiceRegistry.Unsubscribe(OnGatewayAvailable); } catch { }

            if (_protocol != null)
            {
                try { _protocol.MacAddressReceived -= OnMacAddressReceived; } catch { }
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
                if (!string.IsNullOrEmpty(mac))
                {
                    try { svc.UnbindPlayer(mac); } catch { }
                }
            }

            base.Dispose();
        }
    }
}
