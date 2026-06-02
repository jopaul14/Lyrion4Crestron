// ---------------------------------------------------------------------------
//  Helper_Lyrion_Player - Lyrion Helper (Driver 3 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Diagnostics;
using Crestron.RAD.Common.Enums;
using Crestron.RAD.Common.Interfaces;
using Crestron.RAD.Common.Interfaces.ExtensionDevice;
using Crestron.RAD.DeviceTypes.ExtensionDevice;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Helper
{
    public class HelperDriver : AExtensionDevice, ICloudConnected
    {
        private const string CmdPlay = "Play";
        private const string CmdPause = "Pause";
        private const string CmdStop = "Stop";
        private const string CmdNext = "Next";
        private const string CmdPrevious = "Previous";
        private const string CmdTogglePlay = "TogglePlay";
        private const string CmdPowerOn = "PowerOn";
        private const string CmdPowerOff = "PowerOff";
        private const string CmdPowerToggle = "PowerToggle";

        private const string PropTitle = "Title";
        private const string PropArtist = "Artist";
        private const string PropAlbum = "Album";
        private const string PropArtwork = "ArtworkUrl";
        private const string PropElapsed = "Elapsed";
        private const string PropDuration = "Duration";
        private const string PropPlaybackState = "PlaybackState";
        private const string PropPlaybackIcon = "PlaybackIcon";
        private const string PropShuffle = "Shuffle";
        private const string PropRepeat = "Repeat";
        private const string PropPower = "Power";
        private const string PropPowerIcon = "PowerIcon";
        private const string PropTileStatus = "TileStatus";

        private const string IconPlay = "icPlay";
        private const string IconPause = "icPause";
        private const string IconPowerOn = "icPowerRegular";
        private const string IconPowerOff = "icPowerDisabled";

        private readonly Action<string> _log;
        private readonly object _gate = new object();

        private HelperProtocol _protocol;
        private string _configuredMac;
        private string _boundMac;
        private ILyrionGatewayService _gateway;
        private volatile bool _disposed;

        private PropertyValue<string> _titleProp;
        private PropertyValue<string> _artistProp;
        private PropertyValue<string> _albumProp;
        private PropertyValue<string> _artworkProp;
        private PropertyValue<string> _elapsedProp;
        private PropertyValue<string> _durationProp;
        private PropertyValue<string> _playbackStateProp;
        private PropertyValue<string> _playbackIconProp;
        private PropertyValue<bool> _shuffleProp;
        private PropertyValue<bool> _repeatProp;
        private PropertyValue<bool> _powerProp;
        private PropertyValue<string> _powerIconProp;
        private PropertyValue<string> _tileStatusProp;

        public HelperDriver()
        {
            _log = BuildLogger();
            CreateDeviceDefinition();
        }

        // ===== ICloudConnected =====

        public void Initialize()
        {
            ConnectionTransport = new HelperTransport();

            _protocol = new HelperProtocol(ConnectionTransport, Id);
            _protocol.MacAddressReceived += OnMacAddressReceived;
            DeviceProtocol = _protocol;
            DeviceProtocol.Initialize(DriverData);

            LyrionGatewayServiceRegistry.Subscribe(OnGatewayAvailable);
        }

        public override void Connect()
        {
            Connected = true;
        }

        // ===== Extension device definition =====

        private void CreateDeviceDefinition()
        {
            _titleProp = CreateProperty<string>(new PropertyDefinition(PropTitle, null, DevicePropertyType.String));
            _artistProp = CreateProperty<string>(new PropertyDefinition(PropArtist, null, DevicePropertyType.String));
            _albumProp = CreateProperty<string>(new PropertyDefinition(PropAlbum, null, DevicePropertyType.String));
            _artworkProp = CreateProperty<string>(new PropertyDefinition(PropArtwork, null, DevicePropertyType.String));
            _elapsedProp = CreateProperty<string>(new PropertyDefinition(PropElapsed, null, DevicePropertyType.String));
            _durationProp = CreateProperty<string>(new PropertyDefinition(PropDuration, null, DevicePropertyType.String));
            _playbackStateProp = CreateProperty<string>(new PropertyDefinition(PropPlaybackState, null, DevicePropertyType.String));
            _playbackIconProp = CreateProperty<string>(new PropertyDefinition(PropPlaybackIcon, null, DevicePropertyType.String));
            _shuffleProp = CreateProperty<bool>(new PropertyDefinition(PropShuffle, null, DevicePropertyType.Boolean));
            _repeatProp = CreateProperty<bool>(new PropertyDefinition(PropRepeat, null, DevicePropertyType.Boolean));
            _powerProp = CreateProperty<bool>(new PropertyDefinition(PropPower, null, DevicePropertyType.Boolean));
            _powerIconProp = CreateProperty<string>(new PropertyDefinition(PropPowerIcon, null, DevicePropertyType.String));
            _tileStatusProp = CreateProperty<string>(new PropertyDefinition(PropTileStatus, null, DevicePropertyType.String));
        }

        // ===== AExtensionDevice overrides =====

        protected override IOperationResult DoCommand(string command, string[] parameters)
        {
            switch (command)
            {
                case CmdPlay: InvokeOnGateway((svc, mac) => svc.Play(mac)); break;
                case CmdPause: InvokeOnGateway((svc, mac) => svc.Pause(mac)); break;
                case CmdStop: InvokeOnGateway((svc, mac) => svc.Stop(mac)); break;
                case CmdNext: InvokeOnGateway((svc, mac) => svc.Next(mac)); break;
                case CmdPrevious: InvokeOnGateway((svc, mac) => svc.Previous(mac)); break;
                case CmdTogglePlay:
                    InvokeOnGateway((svc, mac) =>
                    {
                        if (_playbackStateProp.Value == "Playing") svc.Pause(mac);
                        else svc.Play(mac);
                    });
                    break;
                case CmdPowerOn: InvokeOnGateway((svc, mac) => svc.PowerOn(mac)); break;
                case CmdPowerOff: InvokeOnGateway((svc, mac) => svc.PowerOff(mac)); break;
                case CmdPowerToggle: InvokeOnGateway((svc, mac) => svc.PowerToggle(mac)); break;
            }
            return new OperationResult(OperationResultCode.Success);
        }

        protected override IOperationResult SetDriverPropertyValue<T>(string propertyKey, T value)
        {
            switch (propertyKey)
            {
                case PropShuffle:
                    var shuffle = value as bool?;
                    if (shuffle.HasValue)
                        InvokeOnGateway((svc, mac) => svc.SetShuffle(mac, shuffle.Value));
                    return new OperationResult(OperationResultCode.Success);

                case PropRepeat:
                    var repeat = value as bool?;
                    if (repeat.HasValue)
                        InvokeOnGateway((svc, mac) => svc.SetRepeat(mac, repeat.Value));
                    return new OperationResult(OperationResultCode.Success);

                case PropPower:
                    var power = value as bool?;
                    if (power.HasValue)
                        InvokeOnGateway((svc, mac) =>
                        {
                            if (power.Value) svc.PowerOn(mac);
                            else svc.PowerOff(mac);
                        });
                    return new OperationResult(OperationResultCode.Success);
            }
            return new OperationResult(OperationResultCode.Error, "Unknown property.");
        }

        protected override IOperationResult SetDriverPropertyValue<T>(string objectId, string propertyKey, T value)
        {
            return new OperationResult(OperationResultCode.Error, "Not supported.");
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
                _log("Helper: Bound to MAC " + mac);

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
            UpdatePlayback(snap.PlaybackState);
            UpdateMetadata(snap.Metadata);
            UpdateShuffle(snap.ShuffleEnabled);
            UpdateRepeat(snap.RepeatEnabled);
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
            UpdatePower(isOn);
        }

        private void OnPlaybackStateChanged(string mac, LyrionPlaybackState state)
        {
            if (!IsMine(mac)) return;
            UpdatePlayback(state);
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

        // ===== Feedback into the extension device UI =====

        private void UpdateAvailability(bool isAvailable)
        {
            Connected = isAvailable;

            if (!isAvailable)
            {
                UpdatePlayback(LyrionPlaybackState.Stopped);
                UpdatePower(false);
            }
        }

        private void UpdatePower(bool isOn)
        {
            _powerProp.Value = isOn;
            RefreshTileStatus();
            Commit();
        }

        private void UpdatePlayback(LyrionPlaybackState state)
        {
            string label;
            string icon;
            // PlaybackIcon shows the *next* transport action (toggle affordance):
            // while playing, show Pause (tap = pause); while paused or stopped,
            // show Play (tap = play). The state label still reflects the actual
            // state for any text display bound to PlaybackState.
            switch (state)
            {
                case LyrionPlaybackState.Playing:
                    label = "Playing";
                    icon = IconPause;
                    break;
                case LyrionPlaybackState.Paused:
                    label = "Paused";
                    icon = IconPlay;
                    break;
                default:
                    label = "Stopped";
                    icon = IconPlay;
                    break;
            }
            _playbackStateProp.Value = label;
            _playbackIconProp.Value = icon;
            RefreshTileStatus();
            Commit();
        }

        private void UpdateMetadata(LyrionMetadata meta)
        {
            _titleProp.Value = meta.Title;
            _artistProp.Value = meta.Artist;
            _albumProp.Value = meta.Album;
            _artworkProp.Value = meta.ArtworkUrl;
            _elapsedProp.Value = FormatTime(meta.PositionSeconds);
            _durationProp.Value = FormatTime(meta.DurationSeconds);
            RefreshTileStatus();
            Commit();
        }

        // Surfaces power / playback state onto the room-page tile so the room
        // still shows whether the player is on or off even when the Source's
        // own tile is hidden from Available Sources. The visible Helper tile
        // therefore carries the on/off indication that the hidden Source no
        // longer provides. Does not Commit() — each caller commits once after
        // updating its own properties.
        private void RefreshTileStatus()
        {
            bool on = _powerProp.Value;
            _powerIconProp.Value = on ? IconPowerOn : IconPowerOff;

            string status;
            if (!on)
            {
                status = "Off";
            }
            else if (!string.IsNullOrEmpty(_titleProp.Value))
            {
                status = _titleProp.Value;
            }
            else
            {
                status = string.IsNullOrEmpty(_playbackStateProp.Value) ? "On" : _playbackStateProp.Value;
            }
            _tileStatusProp.Value = status;
        }

        private void UpdateShuffle(bool enabled)
        {
            _shuffleProp.Value = enabled;
            Commit();
        }

        private void UpdateRepeat(bool enabled)
        {
            _repeatProp.Value = enabled;
            Commit();
        }

        private static string FormatTime(int totalSeconds)
        {
            if (totalSeconds <= 0) return "0:00";
            var m = totalSeconds / 60;
            var s = totalSeconds % 60;
            return m.ToString() + ":" + s.ToString("D2");
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
                try { Trace.WriteLine("[Lyrion.Helper " + DateTime.UtcNow.ToString("HH:mm:ss.fff") + "] " + msg); }
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
