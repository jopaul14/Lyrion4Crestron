// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------


namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>
    /// Immutable point-in-time view of one player. Returned to consumers from
    /// <see cref="ILyrionGatewayService.TryGetSnapshot"/> so a newly-bound
    /// Source / Helper / Receiver driver can paint its UI without waiting for
    /// the next event tick.
    /// </summary>
    /// <remarks>
    /// Snapshots are produced inside the gateway's player registry under its
    /// lock, so all fields are consistent with respect to each other. The
    /// snapshot becomes stale as soon as it is returned — consumers should
    /// subscribe to events for ongoing updates.
    /// </remarks>
    public sealed class LyrionPlayerSnapshot
    {
        public LyrionPlayerSnapshot(
            string mac,
            string name,
            bool isAvailable,
            bool isPoweredOn,
            LyrionPlaybackState playbackState,
            int volume,
            bool muted,
            bool shuffleEnabled,
            bool repeatEnabled,
            LyrionMetadata metadata,
            bool supportsPower,
            bool supportsVolume,
            int volumeStep)
        {
            Mac = mac ?? string.Empty;
            Name = name ?? string.Empty;
            IsAvailable = isAvailable;
            IsPoweredOn = isPoweredOn;
            PlaybackState = playbackState;
            Volume = volume;
            Muted = muted;
            ShuffleEnabled = shuffleEnabled;
            RepeatEnabled = repeatEnabled;
            Metadata = metadata ?? LyrionMetadata.Empty;
            SupportsPower = supportsPower;
            SupportsVolume = supportsVolume;
            VolumeStep = volumeStep < 1 ? 1 : (volumeStep > 50 ? 50 : volumeStep);
        }

        public string Mac { get; }

        /// <summary>Human-readable LMS player name (e.g. "Living Room").</summary>
        public string Name { get; }

        public bool IsAvailable { get; }
        public bool IsPoweredOn { get; }
        public LyrionPlaybackState PlaybackState { get; }
        public int Volume { get; }
        public bool Muted { get; }
        public bool ShuffleEnabled { get; }
        public bool RepeatEnabled { get; }
        public LyrionMetadata Metadata { get; }

        /// <summary>
        /// Whether this player supports explicit power on/off. When false,
        /// <see cref="ILyrionGatewayService.PowerOn"/> falls back to Play
        /// and <see cref="ILyrionGatewayService.PowerOff"/> falls back to Stop.
        /// </summary>
        public bool SupportsPower { get; }

        /// <summary>
        /// Whether this player supports volume control. Software-only players
        /// (e.g. SqueezeLite on a PC) may not expose a mixer.
        /// </summary>
        public bool SupportsVolume { get; }

        /// <summary>
        /// Configured volume step (1–50) for step-up/step-down presses,
        /// published by the Receiver. Default 2 when none has been published.
        /// </summary>
        public int VolumeStep { get; }
    }
}
