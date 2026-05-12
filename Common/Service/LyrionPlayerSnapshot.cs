// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System.Collections.Generic;

namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>
    /// Immutable point-in-time view of one player. Returned to consumers from
    /// <see cref="ILyrionGatewayService.TryGetSnapshot"/> so a newly-bound
    /// Media / Volume driver can paint its UI without waiting for the next
    /// event tick.
    /// </summary>
    public sealed class LyrionPlayerSnapshot
    {
        public LyrionPlayerSnapshot(
            string mac,
            bool isAvailable,
            bool isPoweredOn,
            LyrionPlaybackState playbackState,
            int volume,
            bool muted,
            bool shuffleEnabled,
            bool repeatEnabled,
            LyrionMetadata metadata,
            IReadOnlyList<LyrionPreset> presets)
        {
            Mac = mac ?? string.Empty;
            IsAvailable = isAvailable;
            IsPoweredOn = isPoweredOn;
            PlaybackState = playbackState;
            Volume = volume;
            Muted = muted;
            ShuffleEnabled = shuffleEnabled;
            RepeatEnabled = repeatEnabled;
            Metadata = metadata ?? LyrionMetadata.Empty;
            Presets = presets ?? System.Array.Empty<LyrionPreset>();
        }

        public string Mac { get; }
        public bool IsAvailable { get; }
        public bool IsPoweredOn { get; }
        public LyrionPlaybackState PlaybackState { get; }
        public int Volume { get; }
        public bool Muted { get; }
        public bool ShuffleEnabled { get; }
        public bool RepeatEnabled { get; }
        public LyrionMetadata Metadata { get; }
        public IReadOnlyList<LyrionPreset> Presets { get; }
    }
}
