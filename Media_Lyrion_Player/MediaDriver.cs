// ---------------------------------------------------------------------------
//  Media_Lyrion_Player - Lyrion Source (Driver 2 of 3)
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

namespace LyrionCommunity.Crestron.Lyrion.Media
{
    /// <summary>
    /// Per-room media source entity. Surfaces transport, now-playing,
    /// shuffle/repeat (boolean), and power. Never opens a socket to LMS.
    /// </summary>
    public sealed class MediaDriver : ReflectedAttributeDriverEntity, IDisposable
    {
        private readonly Action<string> _log;
        private readonly object _gate = new object();

        private string _configuredMac;
        private string _boundMac;
        private ILyrionGatewayService _gateway;
        private volatile bool _disposed;

        // ===== Public entity properties =====

        [EntityProperty(Id = "transport:playbackState")]
        public LyrionPlaybackState PlaybackState { get; private set; } = LyrionPlaybackState.Stopped;

        [EntityProperty(Id = "lyrion:available")]
        public bool Available { get; private set; }

        [EntityProperty(Id = "power:on")]
        public bool PowerOn { get; private set; }

        [EntityProperty(Id = "lyrion:shuffleEnabled")]
        public bool ShuffleEnabled { get; private set; }

        [EntityProperty(Id = "lyrion:repeatEnabled")]
        public bool RepeatEnabled { get; private set; }

        [EntityProperty(Id = "media:title")]
        public string Title { get; private set; } = string.Empty;

        [EntityProperty(Id = "media:artist")]
        public string Artist { get; private set; } = string.Empty;

        [EntityProperty(Id = "media:album")]
        public string Album { get; private set; } = string.Empty;

        [EntityProperty(Id = "media:artworkUrl")]
        public string ArtworkUrl { get; private set; } = string.Empty;

        [EntityProperty(Id = "media:durationSec")]
        public int DurationSec { get; private set; }

        [EntityProperty(Id = "media:elapsedSec")]
        public int ElapsedSec { get; private set; }

        public MediaDriver(DriverControllerCreationArgs args, DriverImplementationResources resources)
            : base(DriverController.RootControllerId)
        {
            _log = BuildLogger();

            var cfgArgs = DataDrivenConfigurationControllerArgs.FromResources(args, resources, ControllerId);
            ConfigurationController = new DelegateDataDrivenConfigurationController(
                cfgArgs,
                ApplyConfigurationItems,
                null,
                null);

            // Subscribe to the registry; the callback fires either now (if
            // the gateway is already registered) or once the gateway driver
            // registers itself.
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

                        _configuredMac = canon;
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
            // Guard against late delivery to a disposed driver: if the
            // gateway registers after we have already disposed, the
            // subscription callback would otherwise wire up handlers that
            // are never cleaned up and pin this driver alive.
            if (_disposed) return;
            lock (_gate)
            {
                if (_disposed) return;
                _gateway = service;
            }

            service.AvailabilityChanged += OnAvailabilityChanged;
            service.PowerStateChanged += OnPowerStateChanged;
            service.PlaybackStateChanged += OnPlaybackStateChanged;
            service.MetadataUpdated += OnMetadataUpdated;
            service.ShuffleChanged += OnShuffleChanged;
            service.RepeatChanged += OnRepeatChanged;

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
                _log("Media: Bound to MAC " + mac);

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
            UpdatePlaybackState(snap.PlaybackState);
            UpdateShuffle(snap.ShuffleEnabled);
            UpdateRepeat(snap.RepeatEnabled);
            UpdateMetadata(snap.Metadata);
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

        private void OnPlaybackStateChanged(string mac, LyrionPlaybackState state)
        {
            if (!IsMine(mac)) return;
            UpdatePlaybackState(state);
        }

        private void OnMetadataUpdated(string mac, LyrionMetadata meta)
        {
            if (!IsMine(mac)) return;
            UpdateMetadata(meta);
        }

        private void OnShuffleChanged(string mac, bool enabled)
        {
            if (!IsMine(mac)) return;
            UpdateShuffle(enabled);
        }

        private void OnRepeatChanged(string mac, bool enabled)
        {
            if (!IsMine(mac)) return;
            UpdateRepeat(enabled);
        }

        // ===== Commands =====

        [EntityCommand(Id = "transport:play")]
        public void Play() => InvokeOnGateway((svc, mac) => svc.Play(mac));

        [EntityCommand(Id = "transport:pause")]
        public void Pause() => InvokeOnGateway((svc, mac) => svc.Pause(mac));

        [EntityCommand(Id = "transport:stop")]
        public void Stop() => InvokeOnGateway((svc, mac) => svc.Stop(mac));

        [EntityCommand(Id = "transport:nextTrack")]
        public void Next() => InvokeOnGateway((svc, mac) => svc.Next(mac));

        [EntityCommand(Id = "transport:previousTrack")]
        public void Previous() => InvokeOnGateway((svc, mac) => svc.Previous(mac));

        [EntityCommand(Id = "transport:seek")]
        public void Seek(int positionSeconds) => InvokeOnGateway((svc, mac) => svc.Seek(mac, positionSeconds));

        [EntityCommand(Id = "lyrion:setShuffle")]
        public void SetShuffle(bool enabled) => InvokeOnGateway((svc, mac) => svc.SetShuffle(mac, enabled));

        [EntityCommand(Id = "lyrion:setRepeat")]
        public void SetRepeat(bool enabled) => InvokeOnGateway((svc, mac) => svc.SetRepeat(mac, enabled));

        [EntityCommand(Id = "power:on")]
        public void PowerOnCommand() => InvokeOnGateway((svc, mac) => svc.PowerOn(mac));

        [EntityCommand(Id = "power:off")]
        public void PowerOffCommand() => InvokeOnGateway((svc, mac) => svc.PowerOff(mac));

        [EntityCommand(Id = "power:toggle")]
        public void PowerToggle() => InvokeOnGateway((svc, mac) => svc.PowerToggle(mac));

        // ===== Property update helpers =====

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

        private void UpdatePlaybackState(LyrionPlaybackState value)
        {
            if (PlaybackState == value) return;
            PlaybackState = value;
            try { NotifyPropertyChanged("transport:playbackState", new DriverEntityValue((long)value)); } catch { }
        }

        private void UpdateShuffle(bool value)
        {
            if (ShuffleEnabled == value) return;
            ShuffleEnabled = value;
            try { NotifyPropertyChanged("lyrion:shuffleEnabled", new DriverEntityValue(value)); } catch { }
        }

        private void UpdateRepeat(bool value)
        {
            if (RepeatEnabled == value) return;
            RepeatEnabled = value;
            try { NotifyPropertyChanged("lyrion:repeatEnabled", new DriverEntityValue(value)); } catch { }
        }

        private void UpdateMetadata(LyrionMetadata meta)
        {
            meta = meta ?? LyrionMetadata.Empty;

            if (Title != meta.Title)
            {
                Title = meta.Title;
                try { NotifyPropertyChanged("media:title", new DriverEntityValue(Title)); } catch { }
            }
            if (Artist != meta.Artist)
            {
                Artist = meta.Artist;
                try { NotifyPropertyChanged("media:artist", new DriverEntityValue(Artist)); } catch { }
            }
            if (Album != meta.Album)
            {
                Album = meta.Album;
                try { NotifyPropertyChanged("media:album", new DriverEntityValue(Album)); } catch { }
            }
            if (ArtworkUrl != meta.ArtworkUrl)
            {
                ArtworkUrl = meta.ArtworkUrl;
                try { NotifyPropertyChanged("media:artworkUrl", new DriverEntityValue(ArtworkUrl)); } catch { }
            }
            if (DurationSec != meta.DurationSeconds)
            {
                DurationSec = meta.DurationSeconds;
                try { NotifyPropertyChanged("media:durationSec", new DriverEntityValue((long)DurationSec)); } catch { }
            }
            if (ElapsedSec != meta.PositionSeconds)
            {
                ElapsedSec = meta.PositionSeconds;
                try { NotifyPropertyChanged("media:elapsedSec", new DriverEntityValue((long)ElapsedSec)); } catch { }
            }
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
                try { Debug.WriteLine("[Lyrion.Source] " + msg); }
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
                try { svc.PlaybackStateChanged -= OnPlaybackStateChanged; } catch { }
                try { svc.MetadataUpdated -= OnMetadataUpdated; } catch { }
                try { svc.ShuffleChanged -= OnShuffleChanged; } catch { }
                try { svc.RepeatChanged -= OnRepeatChanged; } catch { }
                if (!string.IsNullOrEmpty(mac))
                {
                    try { svc.UnbindPlayer(mac); } catch { }
                }
            }

            base.Dispose();
        }
    }
}
