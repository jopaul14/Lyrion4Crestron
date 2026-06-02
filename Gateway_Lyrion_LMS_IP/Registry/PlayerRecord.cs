// ---------------------------------------------------------------------------
//  Gateway_Lyrion_LMS_IP - Lyrion Server gateway driver (Driver 1 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Gateway.Registry
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
            Presets = Array.Empty<LyrionPreset>();
        }

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

        // Presets (optional)
        public IReadOnlyList<LyrionPreset> Presets { get; set; }
    }
}
