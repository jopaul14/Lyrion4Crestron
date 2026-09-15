// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>
    /// Immutable snapshot of metadata for a single player. Instances are
    /// produced by the Lyrion Server driver and consumed read-only by Helper drivers.
    /// </summary>
    public sealed class LyrionMetadata
    {
        public LyrionMetadata(
            string title,
            string artist,
            string album,
            int trackNumber,
            int durationSeconds,
            int positionSeconds,
            bool isFrozen)
        {
            Title = title ?? string.Empty;
            Artist = artist ?? string.Empty;
            Album = album ?? string.Empty;
            TrackNumber = trackNumber < 0 ? 0 : trackNumber;
            DurationSeconds = durationSeconds < 0 ? 0 : durationSeconds;
            PositionSeconds = positionSeconds < 0 ? 0 : positionSeconds;
            IsFrozen = isFrozen;
        }

        public string Title { get; }
        public string Artist { get; }
        public string Album { get; }

        /// <summary>
        /// Track number within the album, or 0 when unknown (e.g. radio
        /// streams). Consumers should hide the number when this is 0.
        /// </summary>
        public int TrackNumber { get; }

        public int DurationSeconds { get; }
        public int PositionSeconds { get; }

        /// <summary>
        /// True when this metadata was frozen at the moment the player became
        /// unavailable. Consumers should display frozen metadata unchanged.
        /// After 30 seconds offline the Lyrion Server publishes an empty
        /// <see cref="LyrionMetadata"/> (with <see cref="IsFrozen"/> still
        /// true), at which point consumers should clear their UI.
        /// </summary>
        public bool IsFrozen { get; }

        public static LyrionMetadata Empty { get; } =
            new LyrionMetadata(string.Empty, string.Empty, string.Empty, 0, 0, 0, false);
    }
}
