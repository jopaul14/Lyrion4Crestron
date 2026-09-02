// ---------------------------------------------------------------------------
//  Server_Lyrion_LMS_IP - Lyrion Server driver (Driver 1 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Server.Registry
{
    /// <summary>
    /// One player's authoritative state. Fields match CLAUDE.md "DRIVER 1
    /// INTERNAL PLAYER REGISTRY". Mutation is performed only inside the
    /// owning <see cref="PlayerRegistry"/> under its lock.
    /// </summary>
    internal sealed class PlayerRecord
    {
        public PlayerRecord(string macAddress)
        {
            MacAddress = macAddress;
            Name = string.Empty;
            LifecycleState = PlayerLifecycleState.Unknown;
            PlaybackState = LyrionPlaybackState.Stopped;
            Title = string.Empty;
            Artist = string.Empty;
            Album = string.Empty;
        }

        // Number of consumers currently bound to this MAC. Source, Helper, and
        // Receiver all bind the same player; the record must outlive any one
        // of them, so Unbind decrements and only removes at zero (1.0.12 —
        // before that, disposing or re-addressing one consumer deleted the
        // record out from under the other two).
        public int BindCount { get; set; }

        // True once a FULL status response has been applied to this record —
        // set by NoteStatusApplied after every field of the reply has been
        // noted, never by lifecycle alone. This is the only honest answer to
        // "has the Lyrion Server actually looked at this player": availability
        // is not (it flips on `client new/reconnect` with no status at all,
        // and in ApplyStatusResponse it flips before the power field is
        // parsed). Consumers force-publish a bind-time snapshot only when
        // this is true. Sticky: a later availability loss lowers the fields
        // (see PlayerRegistry.LowerForUnavailable_NoLock) but the record has
        // still been observed, so publishing that lowered state is honest.
        public bool HasObservedState { get; set; }

        // Identity & capabilities
        public string MacAddress { get; }
        public string Name { get; set; }
        public string PlayerId { get; set; }
        public bool CanPowerOff { get; set; } = true;
        public bool SupportsVolume { get; set; } = true;

        // Lifecycle
        public PlayerLifecycleState LifecycleState { get; set; }
        public DateTime LastSeenUtc { get; set; }

        // Derived
        public bool IsAvailable { get; set; }
        public bool IsPoweredOn { get; set; }

        // True once LMS has reported an explicit power state for this player
        // (via a "power"/"prefset power" notification or the status "power"
        // field). When set, the playback-derived power fallback must not pull
        // power off for an idle (stopped) player — the explicit state wins.
        public bool HasExplicitPower { get; set; }

        // Playback
        public LyrionPlaybackState PlaybackState { get; set; }
        public int PositionSeconds { get; set; }
        public int DurationSeconds { get; set; }

        // Volume / mute
        public int Volume { get; set; }
        public bool Muted { get; set; }

        // Configured step-up/step-down amount (1–50), published by the Receiver
        // from its VolumeStep user attribute so other consumers (Helper) match it.
        public int VolumeStep { get; set; } = 2;

        // Modes
        public bool ShuffleEnabled { get; set; }
        public bool RepeatEnabled { get; set; }

        // Metadata
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public int TrackNumber { get; set; }
        public bool IsFrozen { get; set; }
        public DateTime FrozenAtUtc { get; set; }
        public DateTime LastMetadataUpdateUtc { get; set; }
    }
}
