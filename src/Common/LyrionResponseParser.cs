using System;
using System.Collections.Generic;
 
namespace Lyrion4Crestron.Common
{
    /// <summary>
    /// Parses URL-encoded CLI responses from Lyrion Music Server.
    /// </summary>
    public static class LyrionResponseParser
    {
        /// <summary>
        /// Decodes a URL-encoded string from the LMS CLI protocol.
        /// </summary>
        public static string UrlDecode(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                return encoded;
 
            var result = new System.Text.StringBuilder(encoded.Length);
            for (int i = 0; i < encoded.Length; i++)
            {
                if (encoded[i] == '%' && i + 2 < encoded.Length)
                {
                    var hex = encoded.Substring(i + 1, 2);
                    try
                    {
                        var ch = (char)Convert.ToInt32(hex, 16);
                        result.Append(ch);
                        i += 2;
                    }
                    catch
                    {
                        result.Append(encoded[i]);
                    }
                }
                else
                {
                    result.Append(encoded[i]);
                }
            }
            return result.ToString();
        }
 
        /// <summary>
        /// URL-encodes a string for use in LMS CLI commands.
        /// </summary>
        public static string UrlEncode(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;
 
            var result = new System.Text.StringBuilder(value.Length * 2);
            foreach (char c in value)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
                {
                    result.Append(c);
                }
                else
                {
                    result.AppendFormat("%{0:X2}", (int)c);
                }
            }
            return result.ToString();
        }
 
        /// <summary>
        /// Parses a space-delimited, URL-encoded key:value response into a dictionary.
        /// </summary>
        public static Dictionary<string, string> ParseTaggedResponse(string response)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(response))
                return result;
 
            var parts = response.Split(' ');
            foreach (var part in parts)
            {
                var decoded = UrlDecode(part);
                var colonIndex = decoded.IndexOf(':');
                if (colonIndex > 0)
                {
                    var key = decoded.Substring(0, colonIndex);
                    var value = decoded.Substring(colonIndex + 1);
                    result[key] = value;
                }
            }
            return result;
        }
 
        /// <summary>
        /// Parses a serverstatus response to extract player information.
        /// </summary>
        public static List<LyrionPlayerInfo> ParseServerStatus(string response)
        {
            var players = new List<LyrionPlayerInfo>();
            if (string.IsNullOrEmpty(response))
                return players;
 
            var parts = response.Split(' ');
            LyrionPlayerInfo currentPlayer = null;
 
            foreach (var part in parts)
            {
                var decoded = UrlDecode(part);
                var colonIndex = decoded.IndexOf(':');
                if (colonIndex <= 0)
                    continue;
 
                var key = decoded.Substring(0, colonIndex);
                var value = decoded.Substring(colonIndex + 1);
 
                if (key == "playerid")
                {
                    currentPlayer = new LyrionPlayerInfo { PlayerId = value };
                    players.Add(currentPlayer);
                }
                else if (currentPlayer != null)
                {
                    switch (key)
                    {
                        case "name":
                            currentPlayer.Name = value;
                            break;
                        case "model":
                            currentPlayer.Model = value;
                            break;
                        case "ip":
                            currentPlayer.IpAddress = value;
                            break;
                        case "power":
                            currentPlayer.IsPowered = value == "1";
                            break;
                        case "connected":
                            currentPlayer.IsConnected = value == "1";
                            break;
                    }
                }
            }
 
            return players;
        }
 
        /// <summary>
        /// Parses a player status response to update player info.
        /// </summary>
        public static void ParsePlayerStatus(string response, LyrionPlayerInfo player)
        {
            if (string.IsNullOrEmpty(response) || player == null)
                return;
 
            var tags = ParseTaggedResponse(response);
 
            if (tags.ContainsKey("mixer volume"))
            {
                int vol;
                if (int.TryParse(tags["mixer volume"], out vol))
                    player.Volume = vol;
            }
 
            if (tags.ContainsKey("mode"))
                player.Mode = tags["mode"];
 
            if (tags.ContainsKey("title"))
                player.CurrentTitle = tags["title"];
 
            if (tags.ContainsKey("artist"))
                player.CurrentArtist = tags["artist"];
 
            if (tags.ContainsKey("album"))
                player.CurrentAlbum = tags["album"];
 
            if (tags.ContainsKey("artwork_url"))
                player.ArtworkUrl = tags["artwork_url"];
 
            if (tags.ContainsKey("duration"))
            {
                double dur;
                if (double.TryParse(tags["duration"], out dur))
                    player.Duration = dur;
            }
 
            if (tags.ContainsKey("time"))
            {
                double time;
                if (double.TryParse(tags["time"], out time))
                    player.Time = time;
            }
 
            if (tags.ContainsKey("playlist repeat"))
            {
                int repeat;
                if (int.TryParse(tags["playlist repeat"], out repeat))
                    player.RepeatMode = repeat;
            }
 
            if (tags.ContainsKey("playlist shuffle"))
            {
                int shuffle;
                if (int.TryParse(tags["playlist shuffle"], out shuffle))
                    player.ShuffleMode = shuffle;
            }
 
            if (tags.ContainsKey("power"))
                player.IsPowered = tags["power"] == "1";
 
            if (tags.ContainsKey("mixer muting"))
                player.IsMuted = tags["mixer muting"] == "1";
        }
    }
}