// ---------------------------------------------------------------------------
//  Platform_Lyrion_LMS_IP - Crestron Certified Driver for Lyrion Media Server
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Net;
using System.Text;

namespace LyrionCommunity.Crestron.Lyrion.Protocol
{
    /// <summary>
    /// URL-encoding helpers for the LMS CLI (Telnet) protocol.
    /// </summary>
    /// <remarks>
    /// The LMS CLI uses a line protocol where each line is a sequence of
    /// space-separated, percent-encoded tokens. Spaces, tabs, and special
    /// characters within a token (including the colons in MAC addresses) are
    /// percent-encoded. Tokens of the form <c>key:value</c> remain valid under
    /// percent-encoding because LMS decodes each token before parsing it.
    /// </remarks>
    internal static class LmsTokenCodec
    {
        /// <summary>
        /// Percent-encode a single CLI token. Safe to use for any token
        /// including MAC-addressed player IDs and <c>key:value</c> parameter
        /// tokens - LMS percent-decodes each token before parsing.
        /// </summary>
        public static string Encode(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return string.Empty;
            }

            // WebUtility.UrlEncode encodes space as '+', but LMS expects '%20'.
            // We hand-roll the encode for predictable output.
            var sb = new StringBuilder(token.Length + 8);
            var bytes = Encoding.UTF8.GetBytes(token);
            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                if (IsUnreserved(b))
                {
                    sb.Append((char)b);
                }
                else
                {
                    sb.Append('%');
                    sb.Append(HexDigit(b >> 4));
                    sb.Append(HexDigit(b & 0x0F));
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Percent-decode a single CLI token. Tolerates tokens that aren't
        /// percent-encoded (returns them unchanged) and malformed sequences
        /// (leaves the literal '%' in place).
        /// </summary>
        public static string Decode(string token)
        {
            if (string.IsNullOrEmpty(token) || token.IndexOf('%') < 0)
            {
                return token ?? string.Empty;
            }

            try
            {
                // WebUtility is BCL-only and correctly handles '%XX' sequences and UTF-8.
                return WebUtility.UrlDecode(token);
            }
            catch (Exception)
            {
                // Defensive: never throw on malformed LMS input.
                return token;
            }
        }

        /// <summary>
        /// Encode a MAC address (e.g. <c>00:04:20:12:34:56</c>) for use as a
        /// player ID in a CLI command. Colons are percent-encoded.
        /// </summary>
        public static string EncodeMac(string mac)
        {
            return Encode(mac ?? string.Empty);
        }

        // RFC 3986 unreserved set: ALPHA / DIGIT / '-' / '.' / '_' / '~'
        private static bool IsUnreserved(byte b)
        {
            return (b >= (byte)'A' && b <= (byte)'Z')
                || (b >= (byte)'a' && b <= (byte)'z')
                || (b >= (byte)'0' && b <= (byte)'9')
                || b == (byte)'-' || b == (byte)'.' || b == (byte)'_' || b == (byte)'~';
        }

        private static char HexDigit(int nibble)
        {
            return (char)(nibble < 10 ? ('0' + nibble) : ('A' + (nibble - 10)));
        }
    }
}
