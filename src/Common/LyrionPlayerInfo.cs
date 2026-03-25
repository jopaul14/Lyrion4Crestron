namespace Lyrion4Crestron.Common
{
    /// <summary>
    /// Represents information about a Lyrion/Squeezebox player discovered on the network.
    /// </summary>
    public class LyrionPlayerInfo
    {
        /// <summary>
        /// MAC address of the player, used as the unique identifier.
        /// </summary>
        public string PlayerId { get; set; }
 
        /// <summary>
        /// Display name of the player.
        /// </summary>
        public string Name { get; set; }
 
        /// <summary>
        /// Model name of the player hardware.
        /// </summary>
        public string Model { get; set; }
 
        /// <summary>
        /// IP address of the player.
        /// </summary>
        public string IpAddress { get; set; }
 
        /// <summary>
        /// Whether the player is currently powered on.
        /// </summary>
        public bool IsPowered { get; set; }
 
        /// <summary>
        /// Whether the player is connected to the server.
        /// </summary>
        public bool IsConnected { get; set; }
 
        /// <summary>
        /// Current volume level (0-100).
        /// </summary>
        public int Volume { get; set; }
 
        /// <summary>
        /// Current playback mode: play, pause, or stop.
        /// </summary>
        public string Mode { get; set; }
 
        /// <summary>
        /// Title of the currently playing track.
        /// </summary>
        public string CurrentTitle { get; set; }
 
        /// <summary>
        /// Artist of the currently playing track.
        /// </summary>
        public string CurrentArtist { get; set; }
 
        /// <summary>
        /// Album of the currently playing track.
        /// </summary>
        public string CurrentAlbum { get; set; }
 
        /// <summary>
        /// URL for artwork of the currently playing track.
        /// </summary>
        public string ArtworkUrl { get; set; }
 
        /// <summary>
        /// Duration of the current track in seconds.
        /// </summary>
        public double Duration { get; set; }
 
        /// <summary>
        /// Current playback position in seconds.
        /// </summary>
        public double Time { get; set; }
 
        /// <summary>
        /// Current repeat mode: 0=off, 1=song, 2=playlist.
        /// </summary>
        public int RepeatMode { get; set; }
 
        /// <summary>
        /// Current shuffle mode: 0=off, 1=songs, 2=albums.
        /// </summary>
        public int ShuffleMode { get; set; }
 
        /// <summary>
        /// Whether the player is currently muted.
        /// </summary>
        public bool IsMuted { get; set; }
    }
}