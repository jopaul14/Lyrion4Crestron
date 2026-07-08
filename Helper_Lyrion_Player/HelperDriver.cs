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
        private const string CmdToggleRepeat = "ToggleRepeat";
        private const string CmdToggleShuffle = "ToggleShuffle";
        private const string CmdPowerOn = "PowerOn";
        private const string CmdPowerOff = "PowerOff";
        private const string CmdPowerToggle = "PowerToggle";
        private const string CmdVolumeUp = "VolumeUp";
        private const string CmdVolumeDown = "VolumeDown";
        private const string CmdToggleMute = "ToggleMute";

        private const string PropTitle = "Title";
        private const string PropArtist = "Artist";
        private const string PropAlbum = "Album";
        private const string PropElapsed = "Elapsed";
        private const string PropDuration = "Duration";
        private const string PropPlaybackState = "PlaybackState";
        private const string PropPlaybackIcon = "PlaybackIcon";
        private const string PropShuffle = "Shuffle";
        private const string PropRepeat = "Repeat";
        private const string PropPower = "Power";
        private const string PropPowerIcon = "PowerIcon";
        private const string PropTileStatus = "TileStatus";
        // Now-playing layout (see UiDefinition.xml).
        private const string PropSourceName = "SourceName";
        private const string PropTrackLine = "TrackLine";
        private const string PropByArtist = "ByArtist";
        private const string PropFromAlbum = "FromAlbum";
        private const string PropTimeText = "TimeText";
        private const string PropProgress = "Progress";
        private const string PropHasDuration = "HasDuration";
        private const string PropNoDuration = "NoDuration";
        private const string PropRepeatIcon = "RepeatIcon";
        private const string PropShuffleIcon = "ShuffleIcon";
        private const string PropVolume = "Volume";
        private const string PropMuteLabel = "MuteLabel";
        private const string PropSupportsVolume = "SupportsVolume";

        private const string IconPlay = "icPlay";
        private const string IconPause = "icPause";
        private const string IconPowerOn = "icPowerRegular";
        private const string IconPowerOff = "icPowerDisabled";
        private const string IconRepeatOn = "icRepeat";
        private const string IconRepeatOff = "icRepeatDisabled";
        private const string IconShuffleOn = "icShuffle";
        private const string IconShuffleOff = "icShuffleDisabled";

        // Mute button uses a text affordance label (no reliable mute glyph in the
        // Crestron icon set): "Mute" when live, "Unmute" when currently muted.
        private const string LabelMute = "Mute";
        private const string LabelUnmute = "Unmute";

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
        private PropertyValue<string> _elapsedProp;
        private PropertyValue<string> _durationProp;
        private PropertyValue<string> _playbackStateProp;
        private PropertyValue<string> _playbackIconProp;
        private PropertyValue<bool> _shuffleProp;
        private PropertyValue<bool> _repeatProp;
        private PropertyValue<bool> _powerProp;
        private PropertyValue<string> _powerIconProp;
        private PropertyValue<string> _tileStatusProp;
        private PropertyValue<string> _sourceNameProp;
        private PropertyValue<string> _trackLineProp;
        private PropertyValue<string> _byArtistProp;
        private PropertyValue<string> _fromAlbumProp;
        private PropertyValue<string> _timeTextProp;
        private PropertyValue<int> _progressProp;
        private PropertyValue<bool> _hasDurationProp;
        private PropertyValue<bool> _noDurationProp;
        private PropertyValue<string> _repeatIconProp;
        private PropertyValue<string> _shuffleIconProp;
        private PropertyValue<int> _volumeProp;
        private PropertyValue<string> _muteLabelProp;
        private PropertyValue<bool> _supportsVolumeProp;
        private bool _muted;
        private int _volumeStep = 2;

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
            _elapsedProp = CreateProperty<string>(new PropertyDefinition(PropElapsed, null, DevicePropertyType.String));
            _durationProp = CreateProperty<string>(new PropertyDefinition(PropDuration, null, DevicePropertyType.String));
            _playbackStateProp = CreateProperty<string>(new PropertyDefinition(PropPlaybackState, null, DevicePropertyType.String));
            _playbackIconProp = CreateProperty<string>(new PropertyDefinition(PropPlaybackIcon, null, DevicePropertyType.String));
            _shuffleProp = CreateProperty<bool>(new PropertyDefinition(PropShuffle, null, DevicePropertyType.Boolean));
            _repeatProp = CreateProperty<bool>(new PropertyDefinition(PropRepeat, null, DevicePropertyType.Boolean));
            _powerProp = CreateProperty<bool>(new PropertyDefinition(PropPower, null, DevicePropertyType.Boolean));
            _powerIconProp = CreateProperty<string>(new PropertyDefinition(PropPowerIcon, null, DevicePropertyType.String));
            _tileStatusProp = CreateProperty<string>(new PropertyDefinition(PropTileStatus, null, DevicePropertyType.String));
            _sourceNameProp = CreateProperty<string>(new PropertyDefinition(PropSourceName, null, DevicePropertyType.String));
            _trackLineProp = CreateProperty<string>(new PropertyDefinition(PropTrackLine, null, DevicePropertyType.String));
            _byArtistProp = CreateProperty<string>(new PropertyDefinition(PropByArtist, null, DevicePropertyType.String));
            _fromAlbumProp = CreateProperty<string>(new PropertyDefinition(PropFromAlbum, null, DevicePropertyType.String));
            _timeTextProp = CreateProperty<string>(new PropertyDefinition(PropTimeText, null, DevicePropertyType.String));
            _progressProp = CreateProperty<int>(new PropertyDefinition(PropProgress, null, DevicePropertyType.Int32, 0, 100, 1));
            _hasDurationProp = CreateProperty<bool>(new PropertyDefinition(PropHasDuration, null, DevicePropertyType.Boolean));
            _noDurationProp = CreateProperty<bool>(new PropertyDefinition(PropNoDuration, null, DevicePropertyType.Boolean));
            _repeatIconProp = CreateProperty<string>(new PropertyDefinition(PropRepeatIcon, null, DevicePropertyType.String));
            _shuffleIconProp = CreateProperty<string>(new PropertyDefinition(PropShuffleIcon, null, DevicePropertyType.String));
            _volumeProp = CreateProperty<int>(new PropertyDefinition(PropVolume, null, DevicePropertyType.Int32, 0, 100, 1));
            _muteLabelProp = CreateProperty<string>(new PropertyDefinition(PropMuteLabel, null, DevicePropertyType.String));
            _supportsVolumeProp = CreateProperty<bool>(new PropertyDefinition(PropSupportsVolume, null, DevicePropertyType.Boolean));
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
                case CmdToggleRepeat: InvokeOnGateway((svc, mac) => svc.SetRepeat(mac, !_repeatProp.Value)); break;
                case CmdToggleShuffle: InvokeOnGateway((svc, mac) => svc.SetShuffle(mac, !_shuffleProp.Value)); break;
                case CmdPowerOn: InvokeOnGateway((svc, mac) => svc.PowerOn(mac)); break;
                case CmdPowerOff: InvokeOnGateway((svc, mac) => svc.PowerOff(mac)); break;
                case CmdPowerToggle: InvokeOnGateway((svc, mac) => svc.PowerToggle(mac)); break;
                case CmdVolumeUp: InvokeOnGateway((svc, mac) => svc.VolumeUp(mac, _volumeStep)); break;
                case CmdVolumeDown: InvokeOnGateway((svc, mac) => svc.VolumeDown(mac, _volumeStep)); break;
                case CmdToggleMute: InvokeOnGateway((svc, mac) => svc.SetMute(mac, !_muted)); break;
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
                service.NameChanged += OnNameChanged;
                service.PowerStateChanged += OnPowerStateChanged;
                service.PlaybackStateChanged += OnPlaybackStateChanged;
                service.MetadataUpdated += OnMetadataUpdated;
                service.ShuffleChanged += OnShuffleChanged;
                service.RepeatChanged += OnRepeatChanged;
                service.VolumeChanged += OnVolumeChanged;
                service.MuteChanged += OnMuteChanged;
                service.VolumeStepChanged += OnVolumeStepChanged;
            }

            if (oldService != null)
            {
                try { oldService.AvailabilityChanged -= OnAvailabilityChanged; } catch { }
                try { oldService.NameChanged -= OnNameChanged; } catch { }
                try { oldService.PowerStateChanged -= OnPowerStateChanged; } catch { }
                try { oldService.PlaybackStateChanged -= OnPlaybackStateChanged; } catch { }
                try { oldService.MetadataUpdated -= OnMetadataUpdated; } catch { }
                try { oldService.ShuffleChanged -= OnShuffleChanged; } catch { }
                try { oldService.RepeatChanged -= OnRepeatChanged; } catch { }
                try { oldService.VolumeChanged -= OnVolumeChanged; } catch { }
                try { oldService.MuteChanged -= OnMuteChanged; } catch { }
                try { oldService.VolumeStepChanged -= OnVolumeStepChanged; } catch { }
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
            UpdateName(snap.Name);
            UpdateAvailability(snap.IsAvailable);
            UpdatePower(snap.IsPoweredOn);
            UpdatePlayback(snap.PlaybackState);
            UpdateMetadata(snap.Metadata);
            UpdateShuffle(snap.ShuffleEnabled);
            UpdateRepeat(snap.RepeatEnabled);
            UpdateSupportsVolume(snap.SupportsVolume);
            UpdateVolume(snap.Volume);
            UpdateMute(snap.Muted);
            UpdateVolumeStep(snap.VolumeStep);
        }

        // ===== Gateway event handlers =====

        private void OnAvailabilityChanged(string mac, bool isAvailable)
        {
            if (!IsMine(mac)) return;
            UpdateAvailability(isAvailable);
        }

        private void OnNameChanged(string mac, string name)
        {
            if (!IsMine(mac)) return;
            UpdateName(name);
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

        private void OnVolumeStepChanged(string mac, int step)
        {
            if (!IsMine(mac)) return;
            UpdateVolumeStep(step);
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

        private void UpdateName(string name)
        {
            _sourceNameProp.Value = name ?? string.Empty;
            Commit();
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

            // Composite now-playing lines for the layout.
            //   Line 1: "<track>  <bullet>  <title>" (or just the title when no track #)
            //   Line 2: "by <artist>"   Line 3: "from <album>"
            // The bullet (U+2022) is built from its code point so the source stays ASCII.
            var bullet = (char)0x2022;
            _trackLineProp.Value = meta.TrackNumber > 0
                ? meta.TrackNumber.ToString() + "  " + bullet + "  " + meta.Title
                : meta.Title;
            _byArtistProp.Value = string.IsNullOrEmpty(meta.Artist) ? string.Empty : "by " + meta.Artist;
            _fromAlbumProp.Value = string.IsNullOrEmpty(meta.Album) ? string.Empty : "from " + meta.Album;

            // Timing + read-only progress. When the duration is unknown (e.g.
            // a radio stream) hide the bar and total and show elapsed alone.
            var elapsed = FormatTime(meta.PositionSeconds);
            _elapsedProp.Value = elapsed;
            _durationProp.Value = FormatTime(meta.DurationSeconds);

            bool hasDuration = meta.DurationSeconds > 0;
            _hasDurationProp.Value = hasDuration;
            _noDurationProp.Value = !hasDuration;

            if (hasDuration)
            {
                _timeTextProp.Value = elapsed + " / " + FormatTime(meta.DurationSeconds);
                var pct = (int)((long)meta.PositionSeconds * 100 / meta.DurationSeconds);
                _progressProp.Value = pct < 0 ? 0 : (pct > 100 ? 100 : pct);
            }
            else
            {
                _timeTextProp.Value = elapsed;
                _progressProp.Value = 0;
            }

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
            _shuffleIconProp.Value = enabled ? IconShuffleOn : IconShuffleOff;
            Commit();
        }

        private void UpdateRepeat(bool enabled)
        {
            _repeatProp.Value = enabled;
            _repeatIconProp.Value = enabled ? IconRepeatOn : IconRepeatOff;
            Commit();
        }

        private void UpdateVolume(int level)
        {
            if (level < 0) level = 0;
            if (level > 100) level = 100;
            _volumeProp.Value = level;
            Commit();
        }

        private void UpdateMute(bool muted)
        {
            _muted = muted;
            _muteLabelProp.Value = muted ? LabelUnmute : LabelMute;
            Commit();
        }

        // The step is internal only (it parameterizes the Vol+/- commands); it
        // drives no visible property, so there is nothing to Commit.
        private void UpdateVolumeStep(int step)
        {
            if (step < 1) step = 1;
            if (step > 50) step = 50;
            _volumeStep = step;
        }

        private void UpdateSupportsVolume(bool supported)
        {
            _supportsVolumeProp.Value = supported;
            Commit();
        }

        // Formats seconds as xx:yy:zz. Minutes and seconds are always shown
        // (two digits); hours appear only when non-zero. The hours field wraps
        // at 99 to honor the 99:59:59 spacing budget.
        private static string FormatTime(int totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            int hours = totalSeconds / 3600;
            int minutes = (totalSeconds % 3600) / 60;
            int seconds = totalSeconds % 60;
            if (hours > 0)
            {
                hours %= 100;
                return hours.ToString("D2") + ":" + minutes.ToString("D2") + ":" + seconds.ToString("D2");
            }
            return minutes.ToString("D2") + ":" + seconds.ToString("D2");
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
                try { svc.NameChanged -= OnNameChanged; } catch { }
                try { svc.PowerStateChanged -= OnPowerStateChanged; } catch { }
                try { svc.PlaybackStateChanged -= OnPlaybackStateChanged; } catch { }
                try { svc.MetadataUpdated -= OnMetadataUpdated; } catch { }
                try { svc.ShuffleChanged -= OnShuffleChanged; } catch { }
                try { svc.RepeatChanged -= OnRepeatChanged; } catch { }
                try { svc.VolumeChanged -= OnVolumeChanged; } catch { }
                try { svc.MuteChanged -= OnMuteChanged; } catch { }
                try { svc.VolumeStepChanged -= OnVolumeStepChanged; } catch { }
                if (!string.IsNullOrEmpty(mac))
                {
                    try { svc.UnbindPlayer(mac); } catch { }
                }
            }

            base.Dispose();
        }
    }
}
