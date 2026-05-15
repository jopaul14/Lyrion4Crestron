// ---------------------------------------------------------------------------
//  Volume_Lyrion_Player - Lyrion Receiver (Driver 3 of 3)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Volume
{
    /// <summary>
    /// Per-room AV receiver-style endpoint. Surfaces volume (0-100), mute,
    /// and power for a single MAC. Never opens a socket to LMS.
    /// </summary>
    public sealed class VolumeDriver : ReflectedAttributeDriverEntity, IDisposable
    {
        private const int DefaultVolumeStep = 2;

        private readonly Action<string> _log;
        private readonly object _gate = new object();

        private string _configuredMac;
        private string _boundMac;
        private int _volumeStep = DefaultVolumeStep;
        private ILyrionGatewayService _gateway;
        private volatile bool _disposed;

        // ===== Public properties =====

        [EntityProperty(Id = "audio:volume")]
        public int Volume { get; private set; }

        [EntityProperty(Id = "audio:muted")]
        public bool Muted { get; private set; }

        [EntityProperty(Id = "power:on")]
        public bool PowerOn { get; private set; }

        [EntityProperty(Id = "lyrion:available")]
        public bool Available { get; private set; }

        public VolumeDriver(DriverControllerCreationArgs args, DriverImplementationResources resources)
            : base(DriverController.RootControllerId)
        {
            _log = BuildLogger();

            var cfgArgs = DataDrivenConfigurationControllerArgs.FromResources(args, resources, ControllerId);
            ConfigurationController = new DelegateDataDrivenConfigurationController(
                cfgArgs,
                ApplyConfigurationItems,
                null,
                null);

            LyrionGatewayServiceRegistry.Subscribe(OnGatewayAvailable);
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

                        string rawMac = null;
                        if (values.TryGetValue("_Mac_", out var vMac) && vMac.HasValue)
                            rawMac = vMac.Value.GetValue<string>();

                        var canon = MacAddress.Normalize(rawMac);
                        if (canon == null)
                        {
                            errors["_Mac_"] = "Enter a valid MAC address (form aa:bb:cc:dd:ee:ff).";
                        }
                        else
                        {
                            _configuredMac = canon;
                        }

                        if (values.TryGetValue("_VolumeStep_", out var vStep) && vStep.HasValue)
                        {
                            var step = (int)vStep.Value.GetValue<long>();
                            if (step < 1 || step > 50)
                            {
                                errors["_VolumeStep_"] = "Volume step must be between 1 and 50.";
                            }
                            else
                            {
                                _volumeStep = step;
                            }
                        }

                        if (errors.Count > 0) return new ConfigurationItemErrors(errors, null);

                        TryBindToGateway();
                        return null;
                    }

                case DataDrivenConfigurationController.ApplyConfigurationAction.ClearValues:
                    if (values.ContainsKey("_Mac_"))
                    {
                        Unbind();
                    }
                    return null;
            }
            return null;
        }

        // ===== Gateway binding =====

        private void OnGatewayAvailable(ILyrionGatewayService service)
        {
            // Guard against late delivery to a disposed driver. Subscribe
            // inside the lock so a concurrent Dispose() cannot complete
            // between the _disposed check and the event wiring — that race
            // would otherwise leave handlers attached on a disposed driver
            // and pin it alive through the service's delegate chain.
            if (_disposed) return;
            lock (_gate)
            {
                if (_disposed) return;
                _gateway = service;

                service.AvailabilityChanged += OnAvailabilityChanged;
                service.PowerStateChanged += OnPowerStateChanged;
                service.VolumeChanged += OnVolumeChanged;
                service.MuteChanged += OnMuteChanged;
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
                _log("Volume: Bound to MAC " + mac);

                if (svc.TryGetSnapshot(mac, out var snap))
                {
                    ApplySnapshot(snap);
                }
            }
        }

        private void Unbind()
        {
            ILyrionGatewayService svc;
            string mac;
            lock (_gate)
            {
                svc = _gateway;
                mac = _boundMac;
                _boundMac = null;
            }
            if (svc != null && !string.IsNullOrEmpty(mac))
            {
                svc.UnbindPlayer(mac);
            }
        }

        private void ApplySnapshot(LyrionPlayerSnapshot snap)
        {
            UpdateAvailability(snap.IsAvailable);
            UpdatePowerOn(snap.IsPoweredOn);
            UpdateVolume(snap.Volume);
            UpdateMute(snap.Muted);
        }

        // ===== Event handlers =====

        private void OnAvailabilityChanged(string mac, bool isAvailable)
        {
            if (!IsMine(mac)) return;
            UpdateAvailability(isAvailable);
        }

        private void OnPowerStateChanged(string mac, bool isOn)
        {
            if (!IsMine(mac)) return;
            UpdatePowerOn(isOn);
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

        // ===== Commands =====

        [EntityCommand(Id = "audio:setVolume")]
        public void SetVolume(int level) => InvokeOnGateway((svc, mac) => svc.SetVolume(mac, level));

        [EntityCommand(Id = "audio:volumeUp")]
        public void VolumeUp() => InvokeOnGateway((svc, mac) => svc.VolumeUp(mac, _volumeStep));

        [EntityCommand(Id = "audio:volumeDown")]
        public void VolumeDown() => InvokeOnGateway((svc, mac) => svc.VolumeDown(mac, _volumeStep));

        [EntityCommand(Id = "audio:setMute")]
        public void SetMute(bool muted) => InvokeOnGateway((svc, mac) => svc.SetMute(mac, muted));

        [EntityCommand(Id = "audio:toggleMute")]
        public void ToggleMute() => InvokeOnGateway((svc, mac) => svc.SetMute(mac, !Muted));

        [EntityCommand(Id = "power:on")]
        public void PowerOnCommand() => InvokeOnGateway((svc, mac) => svc.PowerOn(mac));

        [EntityCommand(Id = "power:off")]
        public void PowerOffCommand() => InvokeOnGateway((svc, mac) => svc.PowerOff(mac));

        [EntityCommand(Id = "power:toggle")]
        public void PowerToggle() => InvokeOnGateway((svc, mac) => svc.PowerToggle(mac));

        // ===== Update helpers =====

        private void UpdateAvailability(bool value)
        {
            if (Available == value) return;
            Available = value;
            try { NotifyPropertyChanged("lyrion:available", new DriverEntityValue(value)); } catch { }
        }

        private void UpdatePowerOn(bool value)
        {
            if (PowerOn == value) return;
            PowerOn = value;
            try { NotifyPropertyChanged("power:on", new DriverEntityValue(value)); } catch { }
        }

        private void UpdateVolume(int value)
        {
            if (value < 0) value = 0;
            if (value > 100) value = 100;
            if (Volume == value) return;
            Volume = value;
            try { NotifyPropertyChanged("audio:volume", new DriverEntityValue((long)value)); } catch { }
        }

        private void UpdateMute(bool value)
        {
            if (Muted == value) return;
            Muted = value;
            try { NotifyPropertyChanged("audio:muted", new DriverEntityValue(value)); } catch { }
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
            // Trace.WriteLine (not Debug.WriteLine): the TRACE constant is
            // defined in both Debug and Release builds, so these calls survive
            // Release compilation. Debug.WriteLine is stripped in Release.
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

            // Remove our pending-registration callback so the registry does
            // not hold a reference to a disposed driver if the gateway has
            // not yet registered.
            try { LyrionGatewayServiceRegistry.Unsubscribe(OnGatewayAvailable); } catch { }

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
