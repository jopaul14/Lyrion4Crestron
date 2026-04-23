// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using Crestron.DeviceDrivers.EntityModel.Data;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;
using LyrionCommunity.Crestron.Lyrion.Definitions;
using LyrionCommunity.Crestron.Lyrion.Models;
using LyrionCommunity.Crestron.Lyrion.Protocol;
using LyrionCommunity.Crestron.Lyrion.Transport;

namespace LyrionCommunity.Crestron.Lyrion
{
    /// <summary>
    /// A single Lyrion player, exposed as a Crestron <see cref="ManagedDevice"/>-style
    /// entity. One instance per configured player MAC.
    /// </summary>
    /// <remarks>
    /// All outbound transport calls (CLI and JSON-RPC) are performed via the
    /// delegates supplied by <see cref="DriverMain"/> so this class has no
    /// direct dependency on <see cref="Transport.LmsCliClient"/> or
    /// <see cref="Transport.LmsJsonRpcClient"/>. That keeps the entity unit-testable.
    /// <para/>
    /// Thread-safety: <see cref="ApplyPlayerMessage"/> is called from the CLI
    /// receive loop; <c>[EntityCommand]</c> methods are called from the SDK.
    /// A single <see cref="_stateLock"/> guards all mutable state and
    /// publishes changes via <c>NotifyPropertyChanged</c> outside the lock.
    /// </remarks>
    public sealed class PlayerEntity : ReflectedAttributeDriverEntity
    {
        /// <summary>Step size applied by <c>audio:volumeUp</c>/<c>audio:volumeDown</c>.</summary>
        public const int VolumeStep = 2;

        /// <summary>Fixed sleep duration in seconds used by <c>power:sleep</c>.</summary>
        public const int SleepSeconds = 30 * 60;

        private readonly string _macAddress;
        private readonly Func<string, Task<bool>> _sendCli;
        private readonly Func<string, CancellationToken, Task<LmsRpcResult>> _sendRpc;
        private readonly Action<string> _log;
        private readonly object _stateLock = new object();
        private readonly PlayerState _state = new PlayerState();

        internal PlayerEntity(
            string controllerId,
            string macAddress,
            Func<string, Task<bool>> sendCli,
            Func<string, CancellationToken, Task<LmsRpcResult>> sendRpc,
            Action<string> log)
            : base(controllerId)
        {
            if (string.IsNullOrEmpty(controllerId))
            {
                throw new ArgumentException("Controller id is required.", nameof(controllerId));
            }

            if (string.IsNullOrEmpty(macAddress))
            {
                throw new ArgumentException("MAC address is required.", nameof(macAddress));
            }

            _macAddress = macAddress;
            _sendCli = sendCli ?? throw new ArgumentNullException(nameof(sendCli));
            _sendRpc = sendRpc ?? throw new ArgumentNullException(nameof(sendRpc));
            _log = log ?? (_ => { });
        }

        /// <summary>Normalized MAC address used to route CLI notifications to this player.</summary>
        internal string MacAddress => _macAddress;

        // ================================================================== Properties

        [EntityProperty(Id = "transport:playbackState")]
        public PlaybackState PlaybackState { get; private set; }

        [EntityProperty(Id = "audio:volume")]
        public int Volume { get; private set; }

        [EntityProperty(Id = "audio:muted")]
        public bool Muted { get; private set; }

        [EntityProperty(Id = "power:on")]
        public bool PowerOn { get; private set; }

        [EntityProperty(Id = "lyrion:repeatMode")]
        public RepeatMode RepeatMode { get; private set; }

        [EntityProperty(Id = "lyrion:shuffleMode")]
        public ShuffleMode ShuffleMode { get; private set; }

        [EntityProperty(Id = "lyrion:online")]
        public bool Online { get; private set; }

        [EntityProperty(Id = "media:title")]
        public string Title { get; private set; }

        [EntityProperty(Id = "media:artist")]
        public string Artist { get; private set; }

        [EntityProperty(Id = "media:album")]
        public string Album { get; private set; }

        [EntityProperty(Id = "media:artworkUrl")]
        public string ArtworkUrl { get; private set; }

        [EntityProperty(Id = "media:durationSec")]
        public int DurationSec { get; private set; }

        [EntityProperty(Id = "media:elapsedSec")]
        public int ElapsedSec { get; private set; }

        [EntityProperty(Id = "media:isRemote")]
        public bool IsRemote { get; private set; }

        [EntityProperty(Id = "media:stationName")]
        public string StationName { get; private set; }

        [EntityProperty(Id = "media:playlistIndex")]
        public int PlaylistIndex { get; private set; }

        [EntityProperty(Id = "media:playlistLength")]
        public int PlaylistLength { get; private set; }

        /// <summary>
        /// Raw JSON body of the most recent browse/favorites/playlist-tracks
        /// response. Integrators can parse in SIMPL# / programming to render
        /// their UI. Updated by the media:browse* commands.
        /// </summary>
        [EntityProperty(Id = "media:lastBrowseResult")]
        public string LastBrowseResult { get; private set; } = string.Empty;

        // ================================================================== Commands: transport

        [EntityCommand(Id = "transport:play")]
        public void Play() => FireAndLog(LmsCliCommands.Play(_macAddress), "transport:play");

        [EntityCommand(Id = "transport:pause")]
        public void Pause() => FireAndLog(LmsCliCommands.Pause(_macAddress), "transport:pause");

        [EntityCommand(Id = "transport:stop")]
        public void Stop() => FireAndLog(LmsCliCommands.Stop(_macAddress), "transport:stop");

        [EntityCommand(Id = "transport:nextTrack")]
        public void NextTrack() => FireAndLog(LmsCliCommands.NextTrack(_macAddress), "transport:nextTrack");

        [EntityCommand(Id = "transport:previousTrack")]
        public void PreviousTrack() => FireAndLog(LmsCliCommands.PreviousTrack(_macAddress), "transport:previousTrack");

        // ================================================================== Commands: volume

        [EntityCommand(Id = "audio:setVolume")]
        public void SetVolume(int volume) => FireAndLog(LmsCliCommands.SetVolume(_macAddress, volume), "audio:setVolume");

        [EntityCommand(Id = "audio:volumeUp")]
        public void VolumeUp() => FireAndLog(LmsCliCommands.VolumeUp(_macAddress, VolumeStep), "audio:volumeUp");

        [EntityCommand(Id = "audio:volumeDown")]
        public void VolumeDown() => FireAndLog(LmsCliCommands.VolumeDown(_macAddress, VolumeStep), "audio:volumeDown");

        [EntityCommand(Id = "audio:setMute")]
        public void SetMute(bool muted) => FireAndLog(LmsCliCommands.SetMute(_macAddress, muted), "audio:setMute");

        [EntityCommand(Id = "audio:toggleMute")]
        public void ToggleMute() => FireAndLog(LmsCliCommands.ToggleMute(_macAddress), "audio:toggleMute");

        // ================================================================== Commands: power

        [EntityCommand(Id = "power:setPower")]
        public void SetPower(bool on) => FireAndLog(LmsCliCommands.SetPower(_macAddress, on), "power:setPower");

        /// <summary>
        /// Put the player to sleep after <see cref="SleepSeconds"/> seconds
        /// (fixed 30 minutes). LMS fades the volume down and stops playback.
        /// </summary>
        [EntityCommand(Id = "power:sleep")]
        public void Sleep() => FireAndLog(LmsCliCommands.Sleep(_macAddress, SleepSeconds), "power:sleep");

        // ================================================================== Commands: playlist modes

        [EntityCommand(Id = "lyrion:setRepeatMode")]
        public void SetRepeatMode(RepeatMode mode) => FireAndLog(LmsCliCommands.SetRepeat(_macAddress, mode), "lyrion:setRepeatMode");

        [EntityCommand(Id = "lyrion:setShuffleMode")]
        public void SetShuffleMode(ShuffleMode mode) => FireAndLog(LmsCliCommands.SetShuffle(_macAddress, mode), "lyrion:setShuffleMode");

        // ================================================================== Commands: queue (replace + play)

        [EntityCommand(Id = "media:playFavorite")]
        public void PlayFavorite(string favoriteItemId)
        {
            if (string.IsNullOrEmpty(favoriteItemId))
            {
                _log($"{_macAddress}: media:playFavorite called with empty favoriteItemId; ignoring.");
                return;
            }

            FireAndLog(LmsCliCommands.PlayFavorite(_macAddress, favoriteItemId), "media:playFavorite");
        }

        [EntityCommand(Id = "media:playPlaylist")]
        public void PlayPlaylist(string playlistId)
        {
            if (string.IsNullOrEmpty(playlistId))
            {
                _log($"{_macAddress}: media:playPlaylist called with empty playlistId; ignoring.");
                return;
            }

            FireAndLog(LmsCliCommands.PlayPlaylist(_macAddress, playlistId), "media:playPlaylist");
        }

        // ================================================================== Commands: browse (JSON-RPC)

        /// <summary>
        /// Generic hierarchical browse. Updates <see cref="LastBrowseResult"/>
        /// with the raw JSON-RPC response body on success.
        /// </summary>
        /// <param name="node">
        /// LMS browse node name, e.g. <c>artists</c>, <c>albums</c>, <c>genres</c>,
        /// <c>playlists</c>, <c>new_music</c>, <c>randomplay</c>, <c>radios</c>,
        /// <c>apps</c>, or <c>browselibrary</c>.
        /// </param>
        /// <param name="start">Zero-based paging offset.</param>
        /// <param name="count">Max items to return.</param>
        [EntityCommand(Id = "media:browse")]
        public void Browse(string node, int start, int count)
        {
            var body = LmsJsonRpcRequests.Browse(_macAddress, node, SanitizeStart(start), SanitizeCount(count));
            FireAndLogRpc(body, "media:browse");
        }

        [EntityCommand(Id = "media:browseFavorites")]
        public void BrowseFavorites(string parentItemId, int start, int count)
        {
            var body = LmsJsonRpcRequests.QueryFavorites(
                _macAddress,
                SanitizeStart(start),
                SanitizeCount(count),
                string.IsNullOrEmpty(parentItemId) ? null : parentItemId);
            FireAndLogRpc(body, "media:browseFavorites");
        }

        [EntityCommand(Id = "media:queryPlaylistTracks")]
        public void QueryPlaylistTracks(string playlistId, int start, int count)
        {
            if (string.IsNullOrEmpty(playlistId))
            {
                _log($"{_macAddress}: media:queryPlaylistTracks called with empty playlistId; ignoring.");
                return;
            }

            var body = LmsJsonRpcRequests.QueryPlaylistTracks(playlistId, SanitizeStart(start), SanitizeCount(count));
            FireAndLogRpc(body, "media:queryPlaylistTracks");
        }

        // ================================================================== CLI notifications in

        /// <summary>
        /// Apply a CLI message that was dispatched to this player. Called by
        /// <see cref="DriverMain"/> from the CLI receive loop.
        /// </summary>
        internal void ApplyPlayerMessage(LmsMessage message)
        {
            switch (message.Kind)
            {
                case LmsMessageKind.Play:
                    ApplyPlaybackState(Definitions.PlaybackState.Playing);
                    break;

                case LmsMessageKind.Pause:
                    {
                        if (message.Payload is bool isPaused)
                        {
                            ApplyPlaybackState(isPaused
                                ? Definitions.PlaybackState.Paused
                                : Definitions.PlaybackState.Playing);
                        }
                        else
                        {
                            // Toggle with no payload: safest to leave state alone.
                        }
                        break;
                    }

                case LmsMessageKind.Stop:
                    ApplyPlaybackState(Definitions.PlaybackState.Stopped);
                    break;

                case LmsMessageKind.Volume:
                    if (message.Payload is int v)
                    {
                        ApplyVolume(v);
                    }
                    break;

                case LmsMessageKind.Mute:
                    if (message.Payload is bool m)
                    {
                        ApplyMute(m);
                    }
                    break;

                case LmsMessageKind.Power:
                    if (message.Payload is bool p)
                    {
                        ApplyPower(p);
                    }
                    break;

                case LmsMessageKind.Time:
                    if (message.Payload is double seconds)
                    {
                        ApplyElapsed((int)seconds);
                    }
                    break;

                case LmsMessageKind.Repeat:
                    if (message.Payload is int r)
                    {
                        ApplyRepeat(ClampEnumInt(r, 2));
                    }
                    break;

                case LmsMessageKind.Shuffle:
                    if (message.Payload is int s)
                    {
                        ApplyShuffle(ClampEnumInt(s, 2));
                    }
                    break;

                case LmsMessageKind.NewSong:
                    if (message.Payload is NewSongPayload song)
                    {
                        ApplyNewSong(song);
                    }
                    break;

                case LmsMessageKind.Client:
                    if (message.Payload is string sub)
                    {
                        ApplyClient(sub);
                    }
                    break;

                // StatusResponse, PlayerRaw and others: nothing to do at v1.
                // A richer implementation would parse 'status' key:value pairs
                // to backfill full now-playing metadata on reconnect.
                default:
                    break;
            }
        }

        /// <summary>Mark this player as online/offline without touching other state.</summary>
        internal void SetOnline(bool online)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = _state.Online != online;
                _state.Online = online;
                Online = online;
            }

            if (changed)
            {
                NotifyPropertyChanged("lyrion:online", new DriverEntityValue(online));
            }
        }

        // ================================================================== State appliers

        private void ApplyPlaybackState(PlaybackState next)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = _state.Playback != next;
                _state.Playback = next;
                PlaybackState = next;
            }

            if (changed)
            {
                NotifyPropertyChanged("transport:playbackState", new DriverEntityValue((long)next));
            }
        }

        private void ApplyVolume(int volume)
        {
            if (volume < 0) volume = 0;
            if (volume > 100) volume = 100;

            bool changed;
            lock (_stateLock)
            {
                changed = _state.Volume != volume;
                _state.Volume = volume;
                Volume = volume;
            }

            if (changed)
            {
                NotifyPropertyChanged("audio:volume", new DriverEntityValue((long)volume));
            }
        }

        private void ApplyMute(bool muted)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = _state.Muted != muted;
                _state.Muted = muted;
                Muted = muted;
            }

            if (changed)
            {
                NotifyPropertyChanged("audio:muted", new DriverEntityValue(muted));
            }
        }

        private void ApplyPower(bool on)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = _state.Power != on;
                _state.Power = on;
                PowerOn = on;
            }

            if (changed)
            {
                NotifyPropertyChanged("power:on", new DriverEntityValue(on));
            }
        }

        private void ApplyRepeat(int mode)
        {
            var next = (RepeatMode)mode;
            bool changed;
            lock (_stateLock)
            {
                changed = _state.Repeat != next;
                _state.Repeat = next;
                RepeatMode = next;
            }

            if (changed)
            {
                NotifyPropertyChanged("lyrion:repeatMode", new DriverEntityValue((long)next));
            }
        }

        private void ApplyShuffle(int mode)
        {
            var next = (ShuffleMode)mode;
            bool changed;
            lock (_stateLock)
            {
                changed = _state.Shuffle != next;
                _state.Shuffle = next;
                ShuffleMode = next;
            }

            if (changed)
            {
                NotifyPropertyChanged("lyrion:shuffleMode", new DriverEntityValue((long)next));
            }
        }

        private void ApplyElapsed(int seconds)
        {
            if (seconds < 0)
            {
                seconds = 0;
            }

            bool changed;
            lock (_stateLock)
            {
                changed = _state.NowPlaying.ElapsedSec != seconds;
                _state.NowPlaying.ElapsedSec = seconds;
                ElapsedSec = seconds;
            }

            if (changed)
            {
                NotifyPropertyChanged("media:elapsedSec", new DriverEntityValue((long)seconds));
            }
        }

        private void ApplyNewSong(NewSongPayload song)
        {
            // We only get title + index from the newsong notification. Duration,
            // artist, album, artwork are refreshed by a follow-up status query
            // which DriverMain issues on every "newsong" event. For now, at
            // least update the title and playlist index immediately so the UI
            // feels responsive.
            string title = song.Title ?? string.Empty;
            int index = song.Index < 0 ? 0 : song.Index;

            bool titleChanged;
            bool indexChanged;
            lock (_stateLock)
            {
                titleChanged = !StringsEqual(_state.NowPlaying.Title, title);
                indexChanged = _state.NowPlaying.PlaylistIndex != index;

                _state.NowPlaying.Title = title;
                _state.NowPlaying.PlaylistIndex = index;
                _state.NowPlaying.ElapsedSec = 0;

                Title = title;
                PlaylistIndex = index;
                ElapsedSec = 0;
            }

            if (titleChanged)
            {
                NotifyPropertyChanged("media:title", new DriverEntityValue(title));
            }

            if (indexChanged)
            {
                NotifyPropertyChanged("media:playlistIndex", new DriverEntityValue((long)index));
            }

            NotifyPropertyChanged("media:elapsedSec", new DriverEntityValue(0L));
        }

        private void ApplyClient(string subCommand)
        {
            switch (subCommand)
            {
                case "new":
                case "reconnect":
                    SetOnline(true);
                    break;

                case "disconnect":
                case "forget":
                    SetOnline(false);
                    break;
            }
        }

        // ================================================================== Dispatch helpers

        private void FireAndLog(string cliLine, string commandName)
        {
            if (string.IsNullOrEmpty(cliLine))
            {
                return;
            }

            _ = SendCliSafe(cliLine, commandName);
        }

        private async Task SendCliSafe(string cliLine, string commandName)
        {
            try
            {
                var ok = await _sendCli(cliLine).ConfigureAwait(false);
                if (!ok)
                {
                    _log($"{_macAddress}: {commandName} not sent (not connected).");
                }
            }
            catch (Exception ex)
            {
                _log($"{_macAddress}: {commandName} failed: {ex.Message}");
            }
        }

        private void FireAndLogRpc(string jsonBody, string commandName)
        {
            if (string.IsNullOrEmpty(jsonBody))
            {
                return;
            }

            _ = SendRpcSafe(jsonBody, commandName);
        }

        private async Task SendRpcSafe(string jsonBody, string commandName)
        {
            try
            {
                var result = await _sendRpc(jsonBody, CancellationToken.None).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    _log($"{_macAddress}: {commandName} rpc failed: {result.Error}");
                    return;
                }

                ApplyBrowseResult(result.Body ?? string.Empty);
            }
            catch (Exception ex)
            {
                _log($"{_macAddress}: {commandName} threw: {ex.Message}");
            }
        }

        private void ApplyBrowseResult(string body)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = !StringsEqual(LastBrowseResult, body);
                LastBrowseResult = body;
            }

            if (changed)
            {
                NotifyPropertyChanged("media:lastBrowseResult", new DriverEntityValue(body));
            }
        }

        private static int SanitizeStart(int start)
        {
            return start < 0 ? 0 : start;
        }

        private static int SanitizeCount(int count)
        {
            if (count <= 0) return 50;   // Reasonable default page
            if (count > 1000) return 1000; // Hard upper bound to bound memory
            return count;
        }

        private static int ClampEnumInt(int value, int maxInclusive)
        {
            if (value < 0) return 0;
            if (value > maxInclusive) return maxInclusive;
            return value;
        }

        private static bool StringsEqual(string a, string b)
        {
            return string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
