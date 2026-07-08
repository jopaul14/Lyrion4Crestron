// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>
    /// Cross-driver contract published by the Gateway driver (Driver 1) and
    /// consumed by the Source (Driver 2), Helper (Driver 3), and Receiver
    /// (Driver 4) drivers. All state mutation goes through this interface;
    /// Drivers 2, 3, and 4 never open sockets to LMS.
    /// </summary>
    /// <remarks>
    /// <para><b>Threading contract:</b> Events are raised on the CLI receive
    /// thread. Handlers that perform long-running work will block subsequent
    /// event delivery. Consumers should return quickly from event handlers.</para>
    /// <para><b>Event semantics:</b></para>
    /// <list type="bullet">
    /// <item>Events MAY be raised on a background thread; consumers must not
    /// block.</item>
    /// <item>Events are only published for MACs the gateway knows about. A
    /// consumer that subscribes before binding will simply not receive
    /// events until <see cref="BindPlayer"/> succeeds.</item>
    /// <item>The MAC string passed to each event is the lowercase
    /// colon-separated canonical form (<c>aa:bb:cc:dd:ee:ff</c>).</item>
    /// </list>
    /// <para><b>Command semantics:</b></para>
    /// <list type="bullet">
    /// <item>Commands are best-effort and never throw on transport errors.</item>
    /// <item>Commands are dropped silently if the gateway is not currently
    /// CONNECTED to LMS. The next state-change event will reflect reality.</item>
    /// <item>All command methods accept a <c>mac</c> parameter in any valid
    /// format (colon- or dash-separated, any case). The gateway normalizes
    /// the MAC internally. Commands for unbound or malformed MACs are
    /// silently dropped.</item>
    /// </list>
    /// </remarks>
    public interface ILyrionGatewayService
    {
        // ===== Service identity =====

        /// <summary>
        /// Contract version of this service implementation. Consumers can
        /// use this to detect capability mismatches at bind time.
        /// </summary>
        string ServiceVersion { get; }

        // ===== Bind / unbind =====

        /// <summary>
        /// Registers a MAC the consumer cares about. Returns <c>false</c> if
        /// the MAC is malformed. Idempotent; subsequent calls with the same
        /// MAC are no-ops that return <c>true</c>.
        /// </summary>
        /// <remarks>
        /// Bind is a per-process registry, not a refcount. Multiple consumers
        /// can bind the same MAC; <see cref="UnbindPlayer"/> removes the
        /// registry entry regardless of how many consumers called
        /// <see cref="BindPlayer"/>. Consumers that need coordinated ownership
        /// must manage that externally.
        /// </remarks>
        bool BindPlayer(string mac);

        /// <summary>
        /// Removes a MAC from the bound set. Idempotent; calling with an
        /// already-unbound or malformed MAC is a no-op. After unbinding,
        /// the gateway stops maintaining state for this MAC and no further
        /// events will be raised for it.
        /// </summary>
        void UnbindPlayer(string mac);

        // ===== Snapshot =====

        /// <summary>
        /// Returns a consistent point-in-time snapshot for the given MAC.
        /// Returns <c>false</c> if the MAC is malformed or not bound.
        /// </summary>
        /// <remarks>
        /// The snapshot is produced under the registry lock, so all fields
        /// are mutually consistent. It becomes stale immediately — consumers
        /// should subscribe to events for ongoing updates.
        /// </remarks>
        bool TryGetSnapshot(string mac, out LyrionPlayerSnapshot snapshot);

        // ===== Events (Driver 1 → Drivers 2/3) =====

        /// <summary>
        /// Raised when the gateway's smoothed connectivity state to LMS
        /// changes. <c>true</c> = CONNECTED, <c>false</c> = DISCONNECTED.
        /// Consumers can use this to show a server-offline indicator. Player-
        /// level availability events are raised separately.
        /// </summary>
        event Action<bool> ServerConnectivityChanged;

        event Action<string, bool> AvailabilityChanged;

        /// <summary>
        /// Raised when a player's human-readable LMS name changes. The second
        /// argument is the new name. Rarely fires after the initial report.
        /// </summary>
        event Action<string, string> NameChanged;

        event Action<string, bool> PowerStateChanged;
        event Action<string, LyrionPlaybackState> PlaybackStateChanged;
        event Action<string, LyrionMetadata> MetadataUpdated;
        event Action<string, bool> ShuffleChanged;
        event Action<string, bool> RepeatChanged;
        event Action<string, int> VolumeChanged;
        event Action<string, bool> MuteChanged;

        /// <summary>
        /// Raised when the configured volume step for a player changes. The step
        /// is published by the Receiver (from its <c>VolumeStep</c> user
        /// attribute) so other consumers (e.g. the Helper's volume buttons) can
        /// match it. Range 1–50; default 2 when no Receiver has published one.
        /// </summary>
        event Action<string, int> VolumeStepChanged;

        /// <summary>
        /// Raised when the list of available presets for a player changes.
        /// Presets are hardware preset buttons (e.g. radio station presets on
        /// a Squeezebox Radio). The list may be empty if the player does not
        /// support presets or if they have not been discovered yet.
        /// </summary>
        event Action<string, IReadOnlyList<LyrionPreset>> PresetsUpdated;

        // ===== Commands from Driver 2 (Source) and Driver 3 (Helper) =====

        void Play(string mac);
        void Pause(string mac);
        void Stop(string mac);
        void Next(string mac);
        void Previous(string mac);

        /// <summary>Seek to the specified position in the current track.</summary>
        /// <param name="mac">Player MAC address (any valid format).</param>
        /// <param name="positionSeconds">Absolute position; clamped to 0 minimum.</param>
        void Seek(string mac, int positionSeconds);

        void SetShuffle(string mac, bool enabled);
        void SetRepeat(string mac, bool enabled);

        void PowerOn(string mac);
        void PowerOff(string mac);
        void PowerToggle(string mac);

        /// <summary>
        /// Activates a hardware preset button on the player.
        /// </summary>
        /// <param name="mac">Player MAC address (any valid format).</param>
        /// <param name="presetId">
        /// Opaque preset identifier matching <see cref="LyrionPreset.Id"/>.
        /// Typically a 1-based numeric string (e.g. "1", "2"). Silently
        /// dropped if <c>null</c> or empty.
        /// </param>
        void ActivatePreset(string mac, string presetId);

        // ===== Commands from Driver 4 (Receiver) =====

        /// <summary>Sets the absolute volume level.</summary>
        /// <param name="mac">Player MAC address (any valid format).</param>
        /// <param name="level">Volume level, clamped to 0–100.</param>
        void SetVolume(string mac, int level);

        /// <summary>Increases volume by <paramref name="step"/> increments.</summary>
        /// <param name="mac">Player MAC address (any valid format).</param>
        /// <param name="step">Step size; clamped to minimum 1.</param>
        void VolumeUp(string mac, int step);

        /// <summary>Decreases volume by <paramref name="step"/> increments.</summary>
        /// <param name="mac">Player MAC address (any valid format).</param>
        /// <param name="step">Step size; clamped to minimum 1.</param>
        void VolumeDown(string mac, int step);

        void SetMute(string mac, bool muted);

        /// <summary>
        /// Publishes the configured volume step (1–50) for a player so it is
        /// shared across consumers. Called by the Receiver from its
        /// <c>VolumeStep</c> user attribute; the Helper consumes it via
        /// <see cref="VolumeStepChanged"/> / <see cref="LyrionPlayerSnapshot.VolumeStep"/>
        /// so its Volume Up/Down buttons move by the same amount. Change-gated
        /// in the registry; the value is clamped to 1–50.
        /// </summary>
        void SetVolumeStep(string mac, int step);
    }
}
