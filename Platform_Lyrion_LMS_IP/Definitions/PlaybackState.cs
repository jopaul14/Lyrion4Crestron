// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using Crestron.DeviceDrivers.SDK.EntityModel.Attributes;

namespace LyrionCommunity.Crestron.Lyrion.Definitions
{
    /// <summary>Transport playback states reported by a player.</summary>
    [EntityDataType(Id = "transport:PlaybackState")]
    public enum PlaybackState
    {
        /// <summary>Player has nothing in its queue or state is unknown.</summary>
        Stopped,

        /// <summary>Player is paused.</summary>
        Paused,

        /// <summary>Player is actively playing.</summary>
        Playing
    }

    /// <summary>Repeat modes as reported / accepted by LMS (<c>playlist repeat 0|1|2</c>).</summary>
    [EntityDataType(Id = "lyrion:RepeatMode")]
    public enum RepeatMode
    {
        /// <summary>No repeat.</summary>
        Off = 0,

        /// <summary>Repeat the currently-playing song.</summary>
        Song = 1,

        /// <summary>Repeat the whole playlist.</summary>
        Playlist = 2
    }

    /// <summary>Shuffle modes as reported / accepted by LMS (<c>playlist shuffle 0|1|2</c>).</summary>
    [EntityDataType(Id = "lyrion:ShuffleMode")]
    public enum ShuffleMode
    {
        /// <summary>No shuffle.</summary>
        Off = 0,

        /// <summary>Shuffle songs within the playlist.</summary>
        Song = 1,

        /// <summary>Shuffle whole albums.</summary>
        Album = 2
    }
}
