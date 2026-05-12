// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>Identifier + display name of a recall-able preset.</summary>
    public sealed class LyrionPreset
    {
        public LyrionPreset(string id, string displayName)
        {
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
        }

        public string Id { get; }
        public string DisplayName { get; }
    }
}
