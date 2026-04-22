// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LyrionCommunity.Crestron.Lyrion.Protocol
{
    /// <summary>
    /// Builders for LMS JSON-RPC request bodies.
    /// </summary>
    /// <remarks>
    /// LMS JSON-RPC requests are posted to <c>/jsonrpc.js</c> with the shape:
    /// <code>
    /// {
    ///   "id": 1,
    ///   "method": "slim.request",
    ///   "params": ["&lt;playerid&gt;", ["&lt;command&gt;", "&lt;arg1&gt;", ...]]
    /// }
    /// </code>
    /// where <c>&lt;playerid&gt;</c> is either a URL-encoded MAC (for player-scoped
    /// commands) or an empty string for global commands.
    /// </remarks>
    internal static class LmsJsonRpcRequests
    {
        private const string SlimMethod = "slim.request";

        /// <summary>Global <c>version</c> query. Used to populate <c>lyrion:serverVersion</c>.</summary>
        public static string QueryServerVersion(int id = 1)
        {
            return Build(id, string.Empty, new[] { "version", "?" });
        }

        /// <summary>Paged <c>players</c> list (used to validate configured MACs).</summary>
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
        /// Top-level browse node (paged). <paramref name="node"/> is the LMS
        /// category name such as <c>artists</c>, <c>albums</c>, <c>genres</c>,
        /// <c>playlists</c>, <c>new_music</c>, <c>randomplay</c>, <c>radios</c>,
        /// or <c>apps</c>. Additional <paramref name="extraArgs"/> are appended
        /// for operations like <c>genre_id:42</c>.
        /// </summary>
        public static string Browse(
            string playerMac,
            string node,
            int start,
            int count,
            IReadOnlyList<string> extraArgs = null,
            int id = 1)
        {
            if (string.IsNullOrEmpty(node))
            {
                throw new ArgumentException("Browse node is required.", nameof(node));
            }

            var argCount = 3 + (extraArgs != null ? extraArgs.Count : 0);
            var args = new string[argCount];
            args[0] = node;
            args[1] = start.ToString(CultureInfo.InvariantCulture);
            args[2] = count.ToString(CultureInfo.InvariantCulture);
            if (extraArgs != null)
            {
                for (var i = 0; i < extraArgs.Count; i++)
                {
                    args[3 + i] = extraArgs[i] ?? string.Empty;
                }
            }

            return Build(id, playerMac ?? string.Empty, args);
        }

        /// <summary>Paged favorites list for a specific player (or global if empty).</summary>
        public static string QueryFavorites(
            string playerMac,
            int start,
            int count,
            string parentItemId = null,
            int id = 1)
        {
            var hasParent = !string.IsNullOrEmpty(parentItemId);
            var args = new List<string>(6)
            {
                "favorites",
                "items",
                start.ToString(CultureInfo.InvariantCulture),
                count.ToString(CultureInfo.InvariantCulture),
                "want_url:1"
            };

            if (hasParent)
            {
                args.Add("item_id:" + parentItemId);
            }

            return Build(id, playerMac ?? string.Empty, args);
        }

        /// <summary>Paged tracks for a specific playlist id.</summary>
        public static string QueryPlaylistTracks(string playlistId, int start, int count, int id = 1)
        {
            if (string.IsNullOrEmpty(playlistId))
            {
                throw new ArgumentException("Playlist id is required.", nameof(playlistId));
            }

            return Build(id, string.Empty, new[]
            {
                "playlists",
                "tracks",
                start.ToString(CultureInfo.InvariantCulture),
                count.ToString(CultureInfo.InvariantCulture),
                "playlist_id:" + playlistId,
                "tags:galdt"
            });
        }

        // --- Internals ---------------------------------------------------------

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
                if (i > 0)
                {
                    sb.Append(',');
                }
                AppendJsonString(sb, commandTokens[i] ?? string.Empty);
            }
            sb.Append("]]}");
            return sb.ToString();
        }

        /// <summary>Append a properly-escaped JSON string (including surrounding quotes).</summary>
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
