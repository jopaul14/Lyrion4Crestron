// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Text;
using LyrionCommunity.Crestron.Lyrion.Definitions;

namespace LyrionCommunity.Crestron.Lyrion.Protocol
{
    /// <summary>
    /// Builders for the LMS CLI command strings this driver sends.
    /// All methods return a command WITHOUT a trailing newline; the transport
    /// layer is responsible for appending the line terminator.
    /// </summary>
    /// <remarks>
    /// Reference: <a href="https://lyrion.org/reference/cli/">LMS CLI documentation</a>.
    /// </remarks>
    internal static class LmsCliCommands
    {
        // --- Global commands (no player) ---------------------------------------

        /// <summary>Subscribe to unsolicited notifications from all players.</summary>
        public static string ListenAll()
        {
            return "listen 1";
        }

        /// <summary>Authenticate with LMS. Used when the server requires auth.</summary>
        public static string Login(string username, string password)
        {
            var u = LmsTokenCodec.Encode(username ?? string.Empty);
            var p = LmsTokenCodec.Encode(password ?? string.Empty);
            return "login " + u + " " + p;
        }

        /// <summary>Query the LMS server version string.</summary>
        public static string QueryServerVersion()
        {
            return "version ?";
        }

        /// <summary>Query the list of all players known to LMS (paged).</summary>
        public static string QueryPlayers(int start, int count)
        {
            return "players "
                + start.ToString(CultureInfo.InvariantCulture)
                + " "
                + count.ToString(CultureInfo.InvariantCulture);
        }

        // --- Per-player: transport ---------------------------------------------

        public static string Play(string mac) => Player(mac) + " play";
        public static string Pause(string mac) => Player(mac) + " pause 1";
        public static string Unpause(string mac) => Player(mac) + " pause 0";
        public static string Stop(string mac) => Player(mac) + " stop";
        public static string NextTrack(string mac) => Player(mac) + " playlist jump +1";
        public static string PreviousTrack(string mac) => Player(mac) + " playlist jump -1";

        // --- Per-player: volume / mute -----------------------------------------

        public static string SetVolume(string mac, int volume)
        {
            var clamped = ClampVolume(volume);
            return Player(mac) + " mixer volume " + clamped.ToString(CultureInfo.InvariantCulture);
        }

        public static string VolumeUp(string mac, int step)
        {
            var s = Math.Max(1, step);
            return Player(mac) + " mixer volume +" + s.ToString(CultureInfo.InvariantCulture);
        }

        public static string VolumeDown(string mac, int step)
        {
            var s = Math.Max(1, step);
            return Player(mac) + " mixer volume -" + s.ToString(CultureInfo.InvariantCulture);
        }

        public static string SetMute(string mac, bool muted)
        {
            return Player(mac) + " mixer muting " + (muted ? "1" : "0");
        }

        public static string ToggleMute(string mac)
        {
            return Player(mac) + " mixer muting toggle";
        }

        // --- Per-player: power / sleep -----------------------------------------

        public static string SetPower(string mac, bool on)
        {
            return Player(mac) + " power " + (on ? "1" : "0");
        }

        /// <summary>
        /// Fade the player down and stop after <paramref name="seconds"/>. A
        /// value of 0 cancels any active sleep timer.
        /// </summary>
        public static string Sleep(string mac, int seconds)
        {
            var s = Math.Max(0, seconds);
            return Player(mac) + " sleep " + s.ToString(CultureInfo.InvariantCulture);
        }

        // --- Per-player: repeat / shuffle --------------------------------------

        public static string SetRepeat(string mac, RepeatMode mode)
        {
            return Player(mac) + " playlist repeat " + ((int)mode).ToString(CultureInfo.InvariantCulture);
        }

        public static string SetShuffle(string mac, ShuffleMode mode)
        {
            return Player(mac) + " playlist shuffle " + ((int)mode).ToString(CultureInfo.InvariantCulture);
        }

        // --- Per-player: queue / favorites -------------------------------------

        /// <summary>Replace the queue with a saved playlist and start playing.</summary>
        public static string PlayPlaylist(string mac, string playlistId)
        {
            return Player(mac) + " playlistcontrol cmd:load playlist_id:" + LmsTokenCodec.Encode(playlistId);
        }

        /// <summary>Replace the queue with a favorite and start playing.</summary>
        public static string PlayFavorite(string mac, string favoriteItemId)
        {
            return Player(mac) + " favorites playlist play item_id:" + LmsTokenCodec.Encode(favoriteItemId);
        }

        // --- Per-player: status query ------------------------------------------

        /// <summary>
        /// Query a full status snapshot for the player (used to re-sync after
        /// reconnect). <c>tags:galdIKoNcryu</c> requests the metadata fields
        /// this driver exposes.
        /// </summary>
        public static string QueryStatus(string mac)
        {
            return Player(mac) + " status - 1 tags:galdIKoNcryu";
        }

        // --- Internal helpers --------------------------------------------------

        /// <summary>
        /// Build a command where the playerid is a literal string (not a MAC).
        /// Used in tests and for injection of protocol-level commands.
        /// </summary>
        public static string Raw(string playerid, string body)
        {
            var pid = LmsTokenCodec.Encode(playerid ?? string.Empty);
            var sb = new StringBuilder();
            sb.Append(pid);
            if (!string.IsNullOrEmpty(body))
            {
                sb.Append(' ');
                sb.Append(body);
            }
            return sb.ToString();
        }

        private static string Player(string mac)
        {
            if (string.IsNullOrEmpty(mac))
            {
                throw new ArgumentException("Player MAC address is required.", nameof(mac));
            }

            return LmsTokenCodec.EncodeMac(mac);
        }

        private static int ClampVolume(int v)
        {
            if (v < 0)
            {
                return 0;
            }

            if (v > 100)
            {
                return 100;
            }

            return v;
        }
    }
}
