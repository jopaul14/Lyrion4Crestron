// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>
    /// Cross-driver contract published by the Gateway driver (Driver 1) and
    /// consumed by the Media (Driver 2) and Volume (Driver 3) drivers. All
    /// state mutation goes through this interface; Drivers 2 and 3 never
    /// open sockets to LMS.
    /// </summary>
    /// <remarks>
    /// Event semantics:
    /// <list type="bullet">
    /// <item>Events MAY be raised on a background thread; consumers must not
    /// block.</item>
    /// <item>Events are only published for MACs the gateway knows about. A
    /// consumer that subscribes before binding will simply not receive
    /// events until <see cref="BindPlayer"/> succeeds.</item>
    /// <item>The MAC string passed to each event is the lowercase
    /// colon-separated canonical form (<c>aa:bb:cc:dd:ee:ff</c>).</item>
    /// </list>
    /// Command semantics:
    /// <list type="bullet">
    /// <item>Commands are best-effort and never throw on transport errors.</item>
    /// <item>Commands are dropped silently if the gateway is not currently
    /// CONNECTED to LMS. The next state-change event will reflect reality.</item>
    /// </list>
    /// </remarks>
    public interface ILyrionGatewayService
    {
        // ===== Bind / unbind =====

        /// <summary>
        /// Registers a MAC the consumer cares about. Returns false if the
        /// MAC is malformed. Idempotent; subsequent calls with the same MAC
        /// are no-ops. The Gateway uses the bound MAC set to decide which
        /// players to maintain registry records for.
        /// </summary>
        bool BindPlayer(string mac);

        /// <summary>Reverse of <see cref="BindPlayer"/>. Idempotent.</summary>
        void UnbindPlayer(string mac);

        // ===== Snapshot =====

        bool TryGetSnapshot(string mac, out LyrionPlayerSnapshot snapshot);

        // ===== Events (Driver 1 → Drivers 2/3) =====

        event Action<string, bool> AvailabilityChanged;
        event Action<string, bool> PowerStateChanged;
        event Action<string, LyrionPlaybackState> PlaybackStateChanged;
        event Action<string, LyrionMetadata> MetadataUpdated;
        event Action<string, bool> ShuffleChanged;
        event Action<string, bool> RepeatChanged;
        event Action<string, int> VolumeChanged;
        event Action<string, bool> MuteChanged;
        event Action<string, IReadOnlyList<LyrionPreset>> PresetsUpdated;

        // ===== Commands from Driver 2 (Media Source) =====

        void Play(string mac);
        void Pause(string mac);
        void Stop(string mac);
        void Next(string mac);
        void Previous(string mac);
        void Seek(string mac, int positionSeconds);

        void SetShuffle(string mac, bool enabled);
        void SetRepeat(string mac, bool enabled);

        void PowerOn(string mac);
        void PowerOff(string mac);
        void PowerToggle(string mac);

        void ActivatePreset(string mac, string presetId);

        // ===== Commands from Driver 3 (Volume Receiver) =====

        void SetVolume(string mac, int level);
        void VolumeUp(string mac, int step);
        void VolumeDown(string mac, int step);
        void SetMute(string mac, bool muted);
    }
}
