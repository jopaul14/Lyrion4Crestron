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

        [EntityPropertyMetadata(ExtensionUiProperty = true)]
        [EntityProperty(Id = "playbackState")]
        public LyrionPlaybackState PlaybackState { get; private set; } = LyrionPlaybackState.Stopped;

        [EntityPropertyMetadata(ExtensionUiProperty = true)]
        [EntityProperty(Id = "available")]
        public bool Available { get; private set; }

        [EntityPropertyMetadata(ExtensionUiProperty = true)]
        [EntityProperty(Id = "powerOn")]
        public bool PowerOn { get; private set; }

        [EntityPropertyMetadata(ExtensionUiProperty = true)]
        [EntityProperty(Id = "shuffleEnabled")]
        public bool ShuffleEnabled { get; private set; }

        [EntityPropertyMetadata(ExtensionUiProperty = true)]
        [EntityProperty(Id = "repeatEnabled")]
        public bool RepeatEnabled { get; private set; }

        [EntityPropertyMetadata(ExtensionUiProperty = true)]
        [EntityProperty(Id = "title")]
        public string Title { get; private set; } = string.Empty;

        [EntityPropertyMetadata(ExtensionUiProperty = true)]
        [EntityProperty(Id = "artist")]
        public string Artist { get; private set; } = string.Empty;

        [EntityPropertyMetadata(ExtensionUiProperty = true)]
        [EntityProperty(Id = "album")]
        public string Album { get; private set; } = string.Empty;

        [EntityPropertyMetadata(ExtensionUiProperty = true)]
        [EntityProperty(Id = "artworkUrl")]
        public string ArtworkUrl { get; private set; } = string.Empty;

        [EntityPropertyMetadata(ExtensionUiProperty = true)]
        [EntityProperty(Id = "durationSec")]
        public int DurationSec { get; private set; }

        [EntityPropertyMetadata(ExtensionUiProperty = true)]
        [EntityProperty(Id = "elapsedSec")]
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

            var uiDefinition = UiDefinitionProperty.LoadFromDirectoryIfExists(
                args.DriverDataDirectoryPath, resources.InitLogger, LogEntryLevel.Error);
            if (uiDefinition != null)
            {
                AddProperty(uiDefinition, UiDefinitionProperty.Name, uiDefinition);
            }

            AddCommand(this, ExtensionDoCommandExecutor.CommandName,
                new ExtensionDoCommandExecutor(GetCommand, resources.Logger));
            AddCommand(this, ExtensionSetPropertyValueExecutor.CommandName,
                new ExtensionSetPropertyValueExecutor(GetCommand, resources.Logger));

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
            // May be invoked multiple times: once on initial subscription, and
            // again whenever the Gateway driver is reloaded and a new service
            // is registered. On re-notification we must detach from the old
            // (now-dead) service and rebind to the new one.
            //
            // Guard against late delivery to a disposed driver. Swap inside
            // the lock so a concurrent Dispose() cannot complete between the
            // _disposed check and the event wiring.
            if (_disposed) return;

            ILyrionGatewayService oldService;
            lock (_gate)
            {
                if (_disposed) return;
                if (ReferenceEquals(_gateway, service)) return;

                oldService = _gateway;
                _gateway = service;

                // Clear _boundMac so TryBindToGateway does not short-circuit on
                // the "already bound to this MAC" guard — we need to rebind to
                // the new service instance.
                _boundMac = null;

                service.AvailabilityChanged += OnAvailabilityChanged;
                service.PowerStateChanged += OnPowerStateChanged;
                service.PlaybackStateChanged += OnPlaybackStateChanged;
                service.MetadataUpdated += OnMetadataUpdated;
                service.ShuffleChanged += OnShuffleChanged;
                service.RepeatChanged += OnRepeatChanged;
            }

            if (oldService != null)
            {
                try { oldService.AvailabilityChanged -= OnAvailabilityChanged; } catch { }
                try { oldService.PowerStateChanged -= OnPowerStateChanged; } catch { }
                try { oldService.PlaybackStateChanged -= OnPlaybackStateChanged; } catch { }
                try { oldService.MetadataUpdated -= OnMetadataUpdated; } catch { }
                try { oldService.ShuffleChanged -= OnShuffleChanged; } catch { }
                try { oldService.RepeatChanged -= OnRepeatChanged; } catch { }
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

        [EntityCommand(Id = "play")]
        public void Play() => InvokeOnGateway((svc, mac) => svc.Play(mac));

        [EntityCommand(Id = "pause")]
        public void Pause() => InvokeOnGateway((svc, mac) => svc.Pause(mac));

        [EntityCommand(Id = "stop")]
        public void Stop() => InvokeOnGateway((svc, mac) => svc.Stop(mac));

        [EntityCommand(Id = "nextTrack")]
        public void Next() => InvokeOnGateway((svc, mac) => svc.Next(mac));

        [EntityCommand(Id = "previousTrack")]
        public void Previous() => InvokeOnGateway((svc, mac) => svc.Previous(mac));

        [EntityCommand(Id = "seek")]
        public void Seek(int positionSeconds) => InvokeOnGateway((svc, mac) => svc.Seek(mac, positionSeconds));

        [EntityCommand(Id = "setShuffle")]
        public void SetShuffle(bool enabled) => InvokeOnGateway((svc, mac) => svc.SetShuffle(mac, enabled));

        [EntityCommand(Id = "setRepeat")]
        public void SetRepeat(bool enabled) => InvokeOnGateway((svc, mac) => svc.SetRepeat(mac, enabled));

        [EntityCommand(Id = "powerOn")]
        public void PowerOnCommand() => InvokeOnGateway((svc, mac) => svc.PowerOn(mac));

        [EntityCommand(Id = "powerOff")]
        public void PowerOffCommand() => InvokeOnGateway((svc, mac) => svc.PowerOff(mac));

        [EntityCommand(Id = "powerToggle")]
        public void PowerToggle() => InvokeOnGateway((svc, mac) => svc.PowerToggle(mac));

        // ===== Property update helpers =====

        private void UpdateAvailability(bool value)
        {
            if (Available == value) return;
            Available = value;
            try { NotifyPropertyChanged("available", new DriverEntityValue(value)); } catch { }
        }

        private void UpdatePowerOn(bool value)
        {
            if (PowerOn == value) return;
            PowerOn = value;
            try { NotifyPropertyChanged("powerOn", new DriverEntityValue(value)); } catch { }
        }

        private void UpdatePlaybackState(LyrionPlaybackState value)
        {
            if (PlaybackState == value) return;
            PlaybackState = value;
            try { NotifyPropertyChanged("playbackState", new DriverEntityValue((long)value)); } catch { }
        }

        private void UpdateShuffle(bool value)
        {
            if (ShuffleEnabled == value) return;
            ShuffleEnabled = value;
            try { NotifyPropertyChanged("shuffleEnabled", new DriverEntityValue(value)); } catch { }
        }

        private void UpdateRepeat(bool value)
        {
            if (RepeatEnabled == value) return;
            RepeatEnabled = value;
            try { NotifyPropertyChanged("repeatEnabled", new DriverEntityValue(value)); } catch { }
        }

        private void UpdateMetadata(LyrionMetadata meta)
        {
            meta = meta ?? LyrionMetadata.Empty;

            if (Title != meta.Title)
            {
                Title = meta.Title;
                try { NotifyPropertyChanged("title", new DriverEntityValue(Title)); } catch { }
            }
            if (Artist != meta.Artist)
            {
                Artist = meta.Artist;
                try { NotifyPropertyChanged("artist", new DriverEntityValue(Artist)); } catch { }
            }
            if (Album != meta.Album)
            {
                Album = meta.Album;
                try { NotifyPropertyChanged("album", new DriverEntityValue(Album)); } catch { }
            }
            if (ArtworkUrl != meta.ArtworkUrl)
            {
                ArtworkUrl = meta.ArtworkUrl;
                try { NotifyPropertyChanged("artworkUrl", new DriverEntityValue(ArtworkUrl)); } catch { }
            }
            if (DurationSec != meta.DurationSeconds)
            {
                DurationSec = meta.DurationSeconds;
                try { NotifyPropertyChanged("durationSec", new DriverEntityValue((long)DurationSec)); } catch { }
            }
            if (ElapsedSec != meta.PositionSeconds)
            {
                ElapsedSec = meta.PositionSeconds;
                try { NotifyPropertyChanged("elapsedSec", new DriverEntityValue((long)ElapsedSec)); } catch { }
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
            // Trace.WriteLine (not Debug.WriteLine): the TRACE constant is
            // defined in both Debug and Release builds, so these calls survive
            // Release compilation. Debug.WriteLine is stripped in Release.
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
