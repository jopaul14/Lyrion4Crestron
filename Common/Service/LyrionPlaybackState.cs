// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>Playback state surfaced to Source and Helper driver consumers.</summary>
    public enum LyrionPlaybackState
    {
        Stopped = 0,
        Paused = 1,
        Playing = 2
    }
}
