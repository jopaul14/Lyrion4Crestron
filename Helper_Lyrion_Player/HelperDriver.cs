// ---------------------------------------------------------------------------
//  Helper_Lyrion_Player - Lyrion Helper (Driver 3 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
//
//  Phase 1 status: skeleton placeholder. This class is a minimal Entity Model
//  driver that compiles and accepts MAC configuration. In Phase 4 it is
//  replaced with a RAD AExtensionDevice (DeviceType "Media Player",
//  IsExtensionDevice = true) that surfaces the full now-playing UI, transport
//  controls, shuffle, repeat, and seek.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Crestron.DeviceDrivers.EntityModel;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Helper
{
    public sealed class HelperDriver : ReflectedAttributeDriverEntity, IDisposable
    {
        private readonly Action<string> _log;
        private readonly object _gate = new object();

        private string _configuredMac;
        private string _boundMac;
        private ILyrionGatewayService _gateway;
        private volatile bool _disposed;

        public HelperDriver(DriverControllerCreationArgs args, DriverImplementationResources resources)
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
                        string rawMac = null;
                        if (values.TryGetValue("_Mac_", out var v) && v.HasValue)
                        {
                            rawMac = v.Value.GetValue<string>();
                        }

                        var canon = MacAddress.Normalize(rawMac);
                        if (canon == null)
                        {
                            var err = new Dictionary<string, string>(StringComparer.Ordinal);
                            err["_Mac_"] = "Enter a valid MAC address (form aa:bb:cc:dd:ee:ff).";
                            return new ConfigurationItemErrors(err, null);
                        }

                        lock (_gate) { _configuredMac = canon; }
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

        private void OnGatewayAvailable(ILyrionGatewayService service)
        {
            if (_disposed) return;

            lock (_gate)
            {
                if (_disposed) return;
                if (ReferenceEquals(_gateway, service)) return;
                _gateway = service;
                _boundMac = null;
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
                _log("Helper: Bound to MAC " + mac);
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

        private static Action<string> BuildLogger()
        {
            return msg =>
            {
                try { Trace.WriteLine("[Lyrion.Helper " + DateTime.UtcNow.ToString("HH:mm:ss.fff") + "] " + msg); }
                catch { }
            };
        }

        public override void Dispose()
        {
            if (_disposed) { base.Dispose(); return; }
            _disposed = true;

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

            if (svc != null && !string.IsNullOrEmpty(mac))
            {
                try { svc.UnbindPlayer(mac); } catch { }
            }

            base.Dispose();
        }
    }
}
