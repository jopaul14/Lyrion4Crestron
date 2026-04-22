// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using LyrionCommunity.Crestron.Lyrion.Definitions;

namespace LyrionCommunity.Crestron.Lyrion.Models
{
    /// <summary>
    /// Authoritative state for a single player. Owned by <see cref="Lyrion.PlayerEntity"/>.
    /// Updates should be applied inside the entity's state lock before notifying property changes.
    /// </summary>
    public sealed class PlayerState
    {
        public PlaybackState Playback { get; set; } = PlaybackState.Stopped;
        public int Volume { get; set; }
        public bool Muted { get; set; }
        public bool Power { get; set; }
        public RepeatMode Repeat { get; set; } = RepeatMode.Off;
        public ShuffleMode Shuffle { get; set; } = ShuffleMode.Off;
        public bool Online { get; set; }
        public NowPlayingInfo NowPlaying { get; set; } = new NowPlayingInfo();
    }
}
