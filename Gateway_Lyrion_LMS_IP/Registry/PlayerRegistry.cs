// ---------------------------------------------------------------------------
//  Gateway_Lyrion_LMS_IP - Lyrion Server gateway driver (Driver 1 of 3)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Gateway.Registry
{
    /// <summary>
    /// Per-MAC store of <see cref="PlayerRecord"/> instances. All reads and
    /// mutations are serialized through a single lock; consumers receive
    /// strongly-typed change events outside the lock.
    /// </summary>
    /// <remarks>
    /// Only MACs that have been <see cref="Bind"/>ed by a Media or Volume
    /// driver have records here. Player notifications for MACs we don't care
    /// about are dropped silently in the gateway driver before they reach us.
    /// </remarks>
    internal sealed class PlayerRegistry
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, PlayerRecord> _records =
            new Dictionary<string, PlayerRecord>(StringComparer.OrdinalIgnoreCase);

        // ===== Change events (raised outside the lock) =====
        public event Action<string, bool> AvailabilityChanged;
        public event Action<string, bool> PowerStateChanged;
        public event Action<string, LyrionPlaybackState> PlaybackStateChanged;
        public event Action<string, LyrionMetadata> MetadataUpdated;
        public event Action<string, bool> ShuffleChanged;
        public event Action<string, bool> RepeatChanged;
        public event Action<string, int> VolumeChanged;
        public event Action<string, bool> MuteChanged;
        public event Action<string, IReadOnlyList<LyrionPreset>> PresetsUpdated;

        // Whether the server is currently CONNECTED. Availability calculation
        // depends on this gate: if the server is offline, no player is available.
        private bool _serverConnected;

        public bool Bind(string mac)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return false;

            lock (_gate)
            {
                if (!_records.ContainsKey(canon))
                {
                    _records[canon] = new PlayerRecord(canon);
                }
            }

            return true;
        }

        public void Unbind(string mac)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            lock (_gate)
            {
                _records.Remove(canon);
            }
        }

        public bool IsBound(string mac)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return false;

            lock (_gate) return _records.ContainsKey(canon);
        }

        public IReadOnlyList<string> BoundMacs()
        {
            lock (_gate)
            {
                var copy = new List<string>(_records.Keys);
                return copy;
            }
        }

        public bool TryGetSnapshot(string mac, out LyrionPlayerSnapshot snapshot)
        {
            var canon = MacAddress.Normalize(mac);
            snapshot = null;
            if (canon == null) return false;

            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec))
                {
                    return false;
                }

                snapshot = new LyrionPlayerSnapshot(
                    rec.MacAddress,
                    rec.IsAvailable,
                    rec.IsPoweredOn,
                    rec.PlaybackState,
                    rec.Volume,
                    rec.Muted,
                    rec.ShuffleEnabled,
                    rec.RepeatEnabled,
                    SnapshotMetadata(rec),
                    rec.Presets,
                    rec.CanPowerOff,
                    rec.SupportsVolume);
                return true;
            }
        }

        // ===== Server-level inputs =====

        /// <summary>
        /// Reflects the smoothed server state from the FSM. When the server
        /// transitions, every record's availability is recomputed and
        /// <see cref="AvailabilityChanged"/> is raised for each affected MAC.
        /// CLAUDE.md "Do NOT log per-player availability caused by server
        /// disconnect" refers to logging only — the data events must still
        /// fire so consuming drivers can update their Available property.
        /// </summary>
        public IReadOnlyList<string> SetServerConnected(bool connected)
        {
            var affected = new List<(string mac, bool nowAvail)>();
            lock (_gate)
            {
                if (_serverConnected == connected) return Array.Empty<string>();
                _serverConnected = connected;

                foreach (var kvp in _records)
                {
                    var rec = kvp.Value;
                    var nowAvail = ComputeAvailability(rec, _serverConnected);
                    if (nowAvail != rec.IsAvailable)
                    {
                        rec.IsAvailable = nowAvail;
                        affected.Add((rec.MacAddress, nowAvail));

                        if (!nowAvail)
                        {
                            // Freeze metadata immediately on availability loss.
                            // The 30s clear is handled by the freezer pump in
                            // the gateway driver.
                            rec.IsFrozen = true;
                            rec.FrozenAtUtc = DateTime.UtcNow;
                        }
                    }
                }
            }

            var macs = new List<string>(affected.Count);
            foreach (var item in affected)
            {
                macs.Add(item.mac);
                try { AvailabilityChanged?.Invoke(item.mac, item.nowAvail); } catch { }
            }
            return macs;
        }

        // ===== Per-player mutators =====
        // Each returns true and raises the corresponding event when state
        // actually changed. The gateway driver calls these from the CLI
        // receive thread.

        public void NoteLifecycle(string mac, PlayerLifecycleState newState)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            bool availChanged = false;
            bool nowAvail = false;
            PlayerRecord rec;

            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out rec)) return;

                rec.LifecycleState = newState;
                rec.LastSeenUtc = DateTime.UtcNow;

                var before = rec.IsAvailable;
                nowAvail = ComputeAvailability(rec, _serverConnected);
                if (before != nowAvail)
                {
                    rec.IsAvailable = nowAvail;
                    availChanged = true;
                    if (!nowAvail)
                    {
                        rec.IsFrozen = true;
                        rec.FrozenAtUtc = DateTime.UtcNow;
                    }
                }
            }

            if (availChanged)
            {
                RaiseAvailability(canon, nowAvail);
            }
        }

        /// <summary>
        /// Mark a player as InvalidSession and increment its retry counter.
        /// Returns true if this is the first invalid-session for this player
        /// (the caller should attempt rediscovery once). Returns false if
        /// the retry has already been attempted, meaning the caller should
        /// mark Offline.
        /// </summary>
        public bool NoteInvalidSession(string mac)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return false;

            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return false;

                if (rec.LifecycleState == PlayerLifecycleState.InvalidSession)
                {
                    return false;
                }

                rec.LifecycleState = PlayerLifecycleState.InvalidSession;
                return true;
            }
        }

        public void SetPlayerId(string mac, string playerId)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;
            lock (_gate)
            {
                if (_records.TryGetValue(canon, out var rec))
                {
                    rec.PlayerId = playerId;
                }
            }
        }

        public void SetCapabilities(string mac, bool? canPowerOff, bool? supportsVolume)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;
            lock (_gate)
            {
                if (_records.TryGetValue(canon, out var rec))
                {
                    if (canPowerOff.HasValue) rec.CanPowerOff = canPowerOff.Value;
                    if (supportsVolume.HasValue) rec.SupportsVolume = supportsVolume.Value;
                }
            }
        }

        public bool TryGetCapabilities(string mac, out bool canPowerOff, out bool supportsVolume)
        {
            canPowerOff = false;
            supportsVolume = false;
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return false;

            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return false;
                canPowerOff = rec.CanPowerOff;
                supportsVolume = rec.SupportsVolume;
                return true;
            }
        }

        public void NotePlaybackState(string mac, LyrionPlaybackState state)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            bool changed;
            bool powerChanged = false;
            bool nowPowered = false;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                changed = rec.PlaybackState != state;
                if (changed) rec.PlaybackState = state;

                // Power derivation: isOn = play or pause; isOff = stop.
                var derivedPower = state == LyrionPlaybackState.Playing
                                || state == LyrionPlaybackState.Paused;
                if (derivedPower != rec.IsPoweredOn)
                {
                    rec.IsPoweredOn = derivedPower;
                    powerChanged = true;
                    nowPowered = derivedPower;
                }
            }

            if (changed) try { PlaybackStateChanged?.Invoke(canon, state); } catch { }
            if (powerChanged) try { PowerStateChanged?.Invoke(canon, nowPowered); } catch { }
        }

        public void NoteExplicitPower(string mac, bool isOn)
        {
            // The "<mac> power 0|1" CLI notification is the authoritative
            // power signal for players that actually have a power state.
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            bool changed;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                changed = rec.IsPoweredOn != isOn;
                if (changed) rec.IsPoweredOn = isOn;
            }

            if (changed) try { PowerStateChanged?.Invoke(canon, isOn); } catch { }
        }

        public void NoteVolume(string mac, int level)
        {
            if (level < 0) level = 0;
            if (level > 100) level = 100;

            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            bool changed;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                changed = rec.Volume != level;
                if (changed) rec.Volume = level;
            }

            if (changed) try { VolumeChanged?.Invoke(canon, level); } catch { }
        }

        public void NoteMute(string mac, bool muted)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            bool changed;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                changed = rec.Muted != muted;
                if (changed) rec.Muted = muted;
            }

            if (changed) try { MuteChanged?.Invoke(canon, muted); } catch { }
        }

        public void NoteShuffle(string mac, int lmsValue)
        {
            // LMS values: 0 off, 1 song, 2 album → we expose only on/off.
            var enabled = lmsValue != 0;
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            bool changed;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                changed = rec.ShuffleEnabled != enabled;
                if (changed) rec.ShuffleEnabled = enabled;
            }

            if (changed) try { ShuffleChanged?.Invoke(canon, enabled); } catch { }
        }

        public void NoteRepeat(string mac, int lmsValue)
        {
            // LMS values: 0 off, 1 song, 2 playlist → we expose only on/off.
            var enabled = lmsValue != 0;
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            bool changed;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                changed = rec.RepeatEnabled != enabled;
                if (changed) rec.RepeatEnabled = enabled;
            }

            if (changed) try { RepeatChanged?.Invoke(canon, enabled); } catch { }
        }

        public void NoteMetadata(
            string mac,
            string title,
            string artist,
            string album,
            string artworkUrl,
            int durationSeconds,
            int positionSeconds)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            LyrionMetadata snapshot;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;

                rec.Title = title ?? rec.Title ?? string.Empty;
                rec.Artist = artist ?? rec.Artist ?? string.Empty;
                rec.Album = album ?? rec.Album ?? string.Empty;
                rec.ArtworkUrl = artworkUrl ?? rec.ArtworkUrl ?? string.Empty;
                if (durationSeconds >= 0) rec.DurationSeconds = durationSeconds;
                if (positionSeconds >= 0) rec.PositionSeconds = positionSeconds;
                rec.IsFrozen = false;
                rec.LastMetadataUpdateUtc = DateTime.UtcNow;

                snapshot = SnapshotMetadata(rec);
            }

            try { MetadataUpdated?.Invoke(canon, snapshot); } catch { }
        }

        public void NotePosition(string mac, int positionSeconds)
        {
            if (positionSeconds < 0) positionSeconds = 0;
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            LyrionMetadata snapshot = null;
            bool publish = false;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                if (rec.PositionSeconds != positionSeconds)
                {
                    rec.PositionSeconds = positionSeconds;
                    rec.LastMetadataUpdateUtc = DateTime.UtcNow;
                    publish = true;
                    snapshot = SnapshotMetadata(rec);
                }
            }

            if (publish) try { MetadataUpdated?.Invoke(canon, snapshot); } catch { }
        }

        public void NotePresets(string mac, IReadOnlyList<LyrionPreset> presets)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;
            presets = presets ?? Array.Empty<LyrionPreset>();

            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                rec.Presets = presets;
            }

            try { PresetsUpdated?.Invoke(canon, presets); } catch { }
        }

        /// <summary>
        /// Sweep metadata-freeze records. If a record has been frozen for
        /// 30s+, clear it and republish empty metadata. Called once per
        /// second by the gateway driver's pump.
        /// </summary>
        /// <remarks>
        /// Short-circuits when no record is currently frozen so the steady-
        /// state cost is one dictionary scan per second with zero
        /// allocations. The allocation path only runs when a player is
        /// actually offline.
        /// </remarks>
        public IReadOnlyList<(string mac, LyrionMetadata payload)> SweepFrozenMetadata(TimeSpan freezeTtl)
        {
            var now = DateTime.UtcNow;
            List<(string, LyrionMetadata)> toPublish = null;

            lock (_gate)
            {
                bool anyFrozen = false;
                foreach (var kvp in _records)
                {
                    if (kvp.Value.IsFrozen) { anyFrozen = true; break; }
                }
                if (!anyFrozen) return Array.Empty<(string, LyrionMetadata)>();

                foreach (var kvp in _records)
                {
                    var rec = kvp.Value;
                    if (rec.IsFrozen && rec.IsAvailable == false
                        && now - rec.FrozenAtUtc >= freezeTtl
                        && (rec.Title.Length > 0 || rec.Artist.Length > 0 || rec.Album.Length > 0 || rec.ArtworkUrl.Length > 0))
                    {
                        rec.Title = string.Empty;
                        rec.Artist = string.Empty;
                        rec.Album = string.Empty;
                        rec.ArtworkUrl = string.Empty;
                        rec.DurationSeconds = 0;
                        rec.PositionSeconds = 0;
                        if (toPublish == null) toPublish = new List<(string, LyrionMetadata)>();
                        toPublish.Add((rec.MacAddress, SnapshotMetadata(rec)));
                    }
                }
            }

            if (toPublish == null) return Array.Empty<(string, LyrionMetadata)>();

            foreach (var item in toPublish)
            {
                try { MetadataUpdated?.Invoke(item.Item1, item.Item2); } catch { }
            }

            return toPublish;
        }

        /// <summary>
        /// Republish all derived state for the given MACs after a server
        /// reconnect. The gateway driver calls this after recomputing state
        /// from a fresh LMS status snapshot.
        /// </summary>
        public void RepublishAll(IReadOnlyList<string> macs)
        {
            if (macs == null) return;

            foreach (var raw in macs)
            {
                var canon = MacAddress.Normalize(raw);
                if (canon == null) continue;

                bool avail, power, muted, shuffle, repeat;
                LyrionPlaybackState pbs;
                int vol;
                LyrionMetadata metaSnap;
                IReadOnlyList<LyrionPreset> presets;
                lock (_gate)
                {
                    if (!_records.TryGetValue(canon, out var rec)) continue;
                    avail = rec.IsAvailable;
                    power = rec.IsPoweredOn;
                    pbs = rec.PlaybackState;
                    vol = rec.Volume;
                    muted = rec.Muted;
                    shuffle = rec.ShuffleEnabled;
                    repeat = rec.RepeatEnabled;
                    presets = rec.Presets;
                    metaSnap = SnapshotMetadata(rec);
                }

                try { AvailabilityChanged?.Invoke(canon, avail); } catch { }
                try { PowerStateChanged?.Invoke(canon, power); } catch { }
                try { PlaybackStateChanged?.Invoke(canon, pbs); } catch { }
                try { VolumeChanged?.Invoke(canon, vol); } catch { }
                try { MuteChanged?.Invoke(canon, muted); } catch { }
                try { ShuffleChanged?.Invoke(canon, shuffle); } catch { }
                try { RepeatChanged?.Invoke(canon, repeat); } catch { }
                try { MetadataUpdated?.Invoke(canon, metaSnap); } catch { }
                try { PresetsUpdated?.Invoke(canon, presets); } catch { }
            }
        }

        private void RaiseAvailability(string canon, bool nowAvail)
        {
            try { AvailabilityChanged?.Invoke(canon, nowAvail); } catch { }
        }

        private static bool ComputeAvailability(PlayerRecord rec, bool serverConnected)
        {
            if (!serverConnected) return false;
            return rec.LifecycleState == PlayerLifecycleState.Online;
        }

        private static LyrionMetadata SnapshotMetadata(PlayerRecord rec)
        {
            return new LyrionMetadata(
                rec.Title,
                rec.Artist,
                rec.Album,
                rec.ArtworkUrl,
                rec.DurationSeconds,
                rec.PositionSeconds,
                rec.IsFrozen);
        }
    }
}
