# Release Notes

## 1.0.17 — Idle labels at load; an absent metadata field means empty (2026-09-04)

All four drivers ship at 1.0.17. Two bugs, both found on the 1.0.16 hardware
pass. **All four packages must be updated together.**

### Fixed — Lyrion Server

- **A radio stream inherited the previous track's artist and album.** Playing
  a local song and then selecting a radio favourite left "FROM IT STILL
  MOVES" under a stream that has no album — permanently, since no later reply
  ever contradicted it. Whichever field the station omits is the one that
  stays: no artist tag, the old artist stands; no album tag, the old album.
  The track title always updated, because streams do send one.

  `NoteMetadata` reads a null field as "keep what you had", which is correct
  for the `NewSong` notification — a genuine partial update carrying only a
  title, whose whole job is to survive until the full status query returns.
  But `ApplyStatusResponse` also passed null for a key that was simply
  absent, and that reply is the authoritative full picture: absent means the
  field is empty. Material Skin renders the same reply with the field
  missing, which is the behaviour to match.

  Title, artist and album now coerce an absent key to empty, and duration to
  0 (so a stream shows elapsed alone rather than hanging a finished track's
  total off it as `03:14 / 04:52`). The coercion is in `ApplyStatusResponse`
  rather than in `NoteMetadata`, so the partial `NewSong` path keeps its
  sentinels. Position deliberately keeps the "unchanged" sentinel: the 1 s
  pump advances it between replies.

### Fixed — Helper

- **A button could render `S...` or `Tex...` instead of its icon or label.**
  After a processor reboot, one room's Helper showed the literal text `S...`
  where the shuffle icon belongs and `Tex...` where the Mute label belongs,
  and kept showing them through every other button press and through leaving
  and re-entering the page. Pressing the affected button fixed it for good.

  Nothing had ever written those two properties, so Crestron Home rendered a
  placeholder: an unset icon makes a button fall back to its literal label
  (`S...` is "Shuffle" truncated at five across), and MuteBtn has no literal
  label to fall back to. They go unwritten when a player's first observed
  value happens to *equal* the record's default, because the registry
  change-gates and publishes nothing — and the Helper had bound before the
  player was observed, which touches nothing but `Connected` (1.0.14). The
  player in question reported `power:1 mode:play repeat:2 shuffle:0` and was
  unmuted: the three fields that differed from their defaults rendered
  correctly, and the exact two that matched did not. A player merely switched
  off at boot loses its power icon and tile status the same way.

  `HelperDriver.Initialize` now gives every bound label, icon and text line
  its idle value once, before any bind or event can arrive — the same
  baseline the Source sets with `PlayBackStatus = Stop`. It asserts nothing
  about a player: the values written are the labels for the state the
  properties already hold by default, so the two cannot disagree, and the
  first real observation overwrites them.

  Not fixed here, and worth doing deliberately later: the underlying rule
  that a first observation equal to the default publishes nothing. Power has
  an explicit exception (`HasExplicitPower` false→true); shuffle, repeat,
  mute, volume and name do not.

### Retest

1. **Cold boot with a default-valued field.** Set a player's shuffle off and
   leave it unmuted. Reboot the processor. **Every button on that room's
   Helper renders its icon or label** — no `S...`, no `Tex...` — before
   anything is pressed.
2. **Cold boot with the player switched off.** **The power button shows its
   glyph and the room tile reads `Off`**, rather than rendering blank.
3. **Real state still wins.** With shuffle on and the player muted, reboot.
   **The page shows shuffle on and "Unmute"**, not the idle defaults.
4. **Song to stream.** Play a local track with artist and album, then select
   a radio favourite that supplies neither. **Both lines clear**; the track
   title follows the stream. Then one that supplies an album but no artist:
   **the album line shows, the artist line is blank.** Go back to a local
   track: both return.
5. **Stream timing.** On that stream, **the time line shows elapsed alone** —
   no `/ total` left over from the song before it.

## 1.0.16 — A player LMS does not know is no longer a live player (2026-09-04)

All four drivers ship at 1.0.16. This fixes one bug, found on the 1.0.15
hardware pass. **All four packages must be updated together.**

### Fixed — Lyrion Server

- **A room could be switched on, and its position timer advanced, for a
  player that was not on the network at all.** With a player switched off and
  unplugged across an LMS restart, LMS came back without that player in its
  list — and Crestron Home showed it *online*. Power, Play and Pause on the
  Helper and the Receiver all appeared to work: the room turned on, the
  elapsed timer ran, paused, and resumed where it left off. Nothing was there.

  Three things had to line up. LMS does not reject a query for a MAC it does
  not know — it echoes the query back carrying no fields at all
  (`<mac> status - 1 tags:`, verified against LMS 9.1), which reaches
  `ApplyStatusResponse` as a status response whose only key is the `tags` of
  the echoed argument. That method treated an absent `player_connected` as
  Online, so the echo ran to the bottom and marked the record OBSERVED and
  Online — that is, AVAILABLE. Consumers were told the player was connected,
  `CanCommand` opened, and because LMS echoes every command back on the same
  socket and this driver keeps no request/response correlation, the driver's
  own `power 1` and `play` came back and were applied as if the server had
  pushed them.

  Two changes, and the existing effective-state model contains the rest (a
  mutation on an unavailable record stores the raw value and publishes
  nothing):

  - `ApplyStatusResponse` returns before noting anything unless the reply
    carries at least one of `player_connected`, `player_name`, `power` or
    `mode`. A real reply always carries all four. (The test cannot be "no
    keys": `tags:` parses as an empty-valued key, so the echo yields one.)
  - `ApplyPlayersResponse` marks a bound MAC that is missing from the
    server's player list as OFFLINE, instead of only logging the
    bound-MAC-missing warning — the driver already had the authoritative
    answer and discarded it. Only a *complete* reply counts (its `count:`
    must match the number of ids parsed), so a truncated list can never
    report a live player offline. A player that later joins LMS sends
    `client new`/`reconnect`, which restores it through the normal path.

  Not a regression from 1.0.10–1.0.15: through 1.0.11 a status reply marked
  a player Online unconditionally, so this behaved the same way or worse. It
  had simply never been tested with a MAC the server did not know.

  **This also covered up a mistyped MAC.** A well-formed MAC for a player
  that does not exist passed validation, bound, and then presented as a
  working player — right down to a room that turned on. It now shows offline,
  with the warning that was always logged.

### Retest

1. **Player absent from LMS entirely.** Switch a player off and unplug it.
   Restart LMS and confirm on the CLI (`players 0 50`) that the player is not
   listed. **In Crestron Home the room's devices must show offline**; the
   Helper and Receiver power buttons must do nothing; the position timer must
   not run. The log carries one
   `Lyrion Server WARNING: bound player <mac> not present on LMS`.
   Reconnect the player: it returns, powered off, and controls normally.
2. **Well-formed wrong MAC.** Set a Helper's MAC to a valid-format address no
   player uses (e.g. `aa:bb:cc:dd:ee:ff`). **The device shows offline and its
   buttons do nothing**, with the same one warning. Restore the MAC.
3. **Known-but-disconnected player still behaves.** The 1.0.14 case: two
   players playing, stop LMS, switch one off at the device, start LMS. That
   player's room stays off with no flicker; the other returns. (Distinct from
   test 1 — here LMS still lists the player.)
4. **Nothing else moved.** The 1.0.15 retest list unchanged, in particular the
   mute-survives-a-Server-reload and volume-ramp checks.

## 1.0.15 — Mute is observed; Receiver volume ramp; consumer lock scope (2026-09-02)

All four drivers ship at 1.0.15. This closes the nine findings of a full-file
review of the Receiver driver — the last of the five projects to get one.
**All four packages must be updated together.**

### Fixed — Lyrion Server

- **Mute was never observed, so a Lyrion Server reload while muted showed
  "unmuted".** A status reply has no mute field and nothing ever queried one,
  so a rebuilt record held `Muted=false` unobserved; two seconds after the
  reconnect `RepublishAll` pushed that to every consumer — the Receiver's mute
  and the Helper's Mute/Unmute label both flipped — and a muted player's
  volume, which LMS reports as a **negative** number while muted, was clamped
  to 0. The sign is now read: every status reply notes mute (negative =
  muted) and the absolute volume, so `IsObserved` vouches for mute like every
  other field. (Verified against LMS 9.1: `status` reports `mixer volume:-N`
  while `mixer muting` is 1.)
- **`RepublishAll` republished records no status reply had reached.** It
  pushed default volume/mute/name/metadata for them, fabricating edges on
  consumers still showing the real pre-outage values. It now skips unobserved
  records: their loss was already published at the disconnect, and they
  publish the moment their first status reply is applied.

### Fixed — Receiver

- **Press-and-hold on the room volume moved exactly one step.** Crestron Home
  delivers a hold as press then release and the framework ramps between them;
  the Receiver forwarded the press as a single step and never saw the release.
  It now steps once on press and every 300 ms until release (a tap is still
  one step), with a 12 s fuse should the release never arrive. The framework's
  own ramp is deliberately bypassed — it fabricates volume feedback each tick,
  which would fight the real feedback from LMS.
- **An invalid or cleared `VolumeStep` silently kept the previous step.** The
  same persisted value stepped by the old amount until a reload and by 2 after
  it. It now falls back to the default (2), re-publishes it to the Helper, and
  logs one warning for a non-blank invalid value.
- **A `VolumeStep` edit could race the bind's re-publish** and leave the
  Helper stepping by a different amount than the Receiver. The write and the
  publish now run under the apply lock.

### Fixed — Source, Receiver, Helper

- **The service swap on a Lyrion Server reload ran outside the apply lock.**
  A bind already in flight on the old service could force-apply a disposed
  registry's stale snapshot *after* the swap and leave it standing. The whole
  swap — detach, attach, rebind — now runs under `_applyGate`.
- **`Connect()` wrote `Connected` outside the apply lock.** Re-run by the
  framework after a MAC edit, it could interleave with a loss on the CLI
  thread and leave a stale true that the registry never corrects.
- **A mistyped MAC at first setup logged nothing.** A blank attribute still
  stays silent (an unconfigured driver at boot); a non-blank unparseable value
  now logs one warning.

### Changed

- Receiver `ApplySnapshot` collapsed to one branch (Connected first on a
  restore, last on a loss; fields only for an observed record).
- CLAUDE.md and the PRD record that mute is observed from the status volume
  sign, that `RepublishAll` skips unobserved records, and the widened
  apply-lock rule (service swap, `Connect()`, `VolumeStep`).

### Deferred

- **Shared consumer binding helper** (the review's reuse finding). The three
  consumers still hand-roll the same bind/apply/dispose choreography, and this
  release applied the same two lock-scope fixes three times over. Deferred to
  after the 1.0.15 hardware pass: it is a refactor of all three consumers, and
  there is no compiler on the development machine to catch a slip.

### Retest (1.0.15)

1. **Mute survives a Lyrion Server reload.** Mute a player from the Helper.
   Re-import only the Server package; wait 10 s. The Receiver's mute and the
   Helper's "Unmute" label must still show muted, and the Receiver's volume
   must show the real level, not 0. Unmute from the Helper: both must follow.
2. **Mute is seen at first sight.** Mute a player from the LMS web UI, then
   reboot the processor. After boot the Receiver and the Helper must show
   muted without any tap.
3. **Volume hold ramps.** In the Crestron Home app hold the room volume-up
   for about two seconds: the volume must climb several steps and stop on
   release; a tap must move exactly one step. Same for volume-down.
4. **VolumeStep fallback.** Set the Receiver's VolumeStep to `x`: Vol± on
   the Receiver and the Helper must step by 2 and the log must carry one
   WARNING. Clear it: still 2, no warning. Set 5: both step by 5.
5. **Typo at first setup.** Pair a fresh Receiver with a one-character-short
   MAC: one WARNING "nothing bound" in the log, no "Bound to MAC". Correct
   it: "Bound to MAC" follows.
6. **Regression pass.** The 1.0.14 retest list unchanged: LMS restart with a
   player switched off during the outage, offline tile tap, invalid-MAC
   unbind, tile glyph.

## 1.0.14 — Available means freshly observed; Helper hardening (2026-09-02)

All four drivers ship at 1.0.14. This closes the ten findings of a full-file
review of the Helper driver — two of them regressions from 1.0.13. **All four
packages must be updated together.**

### Fixed — Lyrion Server

- **A reconnect republished cached pre-outage state before any status
  arrived.** 1.0.13's effective-state model publishes edges the instant a
  player becomes available, but a player's lifecycle survived a server outage
  as Online, so `SetServerConnected(true)` made every record available at once
  and published its *cached* raw power/playback. A player switched off during
  an LMS restart produced a PoweredOn edge — Room On — and the real OFF one
  round-trip later: the 1.0.5 bounce-back class. The driver still carried the
  comment saying this must never happen. Now a record becomes Online **only
  from a full status response, noted as that response's last step**;
  `client new`/`reconnect` only trigger the status query; a server-level loss
  resets every lifecycle to Unknown. "Available" is a postcondition of
  "freshly observed", by construction.
- **Commands for an unreachable player were handed to LMS as stored
  preferences.** `PowerToggle` on an offline player read its effective power
  (always false) and sent `power 1`; LMS applied it on reconnect and the room
  switched on unexpectedly. Every player command is now gated on the player
  being available (`CanCommand`), the same silent-drop rule the PRD applies to
  a disconnected server.
- **The 1 s pump could overlap itself.** `System.Threading.Timer` fires the
  next tick on another thread if the previous one is still running (a slow
  consumer commit inside the fan-out is enough), advancing the same record
  twice and racing payloads out of order. An `Interlocked` guard now skips
  the tick instead.

### Fixed — Source, Receiver, Helper

- **`Connect()` re-enabled a device the installer had just unbound.**
  1.0.13's `!bound || available` read an unbound driver as connected, so the
  framework's post-edit `Connect()` undid `UnbindInvalidMac`'s "offline" for
  the very edit that caused it. `Connect()` now applies `_lastAvailability`
  alone (initialised true; driven false by a loss or an invalid-MAC unbind).
- **An invalid-MAC unbind left the old player's name, track, volume and mute
  on screen.** It now blanks the whole view (Helper and Receiver).

### Fixed — Helper

- **A Lyrion Server reload wiped the live tile with a blank record, and mute
  could never recover.** The Helper wrote every level of an unobserved
  snapshot; name, track (defeating the 30 s freeze), volume, shuffle/repeat
  and mute were replaced with defaults, and any field whose real value equals
  the default was never corrected because the registry change-gates against
  that blank record — mute is not in a status reply at all, so a muted player
  showed "Mute" and the first tap was a no-op. The Helper now applies a
  bind-time snapshot for observed records only, like the Source and Receiver.
- **Preset edits and button presses bypassed the apply lock.**
  `OnPresetReceived` wrote four properties and committed on Crestron Home's
  configuration thread with no lock while a CLI-thread handler could be
  mid-commit; `DoCommand` read playback/mute/step state unlocked. Both now run
  under `_applyGate`.
- **One commit per unit of work, and nothing rewritten that did not change.**
  `Update*` methods now only assign, through a change-gated `Set`; each event
  handler, the bind snapshot, a preset edit and an unbind commit once. The
  1 Hz position tick used to rewrite fourteen properties, format time three
  times and rebuild four strings per playing player; it now writes the time
  text and touches nothing else. A bind cost ten commits; it costs one.
- **The room tile showed a pause icon while music played.** Its secondary
  icon was bound to the Play/Pause *button's* next-action glyph. A new
  `PlaybackStateIcon` (play while playing, pause otherwise) drives the tile;
  the button keeps its affordance.

### Changed

- CLAUDE.md and the PRD no longer claim `Lyrion_Common.dll` is embedded in
  the consumer packages. The Server embeds it; the consumers ship it as a
  package dependency declared in `Driver.json` — removing that entry breaks
  the consumer at load.
- CLAUDE.md invariants record the lifecycle rule, the command gate, and the
  widened apply-lock rule.

### Retest

1. **LMS restart with a player switched off during the outage.** Two
   players playing; stop LMS; switch one player off (front panel or Material
   Skin — LMS is down, so at the device); start LMS. **The switched-off
   player's room stays off with no flicker; the other's returns.**
2. **Offline player, tile tap.** Unplug a player; tap its room tile. **Nothing
   is sent; when the player is plugged back in it is in the state it was in
   before, not powered on.**
3. **Server-only reload while muted.** Mute a player; re-import only the
   Lyrion Server. **The Helper still says "Unmute" and one tap unmutes.**
4. **Invalid MAC.** Set a Helper's MAC to `xyz`. **The page blanks — no name,
   no track, volume 0 — and shows offline, and STAYS offline after the
   settings screen closes.** Restore the MAC.
5. **Tile glyph.** Play, then pause. **The room tile's secondary icon shows
   play while playing and pause while paused.**
6. Everything from the 1.0.13 retest still holds.

## 1.0.13 — Effective state at the boundary; consumer apply lock (2026-09-02)

All four drivers ship at 1.0.13. This closes the ten findings of a full-file
review of the Source driver — three of which were holes in 1.0.12's own fix.
**All four packages must be updated together** (shared contract semantics
changed; the consumers' bind behaviour changed to match).

### Fixed — the mechanism

- **"Unavailable ⇒ off/stopped" is now applied at the publish boundary, not
  written into the record.** 1.0.12 lowered the registry's raw power/playback
  on availability loss and re-armed the first-report rule. Two things it
  could not see: a status keep-alive for a *disconnected* client
  (`player_connected:0 power:1`) was noted Offline and lowered, and fourteen
  lines later its `power:1` counted as a first explicit report and was
  published — the Source emitted PoweredOn while disconnected, a "Power Is On
  → Room On" mapping turned on a room for an unreachable player, and the
  record stuck there; and after an LMS restart the first status reply lands
  while the FSM still holds the server disconnected, so the same rule
  published ON before `Connected` went true. Records now keep the raw values;
  every publish, snapshot, and republish exposes the *effective* value (raw
  when available, off/stopped when not). A mutation while unavailable stores
  and publishes nothing; loss publishes effective edges then unavailable;
  restore publishes available *then* the effective edges. Nothing is re-armed.

- **Consumers serialise every RAD-facing write under one apply lock.** Bind
  (commit MAC, unbind previous, bind, snapshot, apply), each event handler,
  Dispose's unbind, and invalid-MAC unbinding now run under `_applyGate`.
  Before: a CLI-thread event between the snapshot read and its forced apply
  was overwritten by the stale snapshot and — the registry publishing only on
  change — never corrected; and a Dispose or MAC edit racing an in-flight
  bind could unbind a MAC this driver had not yet bound, decrementing the
  Helper/Receiver's shared count (possibly deleting their record) and then
  leaking the late bind.

### Fixed — Source, Receiver, Helper

- **A Lyrion Server reload while playing emitted a fabricated PoweredOff.**
  The rebind's snapshot is a blank record; 1.0.12 called `UpdatePower(false)`
  un-forced, which is a no-op only when the consumer already holds false —
  against a Source holding ON it passed the change-gate. Consumers now touch
  power/playback only for an *observed* snapshot; for an unobserved one they
  touch nothing but `Connected`.
- **`Connect()` forced `Connected=true` over registry availability.** The
  framework re-runs it after any MAC edit, and the registry — change-gated on
  its own unchanged copy — never sent `AvailabilityChanged(false)` again.
  `Connect()` now restores the last availability reported (true only while
  unbound).
- **A cleared or unparseable MAC was silently ignored**, leaving the driver
  bound to and controlling the previous player. It is now an unbind: release
  the record, report off/stopped then offline, one warning line (silent when
  nothing was bound, so an unconfigured driver does not log at boot).
- **Bind-time playback was forced after `Connected=false`**, which a
  framework that drops state from a disconnected device would discard,
  leaving the RAD default `NoDisc`. The Source now sets `PlayBackStatus =
  Stop` once in `Initialize` and forces nothing for unobserved records.
  Snapshots are applied in the registry's order (available: `Connected`
  first; unavailable: fields first).
- The dead `_boundMac != mac` guard in `TryBindToServer` is gone (the method
  had already returned in that case).

### Fixed — Lyrion Server

- **`Dispose` never published an availability loss.** It unregistered the
  service and tore down the transport but, unlike `RebuildTransport`, never
  called `SetServerConnected(false)`, so consumers kept asserting a dead
  server's last state — and a replacement Server's blank record met consumers
  still holding ON. It now publishes the loss before unregistering.

### Changed

- `VersionDate` in all four `Driver.json` files now matches the release date
  (it had been stale since 1.0.10).
- CLAUDE.md's invariants now define "change" as a change in the effective
  value and record the consumer apply-lock rule.

### Retest

1. **Disconnected keep-alive.** Player playing; unplug its network. Room
   goes off. Wait ≥ 35 s (one keep-alive). **Room stays off**; the Helper
   stays off. Reconnect: room and Helper return.
2. **LMS restart.** Two players playing. Stop LMS ~30 s, start it. Both
   rooms show off during the outage and **come back on within ~8 s of LMS
   returning**, with no OFF/ON flicker at the end.
3. **Server-only reload while playing.** Re-import only the Lyrion Server.
   **No room turns off; playback continues; rooms show on once the new
   Server connects.**
4. **MAC edit while offline.** Player unplugged; edit the Source's MAC to
   itself and save. **The device still shows disconnected.**
5. **Invalid MAC.** Set a Source's MAC to `xyz`. **One WARNING line; the
   room's source shows off and disconnected; the other rooms are unaffected.**
   Restore the MAC.
6. Everything from the 1.0.12 retest still holds.

## 1.0.12 — Registry owns availability; transport and lifecycle fixes (2026-09-02)

All four drivers ship at 1.0.12. This release closes the ten findings of a
full-file review of the Lyrion Server and the shared contract. **All four
packages must be updated together:** `Lyrion_Common.dll` changed (a new
`IsObserved` field on the player snapshot), and a 1.0.11 consumer beside a
1.0.12 Server will fail to bind.

### Fixed — Lyrion Server

- **Re-saving the LMS settings while connected left the driver permanently
  "disconnected".** Rebuilding the transport forced the driver and registry
  to disconnected but never told the connectivity FSM, and detached the old
  socket's handler before it could report the drop. The FSM stayed committed
  =Connected, the new socket's Connected matched it, nothing was published,
  and every command was dropped and every player unavailable — silently —
  until LMS itself went down for more than five seconds. The FSM is now reset
  on every rebuild/teardown, so the replacement socket's Connected commits
  and reconciles normally.

- **A player that dropped off the network and came back stayed OFF/Stopped
  in Crestron Home while it was on and playing.** The Source and Helper
  derived "unavailable ⇒ off/stopped" themselves; the registry kept the
  pre-outage values; on restore the change-gated mutators compared the real
  value against the registry's *unchanged* copy and published nothing. This
  is the gap 1.0.8 tried to patch from the wrong side. The registry now owns
  that derivation: on any availability loss it lowers its own power/playback,
  publishes them as edges before `AvailabilityChanged(false)`, and re-arms
  the first-observation rule, so restore is a genuine edge every consumer
  receives. Consumers no longer derive anything from availability. As a
  consequence the Receiver now also reports PoweredOff for an unreachable
  player, matching the Source.

- **After an LMS restart, a player that was still offline was reported
  powered ON.** `RepublishAll` re-emitted the registry's stale `IsPoweredOn`
  for a record that had gone Offline before the outage; the Source lowered
  power on the availability event and raised it again on the next — a
  PoweredOn edge for an unreachable player, which a "Power Is On → Room On"
  mapping turned into a real Room On after every LMS reboot. Fixed by the
  registry lowering above.

- **Disposing or re-addressing one consumer killed the other two for the
  same room.** All three consumers bind the same MAC and shared one registry
  record; `Unbind` removed it outright. Reloading just the (optional)
  Receiver left the Source and Helper bound to a MAC the registry no longer
  knew — every notification and command dropped, no event, no log. Records
  are now reference-counted and removed only when the last consumer lets go.
  First-bind work (the initial status subscribe) now runs once per player
  rather than once per consumer.

- **A rejected login was an endless two-second reconnect loop with a log
  line each cycle and no explanation.** Two bugs: the backoff counter and
  the connect announcement were reset the instant a socket connected, before
  login, so a server that accepted and then closed the socket never advanced
  past the schedule's first step; and the "login failed" line could never be
  recognised, because the parser classifies it as `LoginAck` and the check
  required `GlobalRaw`. The schedule now resets only after a session that
  lived ten seconds, a short-lived accept-then-close keeps escalating in
  silence, and the auth failure is surfaced once per outage.

- **`NoteMetadata` published on every call and lifted freezes blindly.** It
  was the one registry mutator without a change-gate: every 30 s status
  keep-alive fanned an identical payload to all three consumers, and the
  Helper re-committed its now-playing properties into Crestron Home each
  time, forever. It also cleared `IsFrozen` unconditionally, so a keep-alive
  for a *disconnected* player un-froze its record and the documented 30 s
  clear never ran. Now gated on the six fields, and a freeze is lifted only
  for an available record. Separately, availability restore now lifts a
  freeze itself and publishes the live payload, instead of waiting for the
  next status push.

- **A status reply marked the player Online regardless of
  `player_connected`, and `client forget` left the record internally
  inconsistent.** Keep-alives for a disconnected client carry
  `player_connected:0`; that is now honoured (absence still means Online).
  `NoteInvalidSession` goes through the same availability path as every
  other lifecycle change, so a forgotten player becomes unavailable
  immediately instead of sitting available with a non-Online lifecycle and a
  1 s tick advancing a ghost.

- **`mode` was noted before `power` in a status reply.** A reply carrying
  `mode:play` with `power:0` raised a derived ON edge that the explicit OFF
  a few lines later contradicted — the 1.0.5 bounce-back class, repeated on
  every keep-alive while it held. Power is now noted first.

- **The 1.0.11 "observed" proxy had a hole.** It used `IsAvailable`, which
  flips true on `client new`/`reconnect` with no status at all, and inside
  a status response before power is parsed; a consumer binding in that
  window still force-published a blank PoweredOff. "Observed" is now a
  registry fact (`LyrionPlayerSnapshot.IsObserved`), set only after a full
  status response has been applied, and consumers force on it alone.
  Playback is forced regardless at bind, because the RAD default `NoDisc` is
  wrong for an idle audio player and the registry's default `Stopped` is
  right — the 1.0.11 change had left an idle player showing NoDisc after a
  cold boot.

- **The connectivity FSM logged "connectivity unstable" on every boot.** Its
  fast-flap test measured against a last-commit time initialised to
  construction time, so the first Connecting transition — always within
  milliseconds — looked like a flap. It now measures against "never".

### Changed

- The Lyrion Server's installer-facing description no longer claims a
  JSON-RPC connection (reserved, unused) and now names all three consumers.
- CLAUDE.md's change-gating invariant now lists its three sanctioned
  exceptions, and two new invariants: the registry owns every derivation,
  and never force-publish an unobserved value.

### Retest

1. **Per-player reconnect.** Player on and playing; pull its network cable
   for ~10 s; reconnect. Room shows off during the outage and **comes back
   on, playing, within a few seconds of reconnect.**
2. **LMS restart with one player offline.** Two players, one unplugged. Stop
   LMS for ~30 s, start it. **The unplugged player's room stays off; the
   other's returns.**
3. **Config re-save.** With everything connected, open the Lyrion Server's
   settings in Crestron Home and save them unchanged. **Within ~10 s the
   log shows `Lyrion Server: LMS CONNECTED`, and the rooms still work.**
4. **Reload one consumer.** Re-import only the Receiver package. **The
   Source and Helper for that room keep working.**
5. **Wrong password.** Set a bad LMS password. **One `ERROR auth` line, then
   the reconnect interval grows 2→5→10→30→60 s with no further log lines.**
   Restore the password.
6. **Boot log.** Reboot the processor. **No "connectivity unstable" line.**

## 1.0.11 — A processor reboot no longer shuts down a playing player (2026-09-02)

All four drivers ship at 1.0.11.

### Fixed

- **Rebooting the Crestron Home processor while a player was playing could
  power that player off.** Seen live with two players playing through a
  reboot: one was shut down every time, always the same one, with no
  configuration difference between the rooms.

  At startup, when a Source or Receiver bound to the Lyrion Server, it
  force-published its bind-time snapshot to Crestron Home. That force was
  added in 1.0.3 for the driver-reload case, where the registry holds real
  state that must reach Crestron Home even when it equals the framework
  default. But on a cold boot the registry record is brand new — nothing has
  been observed yet — so the driver was reporting **"powered off" for a
  player nobody had looked at**. With the recommended "Power Is Off → Room
  Off" mapping, Crestron Home acted on that fabrication: Room Off sent
  PowerOff, and LMS paused the player and switched it off. Which player died
  depended only on whether its Source bound before or after Crestron Home's
  Actions & Events engine was listening — deterministic by driver load
  order, hence always the same one.

  The bind-time snapshot is now force-published **only when the record has
  actually been observed** (it is available, which requires a status
  response to have arrived). A cold-boot snapshot is a change-gated no-op
  against the framework defaults, and the real state arrives seconds later
  as a genuine edge. This change only *removes* an unobserved assertion; it
  cannot produce a spurious power-on.

- **The first explicit power report for a player now always publishes**,
  even when its value equals the blank default. Previously a first
  observation of `power 0` was silent (`false == false`), which the change
  above would otherwise expose in one corner: a Lyrion Server driver reload
  while a player was switched off would leave a stale ON in a Source that
  stayed loaded. `HasExplicitPower` flipping from false to true is the
  change being gated on, and it happens once per record; consumers still
  change-gate on their side, so a first report matching what they hold is a
  no-op there.

### Retest

1. Two players off, both rooms off. Power the processor off. Power both
   players on and start music. Power the processor on. **Both keep playing;
   both rooms show on.**
2. One player on and playing, the other switched off. Reload only the
   Lyrion Server driver. (Re-importing it alone is safe here only because
   `Lyrion_Common.dll` did not change in 1.0.11; when it does, all four
   packages must move together — see the 1.0.10 note.) **The off player's
   room shows off; the playing player's room shows on.**

## 1.0.10 — The Gateway is now the Lyrion Server (2026-09-02)

All four drivers ship at 1.0.10. **No behaviour changed.** This release renames
the first driver so its package, code, and documentation match the name
installers actually see.

### Changed

- **`Gateway_Lyrion_LMS_IP.pkg` is now `Server_Lyrion_LMS_IP.pkg`.** The
  driver has always presented itself in the Crestron Home Setup app and
  Configure Pro as **Lyrion Server** (its `BaseModel`), so a package called
  "Gateway" sent people looking for a device that did not exist. The project,
  assembly, namespace (`…Lyrion.Gateway` → `…Lyrion.Server`), driver class
  (`GatewayDriver` → `ServerDriver`), and the shared contract
  (`ILyrionGatewayService` → `ILyrionServerService`, and its registry and
  implementation) were renamed to match. The driver GUID and
  `DependencyGroup` are unchanged, so Crestron Home treats it as the same
  driver.

- **Log prefixes** changed from `Gateway:` to `Lyrion Server:`, and the
  connectivity lines now say `LMS CONNECTED` / `LMS DISCONNECTED` rather than
  `Server CONNECTED`, because "Server" alone is now ambiguous. In this
  codebase *the Lyrion Server* is the driver; *LMS* is the media server it
  talks to. `ServerDriver` carries a remarks block explaining the history and
  that convention for anyone reading the code cold.

- **All documentation** now says Lyrion Server, and the deploy steps in
  BUILD.md give the exact Pair Devices path for each driver (Drivers →
  Platform / Blu-ray Player / Media Player / AV Receiver → Lyrion Community).

### Upgrading from 1.0.9 or earlier

Because the package filename changed, import `Server_Lyrion_LMS_IP.pkg` and
remove the old `Gateway_Lyrion_LMS_IP.pkg` from the processor's driver store.
The other three packages keep their names but are re-versioned so Crestron
Home reloads them; they embed the renamed `Lyrion_Common.dll` and **must** be
updated together with the Server — a 1.0.9 Source loading beside a 1.0.10
Server would look for `ILyrionGatewayService` and fail to bind.

## 1.0.9 — Revert 1.0.8 (2026-08-31)

**1.0.8 made room power tracking worse and is fully reverted here.** All four
drivers ship at 1.0.9, which is behaviourally identical to 1.0.7. The version
number moves forward rather than back because Crestron Home caches sideloaded
drivers by `DriverVersion` — re-publishing 1.0.7 would leave the 1.0.8
assemblies in place.

**If you are running 1.0.8, update.** Its regression is worse than the bug it
tried to fix.

### What 1.0.8 broke

Powering the player on from the Receiver stopped showing media on in the room.
The player did power on and playback did start — only the room's state was
wrong.

1.0.8 made a player's availability-restore re-emit its power state, forced past
the change-gate. But `ApplyStatusResponse` in the Lyrion Server parses a status push
in this order: `NoteLifecycle` first, then playback mode, then the `power`
field. `NoteLifecycle` is what flips availability, so the forced power emit ran
against the registry's *previous* power value — twenty-odd lines before the same
status response updated it. The result was a spurious, forced `PoweredOff`
landing microseconds ahead of the real `PoweredOn`: the 1.0.5 bounce-back
pathology inverted, and enough to leave a room with a "Power Is Off → Room Off"
mapping showing off.

The Receiver was hit hardest. In 1.0.7 it never emitted power on an availability
change at all, so this path was entirely new there — and the Receiver is the
device most installers power on.

### The original problem was configuration, not the driver

The bug 1.0.8 set out to fix — "Power Is Off → Room Off" works but "Power Is
On → Room On" does nothing — was **a missing default route in Crestron Home**.
A room is on when a source is routed to it; `Room On` routes the room's
*default* source, and with no Default Source (Source Routes → Available
Sources) or Preferred Routing set, it silently does nothing while `Room Off`
keeps working. Setting both to the Lyrion devices fixed it with **no driver
change at all**. BUILD.md step 6 now makes this a required setup step, and
step 8 says how to tell this apart from a driver fault in one minute (a
driver-free `Room On` Quick Action).

1.0.8's "edge starvation" diagnosis was wrong. It did not even fit the
reported sequence — a genuine off→on cycle still failed — and should not be
revisited. The Source emits `PoweredOn` and `PoweredOff` symmetrically on
every real transition; that was verified down to the framework's IL and was
never the problem.

## 1.0.7 — Helper layout fixes (2026-08-31)

All four drivers ship at 1.0.7. Driver behaviour is unchanged from 1.0.6; this
release exists to correct the now-playing page after 1.0.6 was tested on a
phone, and to give Crestron Home a new version number so it actually reloads
the packages.

### Fixed

- **The power/transport row did not render at all.** 1.0.6 put six buttons in
  one group; Crestron Home renders at most five and silently drops the entire
  row rather than wrapping it. Playback is back to the five-button row that
  works — Repeat / Previous / Play-Pause / Next / Shuffle — and Power has moved
  in with the volume controls.

- **"Vol -" and "Vol +" displayed as "-  V..." and "+  V...".** A button's icon
  and label compete for the same width, and at three or more across the label
  is truncated to fit. The +/- icons were decorative while the text was the
  actual affordance, so the icons are gone and the labels now render in full.

### Changed

- **The read-only volume level bar has been removed.** It cost a full-height
  card to draw one thin, unadjustable line. Room volume lives on the Receiver
  (or the room's own volume control), and Vol -/+ still step it by the
  Receiver's configured amount. The `SupportsVolume` property is still
  published, just no longer drawn.

- The now-playing page is now: source header, track card (title / artist /
  album / timing), a five-button playback row, a four-button Power / Vol - /
  Mute / Vol + row, then the preset rows.

- The layout rules learned from testing on hardware are recorded at the top of
  `UiDefinition.xml`: the five-button ceiling, the icon-versus-label width
  interaction at each row width, and the control-group stacking behaviour.
  Crestron Home — not the SDK — interprets that file, and neither the build nor
  ManifestUtil validates it, so these constraints are only discoverable by
  deploying.

## 1.0.6 — Configurable presets (2026-08-31)

All four drivers ship at 1.0.6. Includes everything in 1.0.5 below, which was
never released separately.

### Added

- **Up to four presets per room, configured on the Lyrion Helper.** Each is one
  optional user attribute in the Crestron Home setup app, entered as
  `Name|Icon|Command`:

  ```
  KCRW|icBroadcastRegular|favorites playlist play item_id:2
  ```

  The command is the LMS CLI text that follows the player MAC — the driver adds
  the MAC and the line feed. The icon field may be left empty (defaults to
  `icBroadcastRegular`), and the shorter `Name|Command` form works too.

  Configured presets appear as buttons under a "Presets" heading on the Helper's
  now-playing page, carrying the configured name and icon. Empty or unparseable
  slots are hidden, so a room that uses no presets looks exactly as it did
  before.

- **Presets as Crestron Home sequence operations.** The Helper exposes
  "Play Preset 1" … "Play Preset 4" to the event/scene/button-press editor, so a
  single button can power a player on, set its volume, and start a preset.

  This is why presets are declared rather than discovered: an LMS library can
  hold hundreds of playlists and favourites, and enumerating them would mean a
  browsing UI and a discovery cycle in the Lyrion Server to surface a list the
  homeowner would immediately want filtered. The installer names the few that
  matter for the room instead, and the driver never scans the server.

### Changed

- **The now-playing page is more compact,** to make room for presets without
  pushing anything off a phone screen. Three cards were removed and none added
  beyond the presets themselves:

  - The elapsed/duration line moved onto the track card's fourth line, so it no
    longer needs a card of its own.
  - Power moved into the transport row, which is now Power / Previous /
    Play-Pause / Next / Repeat / Shuffle on one line.
  - Volume is now a single `Vol − | Mute | Vol +` row above the level bar. It
    previously tried to wrap those buttons around the bar, which Crestron Home
    rendered stacked — orphaning Vol + in a card of its own.

- **The read-only progress bar has been removed.** It cost a full-height card to
  draw one thin line, it could never seek (Crestron Home has no draggable seek
  bar), and the elapsed/duration text says the same thing. The `Progress`,
  `HasDuration`, and `NoDuration` properties are still published for anyone
  building on the driver's property surface.

### Removed

- **The dormant LMS hardware-preset plumbing** — `LyrionPreset`, the
  `PresetsUpdated` event, `ActivatePreset`, `NotePresets`, and the `Presets`
  field on the player snapshot. It was never wired to anything (the event only
  ever fired with an empty list) and described a different feature: physical
  preset buttons on a Squeezebox Radio, not playlists. Its replacement is the
  configurable presets above, backed by a single pass-through
  `ILyrionServerService.SendPlayerCommand`.

### Security

- `SendPlayerCommand` strips control characters from the configured command. The
  LMS CLI is newline-delimited, so a preset containing a newline would otherwise
  be read by the server as two commands, letting one configured value issue a
  second one the installer never intended.

## 1.0.5 — Power-state bounce-back fix (2026-08-31)

All four drivers ship at 1.0.5.

### Fixed

- **A room configured with "Power Is On → Room On" / "Power Is Off → Room Off"
  turned itself back on 1–2 seconds after being powered off.** Turning a player
  off from Material Skin (or any other LMS app) briefly published a *power on*
  event, which Crestron Home's media-function mapping executed as a genuine
  Room On — restoring the route and restarting playback.

  Root cause: LMS emits `<mac> pause 1` and `<mac> playlist pause 1` about one
  millisecond after `<mac> power 0`, as part of its own power-off sequence. The
  registry's power-derivation fallback treated *paused* as playback, so that
  pause immediately re-raised power that the authoritative `power 0`
  notification had just lowered. Pause is now power-neutral: only `play` raises
  power, and only `stop` can lower it (and then only for players that never
  report an explicit power state, so an on-but-idle player still shows ON).

  Verified against a live LMS: an external power-off now yields exactly one
  power event, `OFF`, with the trailing pause and stop producing playback
  events only.

- **Per-player status subscriptions could stay dead after a brief server
  reconnect.** The `status … subscribe:30` subscriptions live on the CLI socket
  and die with it, but the connectivity FSM deliberately smooths away flaps
  shorter than its stability window — so a fast drop/reconnect re-armed
  `listen 1` without ever re-running reconciliation, and the richer status
  pushes (power, mode, metadata, volume) went silent until the next committed
  reconnect. Subscriptions are now re-armed off the raw socket transition as
  well, so they always follow the connection.

### Removed

- The temporary `DIAG` power-tracing lines added to the Source and Receiver in
  1.0.3. They violated the "no per-player power-change logging" rule and are no
  longer needed.

## 1.0.0 — Initial release (2026-06-04)

First public release of the **Lyrion Media Server – Crestron Certified Drivers**: a
four-driver suite that integrates [Lyrion Media Server](https://lyrion.org/)
(formerly Logitech Media Server / Squeezebox Server) with Crestron Home.

The suite splits responsibilities across four cooperating drivers so a Lyrion
player presents cleanly in Crestron Home's source-routing graph — a routable
audio source, a rich now-playing UI, and an optional volume endpoint — while a
single Lyrion Server owns the one and only connection to LMS.

### Drivers in this release

| Driver | Crestron device type | Instances | Version |
|---|---|---|---|
| `Server_Lyrion_LMS_IP` (Lyrion Server) | Platform (Entity Model) | 1 per home | 1.0.0 |
| `Source_Lyrion_Player` (Lyrion Source) | Bluray Player (RAD) | 1 per player | 1.0.0 |
| `Helper_Lyrion_Player` (Lyrion Helper) | Media Player extension (RAD) | 1 per player | 1.0.0 |
| `Receiver_Lyrion_Player` (Lyrion Receiver) | AV Receiver (RAD) | 1 per player (optional) | 1.0.0 |

Each driver ships as an independent `.pkg`. The Lyrion Server is installed once per
home; the Source, Helper, and (optional) Receiver are installed once per
room/player and bound by the player's MAC address.

### Highlights

- **Single LMS connection.** Only the Lyrion Server opens sockets to LMS — one
  persistent CLI connection plus stateless JSON-RPC over HTTP. The Source,
  Helper, and Receiver drivers never touch the network; they communicate with
  the Lyrion Server through a process-wide service registry (`ILyrionServerService`).
- **Routable source.** The Source declares one digital (Coaxial) and one analog
  (RCA) audio output, so a Lyrion player can be routed to any room endpoint in
  the Crestron Home Source Routes graph.
- **Rich now-playing UI.** The Helper extension surfaces title / artist / album /
  track number, an elapsed/duration progress bar, full transport,
  shuffle, repeat, and power — with a custom layout via `UiDefinition.xml`.
- **Optional volume endpoint.** The Receiver provides 0–100 absolute volume,
  step up/down, mute, and power, and declares matching digital + analog audio
  inputs plus speaker outputs. Omit it to use a 3rd-party AVR instead.

### Features by driver

**Lyrion Source — routable audio source**
- Play / Pause / Stop, Next / Previous, Power on / off / toggle
- Declares digital (Coaxial) + analog (RCA) audio outputs for routing
- Transport/power retained for Crestron Home programming even if the source
  tile is hidden from end users

**Lyrion Helper — rich UI extension**
- Source-name header (the LMS player name)
- Now-playing metadata: title, artist, album, track number, elapsed, duration
- Read-only progress bar with `hh:mm:ss` (hidden when duration is unknown, e.g.
  radio streams; Crestron Home does not support a draggable seek bar)
- Transport: Play / Pause / Stop / Next / Previous
- Shuffle and Repeat as state-driven button icons; power on / off / toggle
- Room-page tile reflects the player's on/off state and now-playing status

**Lyrion Receiver — routable audio endpoint (optional)**
- Volume 0–100 (absolute + step up/down), mute / unmute
- Power on / off / toggle
- Declares digital (Coaxial) + analog (RCA) audio inputs plus speaker outputs

### Reliability & behavior

- **Reconnect is a hard state boundary.** On reconnect the Lyrion Server re-queries
  every bound MAC and recomputes availability, power, playback, volume, mute,
  shuffle, and repeat before republishing — no stale or out-of-order state.
- **Metadata freeze/clear.** Metadata freezes the instant a player goes
  unavailable and is cleared after 30 seconds if it stays offline.
- **Flash-safe, low-chatter logging.** The Lyrion Server logs connectivity
  transitions only, with a 5-second minimum stable time and oscillation
  suppression; each room driver logs a single `Bound to MAC ...` line.
- **Bounded backoff.** CLI reconnect schedule: 2s → 5s → 10s → 30s → 60s (cap).
  Commands issued while disconnected are dropped by design rather than queued.
- **Capability-driven fallbacks.** Players that don't accept power-off receive
  `stop` instead — no warnings.

### Requirements

- Crestron Home with driver runtime **25.0000.0033** or later
- One reachable Lyrion Media Server instance (HTTP port, default 9000; CLI port,
  default 9090)
- Building from source requires Visual Studio 2019/2022 (.NET Framework 4.7.2)
  and the Crestron Certified Drivers SDK 27.0000.0024 or later — see
  [BUILD.md](BUILD.md)

### Installation

Deploy the `.pkg` files via Crestron Toolbox, add the **Lyrion Server first** (one per
home), then add the Source / Helper / (optional) Receiver per player using the
same MAC address on all three, and route the Source output to the room endpoint.
Full step-by-step instructions, including hiding the Source tile from the room
UI, are in [BUILD.md](BUILD.md).

### Not included in this release

The following are intentionally out of scope (see [docs/PRD.md](docs/PRD.md)
"Out of Scope"):

- No sleep timer
- No browse / favorites / queue APIs and no raw LMS command pass-through
- No volume control on the Source or Helper (volume lives on the Receiver)
- No player sync / group management

### Notes

- The Receiver driver is optional; if the room uses an external amp or AVR,
  install only the Source and Helper.
- This is the first release — there is no upgrade path from the earlier
  single-driver `Platform_Lyrion_LMS_IP` prototype.
