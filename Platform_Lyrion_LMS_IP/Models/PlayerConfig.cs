// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LyrionCommunity.Crestron.Lyrion.Models
{
    /// <summary>
    /// One integrator-provided player entry. Parsed from a single line of the
    /// <c>_Players_</c> configuration item.
    /// </summary>
    public sealed class PlayerConfig
    {
        public PlayerConfig(string macAddress, string friendlyName, string description)
        {
            if (string.IsNullOrWhiteSpace(macAddress))
            {
                throw new ArgumentException("MAC address is required.", nameof(macAddress));
            }

            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                throw new ArgumentException("Friendly name is required.", nameof(friendlyName));
            }

            MacAddress = NormalizeMac(macAddress);
            FriendlyName = friendlyName.Trim();
            Description = description?.Trim() ?? string.Empty;
        }

        /// <summary>Normalized lowercase MAC address with colon separators (e.g. "00:04:20:12:34:56").</summary>
        public string MacAddress { get; }

        /// <summary>Human-friendly display name shown in Crestron UI.</summary>
        public string FriendlyName { get; }

        /// <summary>Optional free-form description. Empty string if not provided.</summary>
        public string Description { get; }

        /// <summary>
        /// A Crestron controller-id-safe identifier derived from the MAC.
        /// Colons are stripped so the ID is a plain alphanumeric string.
        /// </summary>
        public string ControllerId => "player_" + MacAddress.Replace(":", string.Empty);

        /// <summary>
        /// Parse the raw contents of the <c>_Players_</c> config item. Returns
        /// one <see cref="PlayerConfig"/> per valid line. Blank lines and lines
        /// starting with <c>#</c> are ignored. Lines that fail to parse are
        /// returned via <paramref name="errors"/> rather than throwing, so the
        /// configuration UI can report all problems at once.
        /// </summary>
        /// <param name="raw">Raw multi-line string from the configuration item. May be null.</param>
        /// <param name="errors">
        /// Populated with one human-readable error message per malformed line.
        /// Always non-null on return; empty on success.
        /// </param>
        public static IReadOnlyList<PlayerConfig> ParseList(string raw, out IReadOnlyList<string> errors)
        {
            var players = new List<PlayerConfig>();
            var errorList = new List<string>();
            errors = errorList;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return players;
            }

            var seenMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                // Format: MAC,Name[,Description]
                var parts = line.Split(new[] { ',' }, 3);
                if (parts.Length < 2)
                {
                    errorList.Add($"Line {i + 1}: expected 'MAC,Name[,Description]' but found '{line}'.");
                    continue;
                }

                var mac = parts[0].Trim();
                var name = parts[1].Trim();
                var description = parts.Length == 3 ? parts[2].Trim() : string.Empty;

                if (!LooksLikeMac(mac))
                {
                    errorList.Add($"Line {i + 1}: '{mac}' is not a valid MAC address (expected form aa:bb:cc:dd:ee:ff).");
                    continue;
                }

                if (name.Length == 0)
                {
                    errorList.Add($"Line {i + 1}: friendly name is required.");
                    continue;
                }

                PlayerConfig cfg;
                try
                {
                    cfg = new PlayerConfig(mac, name, description);
                }
                catch (ArgumentException ex)
                {
                    errorList.Add($"Line {i + 1}: {ex.Message}");
                    continue;
                }

                if (!seenMacs.Add(cfg.MacAddress))
                {
                    errorList.Add($"Line {i + 1}: duplicate MAC '{cfg.MacAddress}'.");
                    continue;
                }

                if (!seenNames.Add(cfg.FriendlyName))
                {
                    errorList.Add($"Line {i + 1}: duplicate friendly name '{cfg.FriendlyName}'.");
                    continue;
                }

                players.Add(cfg);
            }

            return players;
        }

        private static readonly Regex MacRegex = new Regex(
            @"^[0-9a-fA-F]{2}([:-][0-9a-fA-F]{2}){5}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static bool LooksLikeMac(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && MacRegex.IsMatch(value);
        }

        private static string NormalizeMac(string mac)
        {
            // LMS uses lowercase hex with colon separators. Accept hyphen input and normalize.
            return mac.Trim().Replace('-', ':').ToLowerInvariant();
        }

        public override string ToString() => $"{FriendlyName} [{MacAddress}]";
    }
}
