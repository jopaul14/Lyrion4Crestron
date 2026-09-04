// ---------------------------------------------------------------------------
//  Helper_Lyrion_Player - Lyrion Helper (Driver 3 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Crestron.RAD.Common.Attributes.Programming;
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
        // Preset buttons are "Preset1".."Preset4" (see UiDefinition.xml).
        private const string CmdPresetPrefix = "Preset";

        private const string PropTitle = "Title";
        private const string PropArtist = "Artist";
        private const string PropAlbum = "Album";
        private const string PropElapsed = "Elapsed";
        private const string PropDuration = "Duration";
        private const string PropPlaybackState = "PlaybackState";
        private const string PropPlaybackIcon = "PlaybackIcon";
        // Current-state glyph for the room tile (play while playing, pause
        // otherwise) — distinct from PlaybackIcon, which is the NEXT action.
        private const string PropPlaybackStateIcon = "PlaybackStateIcon";
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
        // Preset button state: "PresetLabel1"/"PresetIcon1"/"PresetVisible1" …
        // Deliberately not bare "Preset1": that is the command name, and
        // keeping the two apart makes the UiDefinition unambiguous.
        private const string PropPresetLabelPrefix = "PresetLabel";
        private const string PropPresetIconPrefix = "PresetIcon";
        private const string PropPresetVisiblePrefix = "PresetVisible";
        private const string PropAnyPresets = "AnyPresets";

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

        // Serialises the bind-time snapshot apply, every event handler, and
        // Dispose's unbind — see SourceDriver. Lock order: _applyGate, then
        // _gate; never the reverse.
        private readonly object _applyGate = new object();

        // Last availability reported for the bound player, so Connect() can
        // restore it instead of forcing Connected=true over it. Starts true —
        // the framework's own default for a driver that has not bound yet —
        // and is driven false by an availability loss OR by UnbindInvalidMac,
        // so "unbound because never configured" and "unbound because the MAC
        // is invalid" read differently after Connect() re-runs.
        private bool _lastAvailability = true;

        private HelperProtocol _protocol;
        private string _configuredMac;
        private string _boundMac;
        private ILyrionServerService _server;
        private volatile bool _disposed;

        private PropertyValue<string> _titleProp;
        private PropertyValue<string> _artistProp;
        private PropertyValue<string> _albumProp;
        private PropertyValue<string> _elapsedProp;
        private PropertyValue<string> _durationProp;
        private PropertyValue<string> _playbackStateProp;
        private PropertyValue<string> _playbackIconProp;
        private PropertyValue<string> _playbackStateIconProp;

        // Per-track caches for the 1 Hz metadata tick (see UpdateMetadata).
        private int _lastTrackNumber = -1;
        private int _lastDurationSeconds = -1;
        private string _lastDurationText = string.Empty;
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

        // Preset slots. _presets[i] is null when slot i is unconfigured, in
        // which case its button is hidden. Written from SetUserAttribute and
        // read from DoCommand / the ProgrammableOperations, so access is under
        // _gate.
        private readonly LyrionPresetConfig[] _presets =
            new LyrionPresetConfig[HelperProtocol.PresetCount];
        private readonly PropertyValue<string>[] _presetLabelProps =
            new PropertyValue<string>[HelperProtocol.PresetCount];
        private readonly PropertyValue<string>[] _presetIconProps =
            new PropertyValue<string>[HelperProtocol.PresetCount];
        private readonly PropertyValue<bool>[] _presetVisibleProps =
            new PropertyValue<bool>[HelperProtocol.PresetCount];
        private PropertyValue<bool> _anyPresetsProp;

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
            _protocol.PresetReceived += OnPresetReceived;
            DeviceProtocol = _protocol;
            DeviceProtocol.Initialize(DriverData);

            InitialiseView();

            LyrionServerServiceRegistry.Subscribe(OnServerAvailable);
        }

        /// <summary>
        /// Gives every bound label, icon and text line its idle value once, at
        /// driver load, before any bind or event can arrive.
        /// </summary>
        /// <remarks>
        /// Without this, a property that nothing ever writes keeps the RAD
        /// default (no value), and Crestron Home renders a placeholder in its
        /// place: a button whose bound icon is unset falls back to its literal
        /// label, so ShuffleBtn showed "S..." at 5-across, and MuteBtn — which
        /// has no literal label — showed "Tex...".
        ///
        /// A property goes unwritten whenever a player's first observed value
        /// happens to EQUAL the record's default, because the registry
        /// change-gates and publishes nothing (the sole exception is the first
        /// explicit power report). Combined with a bind that lands before the
        /// player is observed — which touches nothing but Connected (1.0.14) —
        /// the consumer never hears the field at all. Seen on hardware after a
        /// processor reboot: a player with power:1, mode:play and repeat:2
        /// rendered those three buttons correctly and broke on exactly the two
        /// fields whose real values matched the defaults, shuffle:0 and
        /// unmuted. A player that is simply switched off at boot loses its
        /// power icon and tile status the same way.
        ///
        /// This asserts nothing about a player. The values written here are
        /// the labels for the state the properties already hold by default, so
        /// the two cannot disagree; the moment the Lyrion Server observes
        /// anything different it publishes and these are overwritten. It is
        /// the same baseline the Source sets with PlayBackStatus = Stop.
        /// </remarks>
        private void InitialiseView()
        {
            lock (_applyGate)
            {
                UpdateName(string.Empty);
                UpdatePlayback(LyrionPlaybackState.Stopped);
                UpdateShuffle(false);
                UpdateRepeat(false);
                UpdateMute(false);

                // The track card's lines, blank rather than absent. Set
                // directly rather than through UpdateMetadata: that would also
                // seed the timing text with a formatted "00:00" for a player
                // nothing has looked at, which is a claim, where an empty line
                // is not.
                Set(_trackLineProp, string.Empty);
                Set(_byArtistProp, string.Empty);
                Set(_fromAlbumProp, string.Empty);
                Set(_timeTextProp, string.Empty);

                RefreshTileStatus();
                Commit();
            }
        }

        public override void Connect()
        {
            // Re-run by the framework after any MAC edit; must not override
            // the availability already learned. 1.0.13 used
            // `!bound || available`, which read an UNBOUND driver as connected
            // and so undid UnbindInvalidMac's "offline" the moment the
            // framework re-ran this for the very edit that caused the unbind.
            // Under _applyGate like every other write of Connected (1.0.15),
            // so it cannot interleave with a loss arriving on the CLI thread
            // and leave a stale true that the registry would never correct.
            lock (_applyGate)
            {
                bool available;
                lock (_gate) { available = _lastAvailability; }
                Connected = available;
            }
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
            _playbackStateIconProp = CreateProperty<string>(new PropertyDefinition(PropPlaybackStateIcon, null, DevicePropertyType.String));
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

            // Preset label/icon/visible triples, one per slot. All four exist
            // unconditionally; unconfigured slots simply stay invisible, which
            // keeps the UiDefinition static (no runtime UI regeneration).
            _anyPresetsProp = CreateProperty<bool>(new PropertyDefinition(PropAnyPresets, null, DevicePropertyType.Boolean));
            for (var i = 0; i < HelperProtocol.PresetCount; i++)
            {
                var n = (i + 1).ToString();
                _presetLabelProps[i] = CreateProperty<string>(
                    new PropertyDefinition(PropPresetLabelPrefix + n, null, DevicePropertyType.String));
                _presetIconProps[i] = CreateProperty<string>(
                    new PropertyDefinition(PropPresetIconPrefix + n, null, DevicePropertyType.String));
                _presetVisibleProps[i] = CreateProperty<bool>(
                    new PropertyDefinition(PropPresetVisiblePrefix + n, null, DevicePropertyType.Boolean));

                _presetLabelProps[i].Value = string.Empty;
                _presetIconProps[i].Value = LyrionPresetConfig.DefaultIcon;
                _presetVisibleProps[i].Value = false;
            }
            _anyPresetsProp.Value = false;
        }

        // ===== AExtensionDevice overrides =====

        protected override IOperationResult DoCommand(string command, string[] parameters)
        {
            // Under _applyGate: the Toggle* cases read RAD-facing state
            // (_playbackStateProp, _repeatProp, _shuffleProp, _muted,
            // _volumeStep) that the event handlers write under the same lock
            // on other threads. Held only for the read + the service call
            // (which sends a CLI line asynchronously and publishes nothing
            // synchronously), so nothing here re-enters or blocks for long.
            lock (_applyGate)
            {
                DoCommandLocked(command);
            }
            return new OperationResult(OperationResultCode.Success);
        }

        private void DoCommandLocked(string command)
        {
            switch (command)
            {
                case CmdPlay: InvokeOnServer((svc, mac) => svc.Play(mac)); break;
                case CmdPause: InvokeOnServer((svc, mac) => svc.Pause(mac)); break;
                case CmdStop: InvokeOnServer((svc, mac) => svc.Stop(mac)); break;
                case CmdNext: InvokeOnServer((svc, mac) => svc.Next(mac)); break;
                case CmdPrevious: InvokeOnServer((svc, mac) => svc.Previous(mac)); break;
                case CmdTogglePlay:
                    InvokeOnServer((svc, mac) =>
                    {
                        if (_playbackStateProp.Value == "Playing") svc.Pause(mac);
                        else svc.Play(mac);
                    });
                    break;
                case CmdToggleRepeat: InvokeOnServer((svc, mac) => svc.SetRepeat(mac, !_repeatProp.Value)); break;
                case CmdToggleShuffle: InvokeOnServer((svc, mac) => svc.SetShuffle(mac, !_shuffleProp.Value)); break;
                case CmdPowerOn: InvokeOnServer((svc, mac) => svc.PowerOn(mac)); break;
                case CmdPowerOff: InvokeOnServer((svc, mac) => svc.PowerOff(mac)); break;
                case CmdPowerToggle: InvokeOnServer((svc, mac) => svc.PowerToggle(mac)); break;
                case CmdVolumeUp: InvokeOnServer((svc, mac) => svc.VolumeUp(mac, _volumeStep)); break;
                case CmdVolumeDown: InvokeOnServer((svc, mac) => svc.VolumeDown(mac, _volumeStep)); break;
                case CmdToggleMute: InvokeOnServer((svc, mac) => svc.SetMute(mac, !_muted)); break;
                default:
                    var slot = PresetSlotFromCommand(command);
                    if (slot >= 0) TriggerPreset(slot);
                    break;
            }
        }

        /// <summary>
        /// Maps the UI command names "Preset1".."Preset4" to a zero-based slot,
        /// or -1 for anything else.
        /// </summary>
        private static int PresetSlotFromCommand(string command)
        {
            if (command == null) return -1;
            if (command.Length != CmdPresetPrefix.Length + 1) return -1;
            if (!command.StartsWith(CmdPresetPrefix, StringComparison.Ordinal)) return -1;

            var digit = command[command.Length - 1];
            if (digit < '1' || digit > '0' + HelperProtocol.PresetCount) return -1;
            return digit - '1';
        }

        /// <summary>
        /// Sends the configured command for a preset slot. A no-op for an
        /// out-of-range or unconfigured slot, so a stale sequence referring to
        /// a preset the installer has since cleared fails quietly rather than
        /// throwing inside Crestron Home's sequence engine.
        /// </summary>
        private void TriggerPreset(int slot)
        {
            if (slot < 0 || slot >= HelperProtocol.PresetCount) return;

            LyrionPresetConfig preset;
            lock (_gate) { preset = _presets[slot]; }
            if (preset == null) return;

            InvokeOnServer((svc, mac) => svc.SendPlayerCommand(mac, preset.Command));
        }

        protected override IOperationResult SetDriverPropertyValue<T>(string propertyKey, T value)
        {
            switch (propertyKey)
            {
                case PropShuffle:
                    var shuffle = value as bool?;
                    if (shuffle.HasValue)
                        InvokeOnServer((svc, mac) => svc.SetShuffle(mac, shuffle.Value));
                    return new OperationResult(OperationResultCode.Success);

                case PropRepeat:
                    var repeat = value as bool?;
                    if (repeat.HasValue)
                        InvokeOnServer((svc, mac) => svc.SetRepeat(mac, repeat.Value));
                    return new OperationResult(OperationResultCode.Success);

                case PropPower:
                    var power = value as bool?;
                    if (power.HasValue)
                        InvokeOnServer((svc, mac) =>
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
            if (canon == null)
            {
                UnbindInvalidMac(rawMac);
                return;
            }

            lock (_gate) { _configuredMac = canon; }
            TryBindToServer();
        }

        // A cleared or unparseable MAC is an unbind, not a no-op — see
        // SourceDriver.UnbindInvalidMac. The tile goes to off/stopped, then
        // offline. With nothing bound there is no state to lower, but a
        // NON-BLANK value still gets the warning (a typo at first setup is
        // the one moment the installer is looking at the log); an empty
        // attribute at boot stays silent.
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
                        _log("Helper WARNING: player MAC '" + rawMac + "' is not valid; nothing bound");
                    }
                    return;
                }

                if (svc != null) { try { svc.UnbindPlayer(previous); } catch { } }

                // The whole view goes blank, not just power/playback: this
                // driver no longer represents any player, and because the
                // record was unbound the registry's 30 s metadata clear can
                // never reach it. Fields first, availability last (loss order),
                // one commit.
                UpdateName(string.Empty);
                UpdatePlayback(LyrionPlaybackState.Stopped);
                UpdatePower(false);
                UpdateMetadata(LyrionMetadata.Empty);
                UpdateShuffle(false);
                UpdateRepeat(false);
                UpdateVolume(0);
                UpdateMute(false);
                UpdateAvailability(false);
                Commit();
                _log("Helper WARNING: player MAC '" + (rawMac ?? string.Empty) + "' is not valid; unbound from " + previous);
            }
        }

        private void OnPresetReceived(int slot, string configured)
        {
            if (slot < 0 || slot >= HelperProtocol.PresetCount) return;

            var parsed = LyrionPresetConfig.Parse(configured);

            // Under _applyGate like every other RAD-facing write: this runs on
            // Crestron Home's configuration thread and used to write four
            // properties and Commit() with no lock while a CLI-thread handler
            // could be mid-Commit under _applyGate.
            lock (_applyGate)
            {
                lock (_gate) { _presets[slot] = parsed; }

                // Drive the button's label/icon/visibility straight from the
                // parsed value. A slot that fails to parse renders exactly like
                // an empty one — hidden — so a typo can never leave a dead
                // button on the page.
                Set(_presetLabelProps[slot], parsed?.Name ?? string.Empty);
                Set(_presetIconProps[slot], parsed?.Icon ?? LyrionPresetConfig.DefaultIcon);
                Set(_presetVisibleProps[slot], parsed != null);

                var any = false;
                lock (_gate)
                {
                    for (var i = 0; i < _presets.Length; i++)
                    {
                        if (_presets[i] != null) { any = true; break; }
                    }
                }
                Set(_anyPresetsProp, any);

                Commit();
            }
        }

        // ===== Programmable operations (Crestron Home sequences) =====
        // These surface in Crestron Home's event/scene/button-press editor, so
        // an installer can build "power on -> set volume -> start preset 2".
        // Four discrete operations rather than one with a slot parameter: the
        // sequence editor shows them by name, and a bare list reads better than
        // a parameter dialog. The names are static because a driver's
        // programming surface is fixed at compile time — the installer's own
        // preset names appear on the UI buttons, not here.

        [ProgrammableOperation("Play Preset 1")]
        public void PlayPreset1() => TriggerPreset(0);

        [ProgrammableOperation("Play Preset 2")]
        public void PlayPreset2() => TriggerPreset(1);

        [ProgrammableOperation("Play Preset 3")]
        public void PlayPreset3() => TriggerPreset(2);

        [ProgrammableOperation("Play Preset 4")]
        public void PlayPreset4() => TriggerPreset(3);

        // ===== Lyrion Server binding =====

        private void OnServerAvailable(ILyrionServerService service)
        {
            // Invoked on initial subscription and again whenever the Lyrion
            // Server driver reloads and registers a fresh service. The whole
            // swap runs under _applyGate (1.0.15) so it cannot interleave with
            // a bind already in flight on the OLD service — see
            // SourceDriver.OnServerAvailable.
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

                TryBindToServer();
            }
        }

        private void TryBindToServer()
        {
            // Whole bind under _applyGate — see SourceDriver.TryBindToServer.
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
                    _log("Helper: Bound to MAC " + mac);

                    if (svc.TryGetSnapshot(mac, out var snap))
                    {
                        ApplySnapshot(snap);
                    }
                }
            }
        }

        private void ApplySnapshot(LyrionPlayerSnapshot snap)
        {
            // OBSERVED records only, like the Source and Receiver. 1.0.13 wrote
            // every level "observed or not" on the theory that a blank record
            // reads as off/stopped — but on a Lyrion Server reload that wiped
            // the live name, track, volume, shuffle/repeat and mute, and any
            // field whose real value equals the blank default was never
            // corrected, because the registry change-gates against that blank
            // record (mute is never in a status reply at all, so a muted
            // player showed "Mute" until the next live notification). The
            // loss publish from the old Server's Dispose already put the tile
            // at off/stopped; an unobserved bind has nothing to add.
            //
            // The snapshot carries EFFECTIVE power/playback. Availability
            // first when available, last when not — the registry's order.
            // One commit for the whole snapshot.
            if (!snap.IsObserved)
            {
                UpdateAvailability(snap.IsAvailable);
                return;
            }

            if (snap.IsAvailable) UpdateAvailability(true);
            UpdateName(snap.Name);
            UpdatePower(snap.IsPoweredOn);
            UpdatePlayback(snap.PlaybackState);
            UpdateMetadata(snap.Metadata);
            UpdateShuffle(snap.ShuffleEnabled);
            UpdateRepeat(snap.RepeatEnabled);
            UpdateSupportsVolume(snap.SupportsVolume);
            UpdateVolume(snap.Volume);
            UpdateMute(snap.Muted);
            UpdateVolumeStep(snap.VolumeStep);
            if (!snap.IsAvailable) UpdateAvailability(false);
            Commit();
        }

        // ===== Lyrion Server event handlers =====

        // Handlers apply under _applyGate with IsMine checked inside it, so a
        // bind in progress cannot be raced by an event for the MAC it is
        // binding (see SourceDriver).

        // Each handler: one unit of work, one Commit(). The Update* methods
        // only assign (through Set, which skips unchanged values), so a
        // snapshot or a multi-field reply costs one commit, not one per field.

        private void OnAvailabilityChanged(string mac, bool isAvailable)
        {
            lock (_applyGate) { if (IsMine(mac)) UpdateAvailability(isAvailable); }
        }

        private void OnNameChanged(string mac, string name)
        {
            lock (_applyGate) { if (IsMine(mac)) { UpdateName(name); Commit(); } }
        }

        private void OnPowerStateChanged(string mac, bool isOn)
        {
            lock (_applyGate) { if (IsMine(mac)) { UpdatePower(isOn); Commit(); } }
        }

        private void OnPlaybackStateChanged(string mac, LyrionPlaybackState state)
        {
            lock (_applyGate) { if (IsMine(mac)) { UpdatePlayback(state); Commit(); } }
        }

        private void OnMetadataUpdated(string mac, LyrionMetadata meta)
        {
            lock (_applyGate) { if (IsMine(mac)) { UpdateMetadata(meta); Commit(); } }
        }

        private void OnShuffleChanged(string mac, bool enabled)
        {
            lock (_applyGate) { if (IsMine(mac)) { UpdateShuffle(enabled); Commit(); } }
        }

        private void OnRepeatChanged(string mac, bool enabled)
        {
            lock (_applyGate) { if (IsMine(mac)) { UpdateRepeat(enabled); Commit(); } }
        }

        private void OnVolumeChanged(string mac, int level)
        {
            lock (_applyGate) { if (IsMine(mac)) { UpdateVolume(level); Commit(); } }
        }

        private void OnMuteChanged(string mac, bool muted)
        {
            lock (_applyGate) { if (IsMine(mac)) { UpdateMute(muted); Commit(); } }
        }

        private void OnVolumeStepChanged(string mac, int step)
        {
            lock (_applyGate) { if (IsMine(mac)) UpdateVolumeStep(step); }
        }

        // ===== Feedback into the extension device UI =====

        private void UpdateAvailability(bool isAvailable)
        {
            // Connection only: the registry publishes effective off/stopped
            // before this on loss and on/playing after it on restore (see
            // SourceDriver.UpdateAvailability).
            lock (_gate) { _lastAvailability = isAvailable; }
            Connected = isAvailable;
        }

        // Assigns a property only when the value actually changed. The SDK
        // documents Commit() as sending "all of the modified property values"
        // and says nothing about the setter change-gating, so this is what
        // keeps the 1 Hz metadata tick from re-sending nine unchanged strings
        // per player per second. Returns whether anything was written.
        private static bool Set<T>(PropertyValue<T> prop, T value)
        {
            if (EqualityComparer<T>.Default.Equals(prop.Value, value)) return false;
            prop.Value = value;
            return true;
        }

        // The Update* methods below ASSIGN ONLY. The caller — an event
        // handler, ApplySnapshot, OnPresetReceived, UnbindInvalidMac — commits
        // once per unit of work. (Through 1.0.13 each Update* committed
        // itself: ten commits per bind, one per field per status reply.)

        private void UpdateName(string name)
        {
            Set(_sourceNameProp, name ?? string.Empty);
        }

        private void UpdatePower(bool isOn)
        {
            if (Set(_powerProp, isOn)) RefreshTileStatus();
        }

        private void UpdatePlayback(LyrionPlaybackState state)
        {
            string label;
            string icon;
            string stateIcon;
            // PlaybackIcon is the *next* transport action (a toggle affordance
            // for the Play/Pause button): while playing, show Pause; while
            // paused or stopped, show Play. PlaybackStateIcon is the *current*
            // state for the room tile, which has no play/pause tap: a play
            // glyph while music is playing, a pause glyph otherwise. Binding
            // the tile to the affordance showed a pause symbol while playing.
            switch (state)
            {
                case LyrionPlaybackState.Playing:
                    label = "Playing";
                    icon = IconPause;
                    stateIcon = IconPlay;
                    break;
                case LyrionPlaybackState.Paused:
                    label = "Paused";
                    icon = IconPlay;
                    stateIcon = IconPause;
                    break;
                default:
                    label = "Stopped";
                    icon = IconPlay;
                    stateIcon = IconPause;
                    break;
            }
            var changed = Set(_playbackStateProp, label);
            changed |= Set(_playbackIconProp, icon);
            changed |= Set(_playbackStateIconProp, stateIcon);
            if (changed) RefreshTileStatus();
        }

        private void UpdateMetadata(LyrionMetadata meta)
        {
            var textChanged = Set(_titleProp, meta.Title);
            textChanged |= Set(_artistProp, meta.Artist);
            textChanged |= Set(_albumProp, meta.Album);

            // Composite now-playing lines for the layout — rebuilt only when
            // the text they are made of changed, which on the 1 Hz position
            // tick is never.
            //   Line 1: "<track>  <bullet>  <title>" (or just the title when no track #)
            //   Line 2: "by <artist>"   Line 3: "from <album>"
            // The bullet (U+2022) is built from its code point so the source stays ASCII.
            if (textChanged || meta.TrackNumber != _lastTrackNumber)
            {
                _lastTrackNumber = meta.TrackNumber;
                var bullet = (char)0x2022;
                Set(_trackLineProp, meta.TrackNumber > 0
                    ? meta.TrackNumber.ToString() + "  " + bullet + "  " + meta.Title
                    : meta.Title);
                Set(_byArtistProp, string.IsNullOrEmpty(meta.Artist) ? string.Empty : "by " + meta.Artist);
                Set(_fromAlbumProp, string.IsNullOrEmpty(meta.Album) ? string.Empty : "from " + meta.Album);
            }

            // Timing. TimeText is what the page actually shows (on the track
            // card's fourth line): "elapsed / total" when the duration is
            // known, elapsed alone for a radio stream. Progress / HasDuration /
            // NoDuration are still published for the driver's property surface
            // but are no longer drawn. The formatted duration is cached per
            // track so the tick formats one number, not three.
            if (meta.DurationSeconds != _lastDurationSeconds)
            {
                _lastDurationSeconds = meta.DurationSeconds;
                _lastDurationText = FormatTime(meta.DurationSeconds);
                Set(_durationProp, _lastDurationText);
                var has = meta.DurationSeconds > 0;
                Set(_hasDurationProp, has);
                Set(_noDurationProp, !has);
            }

            var elapsed = FormatTime(meta.PositionSeconds);
            Set(_elapsedProp, elapsed);

            if (meta.DurationSeconds > 0)
            {
                Set(_timeTextProp, elapsed + " / " + _lastDurationText);
                var pct = (int)((long)meta.PositionSeconds * 100 / meta.DurationSeconds);
                Set(_progressProp, pct < 0 ? 0 : (pct > 100 ? 100 : pct));
            }
            else
            {
                Set(_timeTextProp, elapsed);
                Set(_progressProp, 0);
            }

            if (textChanged) RefreshTileStatus();
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
            Set(_powerIconProp, on ? IconPowerOn : IconPowerOff);

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
            Set(_tileStatusProp, status);
        }

        private void UpdateShuffle(bool enabled)
        {
            Set(_shuffleProp, enabled);
            Set(_shuffleIconProp, enabled ? IconShuffleOn : IconShuffleOff);
        }

        private void UpdateRepeat(bool enabled)
        {
            Set(_repeatProp, enabled);
            Set(_repeatIconProp, enabled ? IconRepeatOn : IconRepeatOff);
        }

        private void UpdateVolume(int level)
        {
            if (level < 0) level = 0;
            if (level > 100) level = 100;
            Set(_volumeProp, level);
        }

        private void UpdateMute(bool muted)
        {
            _muted = muted;
            Set(_muteLabelProp, muted ? LabelUnmute : LabelMute);
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
            Set(_supportsVolumeProp, supported);
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
                try { Trace.WriteLine("[Lyrion.Helper " + DateTime.UtcNow.ToString("HH:mm:ss.fff") + "] " + msg); }
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
                try { _protocol.PresetReceived -= OnPresetReceived; } catch { }
            }

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
            }

            base.Dispose();
        }
    }
}
