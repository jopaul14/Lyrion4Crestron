// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>
    /// MAC address helpers shared by all three drivers. The canonical form is
    /// lowercase hex with colon separators (e.g. <c>aa:bb:cc:dd:ee:ff</c>).
    /// </summary>
    public static class MacAddress
    {
        private static readonly Regex MacRegex = new Regex(
            @"^[0-9a-fA-F]{2}([:-][0-9a-fA-F]{2}){5}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool IsValid(string mac)
        {
            return !string.IsNullOrWhiteSpace(mac) && MacRegex.IsMatch(mac.Trim());
        }

        /// <summary>
        /// Returns canonical lowercase-colon form. Returns <c>null</c> if
        /// <paramref name="mac"/> is not a valid MAC.
        /// </summary>
        public static string Normalize(string mac)
        {
            if (!IsValid(mac))
            {
                return null;
            }

            return mac.Trim().Replace('-', ':').ToLowerInvariant();
        }
    }
}
