// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;

namespace LyrionCommunity.Crestron.Lyrion.Protocol
{
    /// <summary>
    /// Parses raw LMS CLI lines into strongly-typed messages.
    /// </summary>
    /// <remarks>
    /// LMS CLI lines are space-separated percent-encoded tokens. The first
    /// token is usually a player ID (percent-encoded MAC) or a global
    /// command name (e.g. <c>version</c>, <c>players</c>). This class does
    /// not maintain any state - callers should buffer partial lines in the
    /// transport layer and feed complete lines here.
    /// </remarks>
    internal static class LmsCliParser
    {
        /// <summary>
        /// Split a raw CLI line into its decoded tokens. Empty or whitespace
        /// lines return an empty array. Percent-decoding is applied to each
        /// token individually.
        /// </summary>
        public static string[] Tokenize(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return EmptyTokens;
            }

            var raw = line.Split(' ');
            var result = new string[raw.Length];
            var count = 0;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i].Length == 0)
                {
                    continue;
                }

                result[count++] = LmsTokenCodec.Decode(raw[i]);
            }

            if (count == raw.Length)
            {
                return result;
            }

            var trimmed = new string[count];
            Array.Copy(result, 0, trimmed, 0, count);
            return trimmed;
        }

        /// <summary>
        /// Parse a raw CLI line into a <see cref="LmsMessage"/>. Returns a
        /// message with <see cref="LmsMessage.Kind"/>==<see cref="LmsMessageKind.Unknown"/>
        /// for lines we don't recognise rather than throwing.
        /// </summary>
        public static LmsMessage Parse(string line)
        {
            var tokens = Tokenize(line);
            if (tokens.Length == 0)
            {
                return LmsMessage.Empty;
            }

            // Global commands / responses (version ?, players 0 N, ...) have no
            // MAC prefix. Heuristic: if the first token looks like a MAC, treat
            // it as a player message; otherwise treat as global.
            if (LooksLikeMac(tokens[0]))
            {
                return ParsePlayerMessage(tokens);
            }

            return ParseGlobalMessage(tokens);
        }

        // --- Internals ---------------------------------------------------------

        private static readonly string[] EmptyTokens = new string[0];

        private static LmsMessage ParsePlayerMessage(string[] tokens)
        {
            var mac = NormalizeMac(tokens[0]);
            if (tokens.Length < 2)
            {
                return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
            }

            var verb = tokens[1];
            // We deliberately parse only the notifications the driver acts on
            // here. Unknown verbs return PlayerRaw so the dispatcher can
            // forward them to diagnostic logging.
            switch (verb)
            {
                case "play":
                    return new LmsMessage(LmsMessageKind.Play, mac, tokens, null);

                case "pause":
                    {
                        // "<mac> pause 1" or "<mac> pause 0" or plain "<mac> pause"
                        bool? isPaused = null;
                        if (tokens.Length >= 3)
                        {
                            isPaused = tokens[2] == "1";
                        }
                        return new LmsMessage(LmsMessageKind.Pause, mac, tokens, isPaused);
                    }

                case "stop":
                    return new LmsMessage(LmsMessageKind.Stop, mac, tokens, null);

                case "mixer":
                    return ParseMixer(mac, tokens);

                case "power":
                    {
                        bool? on = null;
                        if (tokens.Length >= 3)
                        {
                            on = tokens[2] == "1";
                        }
                        return new LmsMessage(LmsMessageKind.Power, mac, tokens, on);
                    }

                case "prefset":
                    return ParsePrefset(mac, tokens);

                case "playlist":
                    return ParsePlaylist(mac, tokens);

                case "time":
                    {
                        double? seconds = null;
                        if (tokens.Length >= 3 && double.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                        {
                            seconds = t;
                        }
                        return new LmsMessage(LmsMessageKind.Time, mac, tokens, seconds);
                    }

                case "client":
                    {
                        // "<mac> client new|disconnect|forget|reconnect"
                        var sub = tokens.Length >= 3 ? tokens[2] : string.Empty;
                        return new LmsMessage(LmsMessageKind.Client, mac, tokens, sub);
                    }

                case "status":
                    return new LmsMessage(LmsMessageKind.StatusResponse, mac, tokens, null);

                default:
                    return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
            }
        }

        private static LmsMessage ParseMixer(string mac, string[] tokens)
        {
            // "<mac> mixer volume <level>" or "<mac> mixer muting <0|1>"
            if (tokens.Length < 4)
            {
                return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
            }

            if (tokens[2] == "volume")
            {
                if (int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vol))
                {
                    // LMS reports negative volumes to mean "muted". Clamp to non-negative.
                    if (vol < 0)
                    {
                        vol = -vol;
                    }

                    if (vol > 100)
                    {
                        vol = 100;
                    }

                    return new LmsMessage(LmsMessageKind.Volume, mac, tokens, vol);
                }
            }

            if (tokens[2] == "muting")
            {
                var muted = tokens[3] == "1";
                return new LmsMessage(LmsMessageKind.Mute, mac, tokens, muted);
            }

            return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
        }

        private static LmsMessage ParsePrefset(string mac, string[] tokens)
        {
            // "<mac> prefset server power <0|1>" - power change via preference
            // "<mac> prefset server volume <n>"  - volume change via preference
            if (tokens.Length >= 5 && tokens[2] == "server")
            {
                if (tokens[3] == "power")
                {
                    var on = tokens[4] == "1";
                    return new LmsMessage(LmsMessageKind.Power, mac, tokens, on);
                }

                if (tokens[3] == "volume"
                    && int.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vol))
                {
                    if (vol < 0)
                    {
                        vol = -vol;
                    }
                    return new LmsMessage(LmsMessageKind.Volume, mac, tokens, vol);
                }
            }

            return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
        }

        private static LmsMessage ParsePlaylist(string mac, string[] tokens)
        {
            // "<mac> playlist newsong <title> <index>"
            // "<mac> playlist repeat <0|1|2>"
            // "<mac> playlist shuffle <0|1|2>"
            // "<mac> playlist pause <0|1>"
            if (tokens.Length < 3)
            {
                return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
            }

            switch (tokens[2])
            {
                case "newsong":
                    {
                        // payload: (title, index)
                        string title = tokens.Length >= 4 ? tokens[3] : null;
                        int index = 0;
                        if (tokens.Length >= 5)
                        {
                            int.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
                        }
                        return new LmsMessage(LmsMessageKind.NewSong, mac, tokens, new NewSongPayload(title, index));
                    }

                case "repeat":
                    {
                        int mode = 0;
                        if (tokens.Length >= 4)
                        {
                            int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out mode);
                        }
                        return new LmsMessage(LmsMessageKind.Repeat, mac, tokens, mode);
                    }

                case "shuffle":
                    {
                        int mode = 0;
                        if (tokens.Length >= 4)
                        {
                            int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out mode);
                        }
                        return new LmsMessage(LmsMessageKind.Shuffle, mac, tokens, mode);
                    }

                case "pause":
                    {
                        bool? isPaused = null;
                        if (tokens.Length >= 4)
                        {
                            isPaused = tokens[3] == "1";
                        }
                        return new LmsMessage(LmsMessageKind.Pause, mac, tokens, isPaused);
                    }

                default:
                    return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
            }
        }

        private static LmsMessage ParseGlobalMessage(string[] tokens)
        {
            switch (tokens[0])
            {
                case "version":
                    // "version ? <version-string>"  or  "version <version-string>"
                    {
                        string version = null;
                        if (tokens.Length == 3 && tokens[1] == "?")
                        {
                            version = tokens[2];
                        }
                        else if (tokens.Length >= 2)
                        {
                            version = tokens[tokens.Length - 1];
                        }
                        return new LmsMessage(LmsMessageKind.ServerVersion, null, tokens, version);
                    }

                case "listen":
                    return new LmsMessage(LmsMessageKind.ListenAck, null, tokens, null);

                case "login":
                    return new LmsMessage(LmsMessageKind.LoginAck, null, tokens, null);

                case "players":
                    return new LmsMessage(LmsMessageKind.PlayersResponse, null, tokens, null);

                default:
                    return new LmsMessage(LmsMessageKind.GlobalRaw, null, tokens, null);
            }
        }

        /// <summary>
        /// Extract <c>key:value</c> pairs from the tail end of a status-style
        /// response. Returns null values for keys that appeared with no value.
        /// </summary>
        public static IDictionary<string, string> ExtractKeyValues(string[] tokens, int startIndex)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (tokens == null)
            {
                return dict;
            }

            for (var i = startIndex; i < tokens.Length; i++)
            {
                var t = tokens[i];
                if (string.IsNullOrEmpty(t))
                {
                    continue;
                }

                var colonIndex = t.IndexOf(':');
                if (colonIndex <= 0)
                {
                    continue;
                }

                var key = t.Substring(0, colonIndex);
                var value = colonIndex == t.Length - 1 ? string.Empty : t.Substring(colonIndex + 1);
                dict[key] = value;
            }

            return dict;
        }

        private static bool LooksLikeMac(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            // Canonical MACs are 17 chars (aa:bb:cc:dd:ee:ff) after decoding.
            // After decode our tokenizer already handled percent-encoding, so
            // we just check the hex/colon pattern.
            if (token.Length != 17)
            {
                return false;
            }

            for (var i = 0; i < 17; i++)
            {
                var c = token[i];
                if ((i + 1) % 3 == 0)
                {
                    if (c != ':')
                    {
                        return false;
                    }
                }
                else if (!IsHex(c))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private static string NormalizeMac(string mac)
        {
            // Decoded MAC from LMS arrives as 00:04:20:aa:bb:cc (lowercase conventional).
            // Normalize casing so downstream comparisons are predictable.
            return mac?.ToLowerInvariant() ?? string.Empty;
        }
    }

    /// <summary>One parsed LMS CLI message.</summary>
    internal readonly struct LmsMessage
    {
        public LmsMessage(LmsMessageKind kind, string mac, string[] tokens, object payload)
        {
            Kind = kind;
            Mac = mac;
            Tokens = tokens ?? new string[0];
            Payload = payload;
        }

        public LmsMessageKind Kind { get; }

        /// <summary>Player MAC if the message is player-scoped, otherwise null.</summary>
        public string Mac { get; }

        /// <summary>Decoded tokens (no percent-encoding) in their original order.</summary>
        public string[] Tokens { get; }

        /// <summary>
        /// Strongly-typed payload for the common cases: int (volume, repeat,
        /// shuffle), bool (pause, mute, power), double (time), string (version,
        /// client subcommand), or <see cref="NewSongPayload"/>. May be null.
        /// </summary>
        public object Payload { get; }

        public static readonly LmsMessage Empty = new LmsMessage(LmsMessageKind.Empty, null, null, null);
    }

    internal enum LmsMessageKind
    {
        Empty,
        Unknown,
        GlobalRaw,
        PlayerRaw,
        ServerVersion,
        ListenAck,
        LoginAck,
        PlayersResponse,
        Play,
        Pause,
        Stop,
        Volume,
        Mute,
        Power,
        Time,
        Client,
        NewSong,
        Repeat,
        Shuffle,
        StatusResponse
    }

    internal readonly struct NewSongPayload
    {
        public NewSongPayload(string title, int index)
        {
            Title = title;
            Index = index;
        }

        public string Title { get; }

        public int Index { get; }
    }
}
