// ---------------------------------------------------------------------------
//  Gateway_Lyrion_LMS_IP - Lyrion Server gateway driver (Driver 1 of 3)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LyrionCommunity.Crestron.Lyrion.Gateway.Protocol
{
    /// <summary>
    /// Builders for LMS JSON-RPC request bodies. Only the subset used by the
    /// gateway driver is exposed; browse/favorites/playlist builders were
    /// removed per CLAUDE.md "EXPLICITLY REMOVED FEATURES".
    /// </summary>
    internal static class LmsJsonRpcRequests
    {
        private const string SlimMethod = "slim.request";

        public static string QueryServerVersion(int id = 1)
        {
            return Build(id, string.Empty, new[] { "version", "?" });
        }

        public static string QueryPlayers(int start, int count, int id = 1)
        {
            return Build(id, string.Empty, new[]
            {
                "players",
                start.ToString(CultureInfo.InvariantCulture),
                count.ToString(CultureInfo.InvariantCulture)
            });
        }

        /// <summary>
        /// Full status query used on reconnect to re-derive playback / volume /
        /// metadata for a single player.
        /// </summary>
        public static string QueryPlayerStatus(string playerMac, int id = 1)
        {
            return Build(id, playerMac ?? string.Empty, new[]
            {
                "status",
                "-",
                "1",
                "tags:galdIKoNcryu"
            });
        }

        private static string Build(int id, string playerid, IReadOnlyList<string> commandTokens)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"id\":");
            sb.Append(id.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"method\":\"");
            sb.Append(SlimMethod);
            sb.Append("\",\"params\":[");
            AppendJsonString(sb, playerid ?? string.Empty);
            sb.Append(",[");
            for (var i = 0; i < commandTokens.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendJsonString(sb, commandTokens[i] ?? string.Empty);
            }
            sb.Append("]]}");
            return sb.ToString();
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
