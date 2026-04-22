// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

namespace LyrionCommunity.Crestron.Lyrion.Models
{
    /// <summary>
    /// Snapshot of the metadata for the track a player is currently rendering.
    /// All string fields may be null/empty - LMS commonly omits fields for
    /// radio, podcast, and internet-stream content.
    /// </summary>
    public sealed class NowPlayingInfo
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public string ArtworkUrl { get; set; }
        public int DurationSec { get; set; }
        public int ElapsedSec { get; set; }
        public bool IsRemote { get; set; }
        public string StationName { get; set; }
        public int PlaylistIndex { get; set; }
        public int PlaylistLength { get; set; }

        public NowPlayingInfo Clone()
        {
            return new NowPlayingInfo
            {
                Title = Title,
                Artist = Artist,
                Album = Album,
                ArtworkUrl = ArtworkUrl,
                DurationSec = DurationSec,
                ElapsedSec = ElapsedSec,
                IsRemote = IsRemote,
                StationName = StationName,
                PlaylistIndex = PlaylistIndex,
                PlaylistLength = PlaylistLength
            };
        }
    }
}
