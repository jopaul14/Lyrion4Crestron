// ---------------------------------------------------------------------------
//  Server_Lyrion_LMS_IP - Lyrion Server driver (Driver 1 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Net;
using System.Text;

namespace LyrionCommunity.Crestron.Lyrion.Server.Protocol
{
    /// <summary>
    /// URL-encoding helpers for the LMS CLI (Telnet) protocol. LMS percent-
    /// decodes each space-separated token before parsing it.
    /// </summary>
    internal static class LmsTokenCodec
    {
        /// <summary>
        /// Percent-encodes <paramref name="token"/> per RFC 3986: UTF-8 bytes,
        /// everything outside <c>A-Za-z0-9-._~</c> escaped, uppercase hex.
        /// </summary>
        /// <remarks>
        /// Hand-rolled on purpose; do not "simplify" this to
        /// <c>Uri.EscapeDataString</c> (issue #72 / PR #77, closed won't-fix).
        /// On .NET Framework the BCL encoder leaves <c>! ' ( ) *</c> unescaped
        /// unless the entry assembly targets 4.5+, and a driver DLL cannot
        /// control the host process that decides that quirk, so its output is
        /// host-dependent. It also throws <c>UriFormatException</c> on an
        /// unpaired surrogate and above 65,519 characters, where this loop is
        /// total. <c>Encode</c> is on the credential path
        /// (<c>LmsCliCommands.Login</c>), so deterministic and non-throwing
        /// beats 25 fewer lines.
        /// </remarks>
        public static string Encode(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return string.Empty;
            }

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

        public static string Decode(string token)
        {
            if (string.IsNullOrEmpty(token) || token.IndexOf('%') < 0)
            {
                return token ?? string.Empty;
            }

            try
            {
                return WebUtility.UrlDecode(token);
            }
            catch (Exception)
            {
                return token;
            }
        }

        /// <summary>
        /// Returns <paramref name="mac"/> unchanged after asserting it contains
        /// only hex digits and colon/dash separators. Throws if the value
        /// contains characters that could inject additional CLI tokens (spaces,
        /// newlines, percent signs, etc.).
        /// </summary>
        /// <remarks>
        /// Upstream callers already normalize MACs via <c>MacAddress.Normalize</c>,
        /// so this check should never fail in practice. It exists as a defense-
        /// in-depth barrier: if a future code path passes an unvalidated string,
        /// this throws rather than silently emitting an injectable command.
        /// </remarks>
        public static string EncodeMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return string.Empty;

            for (var i = 0; i < mac.Length; i++)
            {
                var c = mac[i];
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F') || c == ':' || c == '-')
                {
                    continue;
                }
                throw new ArgumentException(
                    "MAC contains characters outside [0-9a-fA-F:-].", nameof(mac));
            }

            return mac;
        }

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
