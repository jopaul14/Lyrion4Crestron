namespace Lyrion4Crestron.Common
{
    /// <summary>
    /// Constants for Lyrion Music Server CLI protocol communication.
    /// </summary>
    public static class LyrionConstants
    {
        /// <summary>
        /// Default CLI port for Lyrion Music Server.
        /// </summary>
        public const int DefaultCliPort = 9090;
 
        /// <summary>
        /// Delimiter used to terminate CLI commands and responses.
        /// </summary>
        public const string Delimiter = "\n";
 
        /// <summary>
        /// CLI command to query server status and connected players.
        /// </summary>
        public const string ServerStatusCommand = "serverstatus 0 100";
 
        /// <summary>
        /// CLI command to subscribe to player status changes.
        /// </summary>
        public const string SubscribeCommand = "subscribe playlist,mixer,pause,client";
 
        /// <summary>
        /// CLI command prefix for querying player status.
        /// </summary>
        public const string PlayerStatusCommand = "status 0 100 tags:adKlNJcx";
 
        // Transport control commands
        public const string PlayCommand = "play";
        public const string PauseCommand = "pause";
        public const string StopCommand = "stop";
        public const string NextTrackCommand = "playlist index +1";
        public const string PreviousTrackCommand = "playlist index -1";
 
        // Volume commands
        public const string VolumeSetCommand = "mixer volume";
        public const string MuteToggleCommand = "mixer muting toggle";
        public const string MuteOnCommand = "mixer muting 1";
        public const string MuteOffCommand = "mixer muting 0";
 
        // Repeat/shuffle commands
        public const string RepeatCommand = "playlist repeat";
        public const string ShuffleCommand = "playlist shuffle";
 
        // Power commands
        public const string PowerOnCommand = "power 1";
        public const string PowerOffCommand = "power 0";
    }
}