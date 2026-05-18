// ---------------------------------------------------------------------------
//  Gateway_Lyrion_LMS_IP - Lyrion Server gateway driver (Driver 1 of 3)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;

namespace LyrionCommunity.Crestron.Lyrion.Gateway.Protocol
{
    /// <summary>
    /// Parses raw LMS CLI lines into strongly-typed messages. Stateless;
    /// callers buffer partial lines in the transport.
    /// </summary>
    internal static class LmsCliParser
    {
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
                if (raw[i].Length == 0) continue;
                result[count++] = LmsTokenCodec.Decode(raw[i]);
            }

            if (count == raw.Length) return result;

            var trimmed = new string[count];
            Array.Copy(result, 0, trimmed, 0, count);
            return trimmed;
        }

        public static LmsMessage Parse(string line)
        {
            var tokens = Tokenize(line);
            if (tokens.Length == 0)
            {
                return LmsMessage.Empty;
            }

            if (LooksLikeMac(tokens[0]))
            {
                return ParsePlayerMessage(tokens);
            }

            return ParseGlobalMessage(tokens);
        }

        private static readonly string[] EmptyTokens = new string[0];

        private static LmsMessage ParsePlayerMessage(string[] tokens)
        {
            var mac = NormalizeMac(tokens[0]);
            if (tokens.Length < 2)
            {
                return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
            }

            switch (tokens[1])
            {
                case "play":
                    return new LmsMessage(LmsMessageKind.Play, mac, tokens, null);

                case "pause":
                    {
                        bool? isPaused = null;
                        if (tokens.Length >= 3) isPaused = tokens[2] == "1";
                        return new LmsMessage(LmsMessageKind.Pause, mac, tokens, isPaused);
                    }

                case "stop":
                    return new LmsMessage(LmsMessageKind.Stop, mac, tokens, null);

                case "mixer":
                    return ParseMixer(mac, tokens);

                case "power":
                    {
                        bool? on = null;
                        if (tokens.Length >= 3) on = tokens[2] == "1";
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
            if (tokens.Length < 4)
            {
                return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
            }

            if (tokens[2] == "volume")
            {
                if (int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vol))
                {
                    if (vol < 0) vol = -vol;
                    if (vol > 100) vol = 100;
                    return new LmsMessage(LmsMessageKind.Volume, mac, tokens, vol);
                }
            }

            if (tokens[2] == "muting")
            {
                return new LmsMessage(LmsMessageKind.Mute, mac, tokens, tokens[3] == "1");
            }

            return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
        }

        private static LmsMessage ParsePrefset(string mac, string[] tokens)
        {
            if (tokens.Length >= 5 && tokens[2] == "server")
            {
                if (tokens[3] == "power")
                {
                    return new LmsMessage(LmsMessageKind.Power, mac, tokens, tokens[4] == "1");
                }

                if (tokens[3] == "volume"
                    && int.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vol))
                {
                    if (vol < 0) vol = -vol;
                    if (vol > 100) vol = 100;
                    return new LmsMessage(LmsMessageKind.Volume, mac, tokens, vol);
                }
            }

            return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
        }

        private static LmsMessage ParsePlaylist(string mac, string[] tokens)
        {
            if (tokens.Length < 3)
            {
                return new LmsMessage(LmsMessageKind.PlayerRaw, mac, tokens, null);
            }

            switch (tokens[2])
            {
                case "newsong":
                    {
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
                        if (tokens.Length >= 4) isPaused = tokens[3] == "1";
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
                    {
                        string version = null;
                        if (tokens.Length == 3 && tokens[1] == "?") version = tokens[2];
                        else if (tokens.Length >= 2) version = tokens[tokens.Length - 1];
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

        public static IDictionary<string, string> ExtractKeyValues(string[] tokens, int startIndex)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (tokens == null) return dict;

            for (var i = startIndex; i < tokens.Length; i++)
            {
                var t = tokens[i];
                if (string.IsNullOrEmpty(t)) continue;

                var colon = t.IndexOf(':');
                if (colon <= 0) continue;

                var key = t.Substring(0, colon);
                var value = colon == t.Length - 1 ? string.Empty : t.Substring(colon + 1);
                dict[key] = value;
            }

            return dict;
        }

        private static bool LooksLikeMac(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length != 17) return false;
            for (var i = 0; i < 17; i++)
            {
                var c = token[i];
                if ((i + 1) % 3 == 0)
                {
                    if (c != ':') return false;
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
            return mac?.ToLowerInvariant() ?? string.Empty;
        }
    }

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
        public string Mac { get; }
        public string[] Tokens { get; }
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
