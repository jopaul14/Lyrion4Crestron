// ---------------------------------------------------------------------------
//  Gateway_Lyrion_LMS_IP - Lyrion Server gateway driver (Driver 1 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Globalization;

namespace LyrionCommunity.Crestron.Lyrion.Gateway.Protocol
{
    /// <summary>
    /// Builders for the LMS CLI command strings this driver sends. The
    /// transport layer appends the line terminator.
    /// </summary>
    /// <remarks>
    /// Sleep is intentionally not exposed; refer to CLAUDE.md "EXPLICITLY
    /// REMOVED FEATURES". Boolean shuffle/repeat are mapped to LMS multi-state
    /// values per CLAUDE.md section B.
    /// </remarks>
    internal static class LmsCliCommands
    {
        // --- Global ------------------------------------------------------------

        public static string ListenAll() => "listen 1";

        public static string Login(string username, string password)
        {
            var u = LmsTokenCodec.Encode(username ?? string.Empty);
            var p = LmsTokenCodec.Encode(password ?? string.Empty);
            return "login " + u + " " + p;
        }

        public static string QueryServerVersion() => "version ?";

        public static string QueryPlayers(int start, int count)
        {
            return "players "
                + start.ToString(CultureInfo.InvariantCulture)
                + " "
                + count.ToString(CultureInfo.InvariantCulture);
        }

        // --- Per-player: transport --------------------------------------------

        public static string Play(string mac) => Player(mac) + " play";
        public static string Pause(string mac) => Player(mac) + " pause 1";
        public static string Unpause(string mac) => Player(mac) + " pause 0";
        public static string Stop(string mac) => Player(mac) + " stop";
        public static string NextTrack(string mac) => Player(mac) + " playlist jump +1";
        public static string PreviousTrack(string mac) => Player(mac) + " playlist jump -1";

        public static string SeekTo(string mac, int seconds)
        {
            var clamped = Math.Max(0, seconds);
            return Player(mac) + " time " + clamped.ToString(CultureInfo.InvariantCulture);
        }

        // --- Per-player: volume / mute ----------------------------------------

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

        // --- Per-player: power -------------------------------------------------

        public static string SetPower(string mac, bool on)
        {
            return Player(mac) + " power " + (on ? "1" : "0");
        }

        // --- Per-player: shuffle / repeat (boolean → multi-state mapping) -----

        // Shuffle ON → LMS "shuffle song" (1); OFF → 0.
        public static string SetShuffle(string mac, bool enabled)
        {
            return Player(mac) + " playlist shuffle " + (enabled ? "1" : "0");
        }

        // Repeat ON → LMS "repeat playlist" (2); OFF → 0.
        public static string SetRepeat(string mac, bool enabled)
        {
            return Player(mac) + " playlist repeat " + (enabled ? "2" : "0");
        }

        // --- Per-player: status query / preset --------------------------------

        /// <summary>
        /// Build a per-player status query. When <paramref name="subscribeSeconds"/>
        /// is greater than zero the query is issued as a <c>subscribe</c>: LMS
        /// replies immediately and then pushes a fresh full status whenever the
        /// player status changes, with the value acting as a maximum keep-alive
        /// interval (push-on-change, not polling). This is the authoritative,
        /// notification-format-independent way to track power and mode - it
        /// captures changes triggered at the device itself that the granular
        /// <c>listen</c> notifications can miss. A value of 0 is a one-shot.
        /// </summary>
        public static string QueryStatus(string mac, int subscribeSeconds = 0)
        {
            var subscribe = subscribeSeconds > 0
                ? " subscribe:" + subscribeSeconds.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

            return Player(mac) + " status - 1" + subscribe + " tags:galdtoNryu";
        }

        /// <summary>
        /// Prefixes an installer-configured command fragment with the player's
        /// MAC, e.g. <c>favorites playlist play item_id:2</c> becomes
        /// <c>aa:bb:cc:dd:ee:ff favorites playlist play item_id:2</c>.
        /// </summary>
        /// <remarks>
        /// The fragment is passed through verbatim — its spaces are the CLI's
        /// own token separators, so it must NOT be run through
        /// <see cref="LmsTokenCodec.Encode"/> as a single token. Sanitizing it
        /// (stripping the control characters that would let one configured
        /// value smuggle in a second CLI line) is the caller's job; see
        /// <c>LyrionGatewayServiceImpl.SendPlayerCommand</c>.
        /// </remarks>
        public static string PlayerCommand(string mac, string command)
        {
            return Player(mac) + " " + command;
        }

        // --- Internals --------------------------------------------------------

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
            if (v < 0) return 0;
            if (v > 100) return 100;
            return v;
        }
    }
}
