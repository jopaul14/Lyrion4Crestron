// ---------------------------------------------------------------------------
//  Server_Lyrion_LMS_IP - Lyrion Server driver (Driver 1 of 4)
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using LyrionCommunity.Crestron.Lyrion.Service;

namespace LyrionCommunity.Crestron.Lyrion.Server.Registry
{
    /// <summary>
    /// Per-MAC store of <see cref="PlayerRecord"/> instances. All reads and
    /// mutations are serialized through a single lock; consumers receive
    /// strongly-typed change events outside the lock.
    /// </summary>
    /// <remarks>
    /// Only MACs that have been <see cref="Bind"/>ed by a Source, Helper, or
    /// Receiver driver have records here. Player notifications for MACs we
    /// don't care about are dropped silently in the Lyrion Server driver before
    /// they reach us.
    /// </remarks>
    internal sealed class PlayerRegistry
    {
        private const int MaxStringLength = 1024;

        private readonly object _gate = new object();
        private readonly Dictionary<string, PlayerRecord> _records =
            new Dictionary<string, PlayerRecord>(StringComparer.OrdinalIgnoreCase);

        // ===== Change events (raised outside the lock) =====
        public event Action<string, bool> AvailabilityChanged;
        public event Action<string, string> NameChanged;
        public event Action<string, bool> PowerStateChanged;
        public event Action<string, LyrionPlaybackState> PlaybackStateChanged;
        public event Action<string, LyrionMetadata> MetadataUpdated;
        public event Action<string, bool> ShuffleChanged;
        public event Action<string, bool> RepeatChanged;
        public event Action<string, int> VolumeChanged;
        public event Action<string, bool> MuteChanged;
        public event Action<string, int> VolumeStepChanged;

        // Whether the server is currently CONNECTED. Availability calculation
        // depends on this gate: if the server is offline, no player is available.
        private bool _serverConnected;

        public bool Bind(string mac) => Bind(mac, out _);

        /// <summary>
        /// Registers a consumer's interest in a MAC. Records are reference
        /// counted: all three consumer drivers bind the same player, and the
        /// record must survive any one of them being disposed or re-addressed.
        /// <paramref name="created"/> is true only for the first bind of a MAC,
        /// so the caller can do first-bind work (the initial status subscribe)
        /// exactly once rather than once per consumer.
        /// </summary>
        public bool Bind(string mac, out bool created)
        {
            created = false;
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return false;

            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec))
                {
                    rec = new PlayerRecord(canon);
                    _records[canon] = rec;
                    created = true;
                }
                rec.BindCount++;
            }

            return true;
        }

        /// <summary>
        /// Releases one consumer's interest. The record is removed only when
        /// the last bound consumer lets go — before 1.0.12 this removed it
        /// unconditionally, and disposing just the (optional) Receiver left the
        /// Source and Helper for the same room bound to a MAC the registry no
        /// longer knew: every notification dropped at IsBound, every command
        /// dropped, no event, no log, until the Lyrion Server was reloaded.
        /// </summary>
        public void Unbind(string mac)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                rec.BindCount--;
                if (rec.BindCount <= 0)
                {
                    _records.Remove(canon);
                }
            }
        }

        /// <summary>
        /// Marks a record as having had a full status response applied. Called
        /// by the Lyrion Server driver at the END of ApplyStatusResponse, after
        /// every field has been noted. Bookkeeping only — no event — because it
        /// changes nothing a consumer displays; it changes what a consumer may
        /// <em>force</em> at bind time (see <see cref="LyrionPlayerSnapshot.IsObserved"/>).
        /// </summary>
        public void NoteStatusApplied(string mac)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;
            lock (_gate)
            {
                if (_records.TryGetValue(canon, out var rec)) rec.HasObservedState = true;
            }
        }

        public bool IsBound(string mac)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return false;

            lock (_gate) return _records.ContainsKey(canon);
        }

        /// <summary>
        /// True when the MAC is bound AND currently available (server connected,
        /// player Online). The service gates every player command on this: a
        /// command for an unreachable player is dropped the same way a command
        /// for a disconnected server is, rather than being handed to LMS as a
        /// stored preference that fires when the player next reconnects
        /// (PowerToggle on an offline player used to send `power 1`, which LMS
        /// applied on reconnect and switched the room on unexpectedly).
        /// </summary>
        public bool IsAvailable(string mac)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return false;

            lock (_gate)
            {
                return _records.TryGetValue(canon, out var rec) && rec.IsAvailable;
            }
        }

        public IReadOnlyList<string> BoundMacs()
        {
            lock (_gate)
            {
                var copy = new List<string>(_records.Keys);
                return copy;
            }
        }

        /// <summary>
        /// Diagnostic record count. Useful for spotting record accumulation if
        /// a consumer is recreated without calling <see cref="Unbind"/>.
        /// </summary>
        public int Count
        {
            get { lock (_gate) { return _records.Count; } }
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

                // Effective values: an unavailable player is off and stopped
                // no matter what LMS last said about it (see EffectivePower).
                snapshot = new LyrionPlayerSnapshot(
                    rec.MacAddress,
                    rec.Name,
                    rec.IsAvailable,
                    EffectivePower(rec),
                    EffectivePlayback(rec),
                    rec.Volume,
                    rec.Muted,
                    rec.ShuffleEnabled,
                    rec.RepeatEnabled,
                    SnapshotMetadata(rec),
                    rec.CanPowerOff,
                    rec.SupportsVolume,
                    rec.VolumeStep,
                    rec.HasObservedState);
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
            var affected = new List<AvailabilityChange>();
            lock (_gate)
            {
                if (_serverConnected == connected) return Array.Empty<string>();
                _serverConnected = connected;

                foreach (var kvp in _records)
                {
                    var rec = kvp.Value;

                    // A server-level loss means we no longer know any player's
                    // lifecycle. Forget it, so that when the server returns
                    // nothing springs back to "available" on the strength of
                    // pre-outage state: a record becomes Online again only when
                    // a fresh status response says so (ApplyStatusResponse
                    // notes lifecycle LAST), and only then are its effective
                    // edges published. Before 1.0.14 the lifecycle survived the
                    // outage, so SetServerConnected(true) made every record
                    // available at once and published the CACHED raw
                    // power/playback as edges before any status arrived — a
                    // player switched off during the outage produced a
                    // PoweredOn edge (Room On) followed by the real OFF one
                    // round-trip later, the 1.0.5 bounce-back class.
                    if (!connected) rec.LifecycleState = PlayerLifecycleState.Unknown;

                    var change = ApplyAvailability_NoLock(rec);
                    if (change != null) affected.Add(change);
                }
            }

            var macs = new List<string>(affected.Count);
            foreach (var item in affected)
            {
                macs.Add(item.Mac);
                RaiseAvailabilityChange(item);
            }
            return macs;
        }

        // ===== Availability derivation (the registry owns it) =====
        //
        // "Unavailable implies powered off and stopped" used to be derived
        // inside the Source and Helper (each zeroed its own copy on
        // AvailabilityChanged(false)) while the registry kept the pre-outage
        // power/playback. That split is what made 1.0.8 necessary and what
        // made 1.0.8 wrong: on restore the change-gated NoteExplicitPower /
        // NotePlaybackState compared the real value against the registry's
        // UNCHANGED copy, found no change, and published nothing — so the
        // consumers stayed OFF/Stopped for a player that was on and playing.
        // A server reconnect was rescued by RepublishAll; a per-player
        // disconnect/reconnect never was. RepublishAll itself then re-emitted
        // the stale IsPoweredOn=true for a player still offline, handing a
        // "Power Is On -> Room On" mapping a PoweredOn edge for an unreachable
        // player after every LMS restart.
        //
        // Now the registry lowers power/playback itself on every availability
        // loss (server-level and per-player alike), publishes those as real
        // edges, and resets HasExplicitPower so the next explicit report
        // counts as a first observation and publishes even if it equals the
        // lowered value. Restore is then a genuine registry edge that every
        // consumer receives identically, and the consumers' UpdateAvailability
        // only sets Connected — no derivation, per the PRD.

        // ===== Effective state =====
        //
        // The record keeps the RAW values LMS last reported (IsPoweredOn,
        // PlaybackState). What consumers are told — and what a snapshot or a
        // republish exposes — is the EFFECTIVE value: raw when the player is
        // available, off/stopped when it is not. "Unavailable implies powered
        // off and stopped" is therefore applied once, at the publish boundary,
        // instead of being written into the record and undone later.
        //
        // This replaces 1.0.12's "lower the raw fields on loss" — which was
        // right in spirit and wrong in two ways it could not see: a status
        // keep-alive for a DISCONNECTED client (player_connected:0 power:1)
        // arrived, was noted Offline (lowered), and fourteen lines later its
        // power field counted as a first explicit report and was published —
        // PoweredOn for an unreachable player; and after an LMS restart the
        // first status reply lands while the FSM still holds the server
        // disconnected, so the same first-report rule published PoweredOn
        // before Connected went true. With effective state, a mutation while
        // unavailable changes the raw value and publishes nothing (effective
        // is unchanged), and restore publishes the effective edges AFTER
        // AvailabilityChanged(true). Nothing needs re-arming.

        private static bool EffectivePower(PlayerRecord rec)
        {
            return rec.IsAvailable && rec.IsPoweredOn;
        }

        private static LyrionPlaybackState EffectivePlayback(PlayerRecord rec)
        {
            return rec.IsAvailable ? rec.PlaybackState : LyrionPlaybackState.Stopped;
        }

        private sealed class AvailabilityChange
        {
            public string Mac;
            public bool NowAvailable;
            public bool? PowerEdge;                   // effective power changed -> new value
            public LyrionPlaybackState? PlaybackEdge; // effective playback changed -> new value
            public LyrionMetadata Unfrozen;           // non-null when restore cleared a freeze
        }

        /// <summary>
        /// Recomputes a record's availability from its lifecycle and the server
        /// state, applies the consequences (freeze on loss, unfreeze on
        /// restore), computes which EFFECTIVE fields moved as a result, and
        /// returns what to publish — or null if nothing changed. Callers raise
        /// the returned change OUTSIDE the lock via
        /// <see cref="RaiseAvailabilityChange"/>.
        /// </summary>
        private AvailabilityChange ApplyAvailability_NoLock(PlayerRecord rec)
        {
            var nowAvail = ComputeAvailability(rec, _serverConnected);
            if (nowAvail == rec.IsAvailable) return null;

            var powerBefore = EffectivePower(rec);
            var playbackBefore = EffectivePlayback(rec);

            rec.IsAvailable = nowAvail;
            var change = new AvailabilityChange { Mac = rec.MacAddress, NowAvailable = nowAvail };

            if (!nowAvail)
            {
                // Freeze metadata immediately on availability loss. The 30s
                // clear is handled by the freezer pump in the Lyrion Server
                // driver.
                rec.IsFrozen = true;
                rec.FrozenAtUtc = DateTime.UtcNow;
            }
            else if (rec.IsFrozen)
            {
                // The freeze only ever cleared via NoteMetadata, so a record
                // could sit available-but-frozen until the next status push,
                // publishing frozen payloads from the 1s tick meanwhile. Clear
                // it here and let consumers know the payload is live again.
                rec.IsFrozen = false;
                change.Unfrozen = SnapshotMetadata(rec);
            }

            var powerAfter = EffectivePower(rec);
            var playbackAfter = EffectivePlayback(rec);
            if (powerAfter != powerBefore) change.PowerEdge = powerAfter;
            if (playbackAfter != playbackBefore) change.PlaybackEdge = playbackAfter;

            return change;
        }

        /// <summary>
        /// Publishes an availability change. Order matters and is the same on
        /// every path: on loss, the effective field edges go out FIRST and
        /// AvailabilityChanged LAST, so "unavailable" is a postcondition of
        /// "power and playback are already off" for anyone reacting to it; on
        /// restore, AvailabilityChanged goes FIRST, then the effective field
        /// edges, then the unfrozen metadata — so a consumer's Connected is
        /// already true when it is told the player is on.
        /// </summary>
        private void RaiseAvailabilityChange(AvailabilityChange c)
        {
            if (!c.NowAvailable)
            {
                if (c.PlaybackEdge.HasValue) try { PlaybackStateChanged?.Invoke(c.Mac, c.PlaybackEdge.Value); } catch { }
                if (c.PowerEdge.HasValue) try { PowerStateChanged?.Invoke(c.Mac, c.PowerEdge.Value); } catch { }
                try { AvailabilityChanged?.Invoke(c.Mac, false); } catch { }
            }
            else
            {
                try { AvailabilityChanged?.Invoke(c.Mac, true); } catch { }
                if (c.PowerEdge.HasValue) try { PowerStateChanged?.Invoke(c.Mac, c.PowerEdge.Value); } catch { }
                if (c.PlaybackEdge.HasValue) try { PlaybackStateChanged?.Invoke(c.Mac, c.PlaybackEdge.Value); } catch { }
                if (c.Unfrozen != null) try { MetadataUpdated?.Invoke(c.Mac, c.Unfrozen); } catch { }
            }
        }

        // ===== Per-player mutators =====
        // Each returns true and raises the corresponding event when state
        // actually changed. The Lyrion Server driver calls these from the CLI
        // receive thread.

        public void NoteLifecycle(string mac, PlayerLifecycleState newState)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            AvailabilityChange change;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                rec.LifecycleState = newState;
                rec.LastSeenUtc = DateTime.UtcNow;
                change = ApplyAvailability_NoLock(rec);
            }

            if (change != null) RaiseAvailabilityChange(change);
        }

        /// <summary>
        /// Mark a player as InvalidSession. Returns true if this is the first
        /// invalid-session for this player (the caller should attempt
        /// rediscovery once). Returns false if the retry has already been
        /// attempted, meaning the caller should mark Offline.
        /// </summary>
        /// <remarks>
        /// Goes through the same availability path as <see cref="NoteLifecycle"/>:
        /// InvalidSession is not Online, so the record becomes unavailable
        /// (lowered, frozen, published) immediately. Before 1.0.12 this only
        /// set the lifecycle field, leaving IsAvailable stale-true with no
        /// event — an internally inconsistent record that consumers kept
        /// treating as live, with the 1s tick advancing a ghost. It also means
        /// the documented "if still failing, mark OFFLINE" outcome no longer
        /// depends on a reply that may never come: an unanswered rediscovery
        /// leaves the player unavailable, which is the user-visible meaning of
        /// OFFLINE.
        /// </remarks>
        public bool NoteInvalidSession(string mac)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return false;

            AvailabilityChange change;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return false;
                if (rec.LifecycleState == PlayerLifecycleState.InvalidSession) return false;

                rec.LifecycleState = PlayerLifecycleState.InvalidSession;
                change = ApplyAvailability_NoLock(rec);
            }

            if (change != null) RaiseAvailabilityChange(change);
            return true;
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
            LyrionPlaybackState effectiveState;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                var playbackBefore = EffectivePlayback(rec);
                var powerBefore = EffectivePower(rec);
                rec.PlaybackState = state;

                // Power derivation (CLAUDE.md §C) is a FALLBACK that must not
                // override an explicit LMS power signal. Only active playback
                // raises power. A pause is deliberately power-NEUTRAL: LMS
                // emits "<mac> pause 1" and "<mac> playlist pause 1" about a
                // millisecond after "<mac> power 0" as part of its own
                // power-off sequence, so treating a pause as playback would
                // re-raise power immediately after an external power-off and
                // hand Crestron Home a spurious ON edge — which a "Power Is On
                // -> Room On" media-function mapping turns straight back into
                // a real power-on. A stop only implies power off for players
                // that never report an explicit power state — a player powered
                // on but idle (stopped) keeps its explicit "on" state.
                var desiredPower = rec.IsPoweredOn;
                if (state == LyrionPlaybackState.Playing)
                {
                    desiredPower = true;
                }
                else if (state == LyrionPlaybackState.Stopped && !rec.HasExplicitPower)
                {
                    desiredPower = false;
                }

                rec.IsPoweredOn = desiredPower;

                // Publish EFFECTIVE changes only (see the effective-state
                // section): a mutation while unavailable is stored and
                // silent; restore publishes the edges.
                effectiveState = EffectivePlayback(rec);
                changed = effectiveState != playbackBefore;
                var powerAfter = EffectivePower(rec);
                if (powerAfter != powerBefore)
                {
                    powerChanged = true;
                    nowPowered = powerAfter;
                }
            }

            if (changed) try { PlaybackStateChanged?.Invoke(canon, effectiveState); } catch { }
            if (powerChanged) try { PowerStateChanged?.Invoke(canon, nowPowered); } catch { }
        }

        public void NoteExplicitPower(string mac, bool isOn)
        {
            // The "<mac> power 0|1" CLI notification is the authoritative
            // power signal for players that actually have a power state.
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            bool publish;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;

                // The FIRST explicit report for a record always publishes, even
                // when its value equals the blank default. This is still
                // change-gated in the honest sense: HasExplicitPower flipping
                // from false to true is the change, and it happens exactly
                // once per record. Without it a first observation of "power 0"
                // is silent (false == false), so a consumer whose own copy is
                // stale — a Source that stayed loaded through a Lyrion Server
                // driver reload while the player was switched off — would
                // never be told. Consumers change-gate on their side, so a
                // first report that matches what they already hold is a no-op
                // there. Bind-time snapshots are deliberately NOT forced for
                // unobserved records (see SourceDriver.ApplySnapshot); this
                // is the other half of that contract: the first real
                // observation is the moment consumers get synced.
                var first = !rec.HasExplicitPower;
                var before = EffectivePower(rec);
                rec.HasExplicitPower = true;
                rec.IsPoweredOn = isOn;
                var after = EffectivePower(rec);

                // Publish on an EFFECTIVE change. While the player is
                // unavailable this stores the raw value and publishes nothing
                // (effective is off either way); restore publishes the edge.
                // The first-report rule applies only while available — that
                // is the one case a consumer could hold a stale copy the
                // registry cannot see.
                publish = after != before || (first && rec.IsAvailable);
                isOn = after;
            }

            if (publish) try { PowerStateChanged?.Invoke(canon, isOn); } catch { }
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

        /// <summary>
        /// Records the configured volume step for a player (published by the
        /// Receiver from its VolumeStep user attribute). Clamped to 1–50 and
        /// change-gated. Consumers use it so their Vol+/- move by the same amount.
        /// </summary>
        public void NoteVolumeStep(string mac, int step)
        {
            if (step < 1) step = 1;
            if (step > 50) step = 50;

            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            bool changed;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                changed = rec.VolumeStep != step;
                if (changed) rec.VolumeStep = step;
            }

            if (changed) try { VolumeStepChanged?.Invoke(canon, step); } catch { }
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

        /// <summary>
        /// Updates the player's human-readable name. Change-gated: only raises
        /// <see cref="NameChanged"/> when the value actually changes.
        /// </summary>
        public void NoteName(string mac, string name)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null || string.IsNullOrEmpty(name)) return;

            bool changed;
            string applied;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;
                applied = Cap(name);
                changed = !string.Equals(rec.Name, applied, StringComparison.Ordinal);
                if (changed) rec.Name = applied;
            }

            if (changed) try { NameChanged?.Invoke(canon, applied); } catch { }
        }

        public void NoteMetadata(
            string mac,
            string title,
            string artist,
            string album,
            int trackNumber,
            int durationSeconds,
            int positionSeconds)
        {
            var canon = MacAddress.Normalize(mac);
            if (canon == null) return;

            LyrionMetadata snapshot;
            lock (_gate)
            {
                if (!_records.TryGetValue(canon, out var rec)) return;

                // Change-gated like every other mutator (it was the one that
                // wasn't: before 1.0.12 every 30s status keep-alive fanned an
                // identical payload to all three consumers, and the Helper
                // re-committed its now-playing properties into Crestron Home
                // each time, forever).
                var newTitle = Cap(title ?? rec.Title ?? string.Empty);
                var newArtist = Cap(artist ?? rec.Artist ?? string.Empty);
                var newAlbum = Cap(album ?? rec.Album ?? string.Empty);
                var newTrack = trackNumber >= 0 ? trackNumber : rec.TrackNumber;
                var newDuration = durationSeconds >= 0 ? durationSeconds : rec.DurationSeconds;
                var newPosition = positionSeconds >= 0 ? positionSeconds : rec.PositionSeconds;

                // A freeze is lifted only for a player that is actually
                // available. A status reply can arrive for an unavailable
                // record (the subscription keeps pushing keep-alives for a
                // disconnected client; a reconcile pass walks every bound
                // MAC), and blindly clearing IsFrozen there defeated the 30s
                // clear: the sweep requires frozen && unavailable, so stale
                // title/artist stayed on screen indefinitely.
                var unfreeze = rec.IsFrozen && rec.IsAvailable;

                var changed = unfreeze
                    || !string.Equals(rec.Title, newTitle, StringComparison.Ordinal)
                    || !string.Equals(rec.Artist, newArtist, StringComparison.Ordinal)
                    || !string.Equals(rec.Album, newAlbum, StringComparison.Ordinal)
                    || rec.TrackNumber != newTrack
                    || rec.DurationSeconds != newDuration
                    || rec.PositionSeconds != newPosition;

                if (!changed) return;

                rec.Title = newTitle;
                rec.Artist = newArtist;
                rec.Album = newAlbum;
                rec.TrackNumber = newTrack;
                rec.DurationSeconds = newDuration;
                rec.PositionSeconds = newPosition;
                if (unfreeze) rec.IsFrozen = false;
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

        /// <summary>
        /// Sweep metadata-freeze records. If a record has been frozen for
        /// 30s+, clear it and republish empty metadata. Called once per
        /// second by the Lyrion Server driver's pump.
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
                        && (rec.Title.Length > 0 || rec.Artist.Length > 0 || rec.Album.Length > 0))
                    {
                        rec.Title = string.Empty;
                        rec.Artist = string.Empty;
                        rec.Album = string.Empty;
                        rec.TrackNumber = 0;
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
        /// Advance the playback position by one second for every player that is
        /// currently Playing and available, then republish its metadata. Called
        /// once per second by the Lyrion Server driver's pump so the Helper's elapsed
        /// time counts up smoothly between the sparse status snapshots LMS
        /// pushes (otherwise elapsed only refreshes on the ~30s keep-alive).
        /// </summary>
        /// <remarks>
        /// Bounded by design: at most one MetadataUpdated per second per
        /// actively-playing player, and only the position advances. Paused and
        /// stopped players are skipped, so an idle system raises nothing. The
        /// authoritative position from each status push re-seeds the counter
        /// and corrects any drift.
        /// </remarks>
        public IReadOnlyList<(string mac, LyrionMetadata payload)> TickPlayingPositions()
        {
            List<(string, LyrionMetadata)> toPublish = null;

            lock (_gate)
            {
                foreach (var kvp in _records)
                {
                    var rec = kvp.Value;
                    if (rec.PlaybackState != LyrionPlaybackState.Playing) continue;
                    if (!rec.IsAvailable) continue;

                    // Don't run past a known track duration; live streams report
                    // duration 0, so they advance without a cap.
                    if (rec.DurationSeconds > 0 && rec.PositionSeconds >= rec.DurationSeconds)
                    {
                        continue;
                    }

                    rec.PositionSeconds += 1;
                    rec.LastMetadataUpdateUtc = DateTime.UtcNow;

                    if (toPublish == null) toPublish = new List<(string, LyrionMetadata)>();
                    toPublish.Add((rec.MacAddress, SnapshotMetadata(rec)));
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
        /// reconnect. The Lyrion Server driver calls this after recomputing state
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
                int vol, volStep;
                LyrionMetadata metaSnap;
                lock (_gate)
                {
                    if (!_records.TryGetValue(canon, out var rec)) continue;
                    avail = rec.IsAvailable;
                    power = EffectivePower(rec);
                    pbs = EffectivePlayback(rec);
                    vol = rec.Volume;
                    volStep = rec.VolumeStep;
                    muted = rec.Muted;
                    shuffle = rec.ShuffleEnabled;
                    repeat = rec.RepeatEnabled;
                    metaSnap = SnapshotMetadata(rec);
                }

                try { AvailabilityChanged?.Invoke(canon, avail); } catch { }
                try { PowerStateChanged?.Invoke(canon, power); } catch { }
                try { PlaybackStateChanged?.Invoke(canon, pbs); } catch { }
                try { VolumeChanged?.Invoke(canon, vol); } catch { }
                try { VolumeStepChanged?.Invoke(canon, volStep); } catch { }
                try { MuteChanged?.Invoke(canon, muted); } catch { }
                try { ShuffleChanged?.Invoke(canon, shuffle); } catch { }
                try { RepeatChanged?.Invoke(canon, repeat); } catch { }
                try { MetadataUpdated?.Invoke(canon, metaSnap); } catch { }
            }
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
                rec.TrackNumber,
                rec.DurationSeconds,
                rec.PositionSeconds,
                rec.IsFrozen);
        }

        private static string Cap(string value)
        {
            return value.Length <= MaxStringLength ? value : value.Substring(0, MaxStringLength);
        }
    }
}
