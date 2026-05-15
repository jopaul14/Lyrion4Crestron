// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>
    /// Identifier + display name of a hardware preset button. Null
    /// constructor arguments are normalized to <see cref="string.Empty"/>.
    /// </summary>
    public sealed class LyrionPreset
    {
        public LyrionPreset(string id, string displayName)
        {
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
        }

        /// <summary>
        /// Opaque identifier passed to
        /// <see cref="ILyrionGatewayService.ActivatePreset"/>. Typically a
        /// 1-based numeric string (e.g. "1", "2").
        /// </summary>
        public string Id { get; }

        public string DisplayName { get; }
    }
}
