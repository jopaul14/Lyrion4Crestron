# Lyrion4Crestron — Product Requirements Document

**Status:** Authoritative. This document supersedes the original refactor specification (formerly in `CLAUDE.md`) and describes the system **as built** at driver version 1.0.13. Where this document and older documents disagree, this document wins.

**Audience:** Developers and contributors maintaining or extending the driver suite.

---

## Problem Statement

Lyrion Media Server (LMS, formerly Logitech Media Server / Squeezebox Server) owners who also run Crestron Home have no supported way to use their Squeezebox players as first-class audio sources in their home. Crestron Home does not allow third-party "Media Player" devices to act as routable sources, so a naive single-driver integration can offer controls but can never appear in the Source Routes graph, never be selected as a room's audio source, and never participate in whole-home audio routing.

Users want to walk up to a Crestron touchpanel or open the Crestron Home app, pick their Squeezebox player as the room's source, see what is playing, and control playback and volume — with the same reliability they expect from natively supported sources, and without the integration degrading the processor (log spam, flash wear) or misbehaving when the LMS server or a player goes offline.

## Solution

A suite of four cooperating Crestron Home drivers that together present each LMS player as a routable, controllable, richly-displayed audio source:

1. **Lyrion Server** — one instance per home. The only component that talks to LMS. Owns connectivity, player discovery, state derivation, metadata lifecycle, and logging. Exposes an in-process service that the other drivers consume.
2. **Lyrion Source** — one per room/player, bound by MAC address. A RAD "Bluray Player" driver that exists to be routable: it appears in the Crestron Home Source Routes graph with one digital and one analog audio output and offers only basic transport and power.
3. **Lyrion Helper** — one per room/player, bound by MAC address. A RAD "Media Player" extension device (not routable) that hosts the rich now-playing UI: title/artist/album, elapsed/duration, transport, shuffle/repeat, power, volume (Volume Up/Down and Mute buttons that step by the Receiver's configured amount), and up to four installer-configured presets.
4. **Lyrion Receiver** — optional, one per room/player, bound by MAC address. A RAD "AV Receiver" driver that acts as the routing endpoint and owns room volume, mute, power, and input selection. A third-party AVR driver may be used in its place.

This Source + Helper split (routable shell + extension-device UI) is the same pattern used by the BluOS, Linn, WiiM, and VSSL integrations, because it is the only way to get both routability and a rich media UI out of Crestron Home today.

## User Stories

### Homeowner — routing and playback

1. As a homeowner, I want my Squeezebox player to appear as a selectable audio source in a Crestron Home room, so that I can route it like any native source.
2. As a homeowner, I want to see the current track's title, artist, and album in the room view, so that I know what is playing without opening another app.
3. As a homeowner, I want to see elapsed time and track duration, so that I can tell where I am in a track.
4. As a homeowner, I want the elapsed time to advance smoothly every second while playing, so that the display doesn't jump in large steps.
5. As a homeowner, I want Play, Pause, Stop, Next, and Previous controls in the room view, so that I can control playback from Crestron Home.
6. As a homeowner, I want shuffle and repeat toggles, so that I can control playback modes without using the LMS app.
7. As a homeowner, I want to power the player on and off from Crestron Home, so that the room reflects and controls the player's real power state.
8. As a homeowner, I want the room's power tile to reflect the player's actual state — including changes made at the device itself or from another LMS app (e.g. Material Skin) — so that Crestron Home never shows stale power state.
9. As a homeowner, I want room volume (0–100), stepped volume up/down, and mute on the Receiver, so that I control loudness where the audio actually comes out.
10. As a homeowner, I want a player that is powered on but idle (stopped) to still show as ON, so that the UI matches reality rather than guessing from playback.

### Homeowner — resilience

11. As a homeowner, I want the now-playing display to freeze (not blank) the moment the server connection drops, so that a brief network blip doesn't flash empty screens.
12. As a homeowner, I want frozen metadata to clear after 30 seconds of continued outage, so that I'm not shown stale information indefinitely.
13. As a homeowner, I want everything (availability, power, playback, volume, mute, shuffle, repeat, metadata) to come back correct automatically after the server reconnects, so that I never have to "fix" the system after an LMS reboot.
14. As a homeowner, I want players to show as unavailable when the server is unreachable, so that the UI is honest about what can be controlled.

### Installer / dealer

15. As an installer, I want to configure the Lyrion Server once with the LMS hostname/IP, HTTP port (default 9000), CLI port (default 9090), and optional username/password, so that server settings live in exactly one place.
16. As an installer, I want to configure Source, Helper, and Receiver instances with nothing but a player MAC address, so that per-room setup is trivial and cannot drift from the server config.
17. As an installer, I want a configurable volume step size on the Receiver (default 2), so that volume ramping matches the client's speakers.
17a. As an installer, I want to configure up to four named, icon-bearing presets on the Helper — each a fixed LMS CLI fragment such as `favorites playlist play item_id:2` — so that a room offers exactly the playlists and favourites that belong there, without the driver enumerating the server's entire library.
17b. As an installer, I want each preset to also appear as an operation in Crestron Home sequences, so that one button press can power a player on, set its volume, and start a preset.
17c. As a homeowner, I want configured presets to appear as buttons on the Helper's now-playing page, and unconfigured ones to be absent rather than dead, so that the page only shows what actually works.
18. As an installer, I want to route the Source's digital or analog output to the Receiver's matching input (or to a third-party AVR), so that the integration fits the Source Routes graph like any native equipment.
19. As an installer, I want a clear warning in the log when a configured MAC address doesn't exist on the LMS server, so that typos are diagnosable without a debugger.
20. As an installer, I want the drivers to load into the same AppDomain and find each other automatically, so that no wiring beyond MAC binding is required.

### Crestron processor operator (reliability / hygiene)

21. As a processor operator, I want the integration to log only meaningful state transitions and errors, so that logs stay readable and flash wear is negligible.
22. As a processor operator, I want rapid server connect/disconnect oscillation collapsed into a single "connectivity unstable" notice plus the final stable state, so that a flapping network doesn't flood the log.
23. As a processor operator, I want routine player power changes to produce **no log lines at all**, so that normal daily listening leaves no log residue. (Power changes are normal behavior, not noteworthy events.)
24. As a processor operator, I want a single per-reconnect summary line (player counts, connects, disconnects), so that reconciliation is auditable without being chatty.
25. As a processor operator, I want commands issued while the server is disconnected to be dropped silently rather than queued or retried, so that reconnects never unleash a burst of stale commands.

### Developer / contributor

26. As a developer, I want all LMS protocol knowledge (CLI framing, JSON-RPC, token encoding, parsing) isolated in the Lyrion Server, so that the other three drivers stay trivial and protocol changes touch one assembly.
27. As a developer, I want a single typed service contract between the Lyrion Server and the per-room drivers, so that adding a consumer or extending an event is a contract change, not a protocol change.
28. As a developer, I want per-player state centralized in one registry keyed by MAC, so that every derived value (availability, power, playback, modes, metadata) has exactly one owner.
29. As a developer, I want all registry mutations change-gated, so that redundant LMS pushes produce zero events, zero UI updates, and zero log lines.
30. As a developer, I want reconnect handled as a hard state boundary with idempotent full reconciliation, so that missed or out-of-order events during an outage can never corrupt state.

## Implementation Decisions

### Architecture

- **Four packages, one AppDomain.** Lyrion Server (Entity Model SDK, DeviceType "Platform"), Source (RAD, "Bluray Player"), Helper (RAD extension, "Media Player", `IsExtensionDevice: true`), Receiver (RAD, "AV Receiver"). All four share DependencyGroup `LyrionLMS` so they load into the same AppDomain, and all depend on a shared `Lyrion_Common` assembly (embedded in each package) that carries the service contract and DTOs.
- **The Lyrion Server is the sole LMS client.** It maintains a persistent CLI socket (the live channel: commands, notifications, subscriptions) and a stateless JSON-RPC HTTP client (retained in the codebase but **reserved/unused** in v1.0.x). Source, Helper, and Receiver never open sockets and never speak LMS protocol.
- **In-process service rendezvous.** The Lyrion Server registers an `ILyrionServerService` implementation in a process-wide static registry; consumer drivers look it up (and are notified when it appears) and bind by MAC address. The contract carries bind/unbind, a snapshot query, ~11 state/metadata events, and transport/power/volume/mute commands plus a pass-through `SendPlayerCommand` used by the Helper's presets. Notable additions beyond the original spec, kept deliberately: a server-connectivity-changed event, a point-in-time snapshot query, and per-player capability flags (supports-power, supports-volume).
- **Thin adapters.** Source, Helper, and Receiver contain no business logic: they translate Crestron commands into service calls and service events into Crestron property updates. Each logs exactly one startup line ("Bound to MAC …").

### State model

- **Player registry.** A dictionary keyed by MAC → player record holding identity (name, ephemeral player ID, capabilities), lifecycle (UNKNOWN / OFFLINE / ONLINE / INVALID_SESSION, last-seen), derived state (availability, power), playback (state, position, duration), modes (shuffle, repeat), and metadata (title/artist/album/track number, freeze bookkeeping). Every mutator is change-gated: no change → no event.
- **Availability is server-dominant.** If the server is not CONNECTED, all players are unavailable, and per-player availability changes caused by a server disconnect are not logged individually.
- **Power semantics: explicit wins, derivation is fallback.** When LMS reports explicit power (a power notification or the status `power` field), that state is authoritative. Playback may only *raise* power, and only from `play` (playing ⇒ on); a stop lowers power only for players that have never reported explicit power. This keeps an on-but-idle player showing ON. **A pause is power-neutral — it neither raises nor lowers.** This is not a stylistic choice: LMS emits `<mac> pause 1` and `<mac> playlist pause 1` roughly one millisecond after `<mac> power 0` as part of its own power-off sequence, so a pause that raised power would publish a spurious ON edge on every external power-off. Downstream, a Crestron Home room with the media-function mapping "Power Is On → Room On" turns that edge straight back into a real power-on, and the room bounces back on ~1–2 s after the homeowner turns it off.
- **The registry owns availability's consequences, applied as *effective state* at the publish boundary.** A record keeps the RAW values LMS last reported; what consumers are told — and what `TryGetSnapshot` and `RepublishAll` expose — is the EFFECTIVE value: raw when the player is available, off/stopped when it is not. On any availability loss (server-level or per-player, including InvalidSession, and the Lyrion Server driver's own Dispose) the registry freezes metadata and publishes the effective edges *before* `AvailabilityChanged(false)`, so "unavailable" is a postcondition of "power and playback are already off". On restore it publishes `AvailabilityChanged(true)` *then* the effective edges, then the unfrozen metadata, so a consumer's `Connected` is already true when it is told the player is on. A mutation while unavailable stores the raw value and publishes nothing. Consumers' `UpdateAvailability` only sets `Connected`. 1.0.12 lowered the raw fields on loss and re-armed the first-report rule; that was right in spirit and let a status keep-alive for a *disconnected* client (`player_connected:0 power:1`) republish PoweredOn fourteen lines after being lowered, and let the post-reconnect first report publish ON before `Connected` went true. 1.0.13 moved the derivation to the boundary.
- **Consumers apply under one lock and never publish for an unobserved record.** Each consumer serialises bind (commit MAC, unbind previous, bind, snapshot, apply), every event handler, Dispose's unbind, and invalid-MAC unbinding under `_applyGate` (lock order `_applyGate` → `_gate`). Without it a CLI-thread event between the snapshot read and its forced apply was overwritten and — the registry publishing only on change — never corrected, and a Dispose racing an in-flight bind could decrement another consumer's shared bind count. A bind-time snapshot touches power/playback only when `IsObserved`; for an unobserved record it touches nothing but `Connected` — calling `UpdatePower` un-forced is not enough, because an un-forced false still passes the change-gate when the consumer holds ON (a Lyrion Server reload while playing). Snapshots are applied in the registry's order: available → `Connected` first then fields; unavailable → fields then `Connected`. `Connect()` restores the last availability the registry reported rather than forcing `Connected=true` (the framework re-runs it after any MAC edit). The Source aligns the RAD `PlayBackStatus` baseline to `Stop` once in `Initialize`, so no playback value is ever forced for an unobserved record. A cleared or unparseable MAC is an unbind with one warning line, not a silent no-op. This replaces the pre-1.0.12 arrangement where the Source and Helper derived "off/stopped" from availability themselves while the registry kept the pre-outage values: on restore the change-gated mutators compared the real value against the registry's *unchanged* copy and published nothing, leaving consumers OFF/Stopped for a player that was on and playing (a server reconnect was rescued by `RepublishAll`; a per-player reconnect never was), and `RepublishAll` re-emitted the stale ON for a player still offline. A status reply's `player_connected:0` is honoured as Offline; absence means Online.
- **A bind-time snapshot is force-published only if the record was observed; the first real observation always publishes.** `LyrionPlayerSnapshot.IsObserved` is set by the registry only after a FULL status response has been applied — the last statement of `ApplyStatusResponse`, after power, mode, volume, and metadata are noted. Source and Receiver force their bind-time power emit on that and nothing else. `IsAvailable` is explicitly not a proxy: it flips true on a `client new`/`reconnect` notification with no status at all, and inside a status response before the power field is parsed; 1.0.11 used it and left a window in which a consumer binding for a player that had just come online force-published "powered off" for it. Playback is not forced for an unobserved record either (1.0.13): the Source sets `PlayBackStatus = Stop` once in `Initialize`, aligning the RAD baseline (`NoDisc`) with the registry's default. The other half of the contract: the first explicit power report for a record publishes even when its value equals the default (`HasExplicitPower` false→true is the change, and it is re-armed by every availability loss), so a consumer whose copy went stale is synced at first sight. **General rule: never force-publish a value the Lyrion Server has not observed.** 1.0.8 broke this on availability-restore, 1.0.11 at bind, and 1.0.11's own proxy left the window above; 1.0.12 made "observed" a first-class registry fact.
- **Power is noted before mode in a status response.** The playback-derived power raise is a fallback; noting the explicit `power` field first means a reply carrying `mode:play` with `power:0` (LMS pauses ~1 ms after `power 0`, and a push can land between; synced slaves) no longer raises an ON edge that the explicit OFF immediately contradicts.
- **`NoteMetadata` is change-gated like every other mutator**, and lifts a freeze only for an available record — a status reply for an unavailable player (the subscription keeps pushing keep-alives for a disconnected client) must not defeat the 30 s clear.
- **Shuffle/repeat are booleans at the contract.** Shuffle ON maps to LMS "Shuffle Song"; Repeat ON maps to LMS "Repeat Playlist". Multi-state LMS values collapse to booleans.
- **Volume is 0–100 end-to-end**, no rescaling, and is exposed only through the Receiver (absolute set, step up/down with configurable step, mute).

### Connectivity and reconciliation

- **Server backoff:** 2s → 5s → 10s → 30s → 60s (capped). Commands never trigger reconnect attempts; while disconnected, commands are dropped silently by design.
- **Reconnect is a hard state boundary.** On transition to CONNECTED the Lyrion Server refreshes the full player list, re-resolves player IDs for all bound MACs, recomputes availability/power/playback/volume/mute/shuffle/repeat, republishes all derived state to consumers, then republishes metadata per the freeze/clear rules. Reconciliation is idempotent.
- **Per-player status subscription.** For each bound MAC the Lyrion Server opens a change-gated LMS subscribing status query (`<mac> status - 1 subscribe:30 tags:…`). This is push-on-change (not polling); the 30s figure is only a keep-alive ceiling. Subscriptions die with the CLI connection and are re-established on every (re)connect and on bind.
- **1-second position tick.** LMS does not push elapsed position continuously, and the Helper UI does not interpolate. The Lyrion Server advances position by one second for each *Playing, available* player on its existing 1s pump and republishes metadata (position field only). Authoritative status pushes re-seed position and correct drift. No logging, no flash writes.
- **Invalid session handling.** On player-ID rejection: mark INVALID_SESSION, rediscover once, retry the command once; if still failing, mark OFFLINE. Never infinite retries.
- **Metadata freeze/clear.** On loss of availability, metadata freezes immediately (frozen timestamp recorded). A 1s sweep clears metadata still frozen after 30 seconds. Reconnect republishes fresh metadata.

### Logging (normative surface)

The Lyrion Server is the only meaningful logger. The complete intended log surface is:

- Server connectivity **state transitions** (DISCONNECTED / CONNECTING / CONNECTED), smoothed with a 5-second minimum-stable window; rapid oscillation is collapsed into one "connectivity unstable — suppressing transition logs" notice plus the final stable transition.
- One **reconcile summary** line per reconnect (player/connect/disconnect counts).
- A **warning when a bound MAC is not present on LMS** (misconfiguration signal).
- **Errors only** otherwise: authentication failure, retry exhaustion, fatal protocol errors. No auth-success logs, no retry-attempt logs.
- **Explicitly excluded:** per-player power-state change logging. Power changes are normal behavior and occur constantly during ordinary use (playback can derive power changes); they must not be logged. (A diagnostic power-trace log existed in 1.0.2 and has been removed.)
- Consumer drivers log exactly one startup line each.

### Platform limitations (documented, not gaps)

- **Seek is not user-invokable, and the progress bar is gone.** Crestron Home's media-player extension UI has no draggable or tappable seek bar, so a progress bar could only ever be a read-only gauge — and Crestron Home renders every control as a fixed-height card, so it cost a full card to draw one thin line. As of 1.0.6 the Helper shows elapsed/duration as a text line on the track card instead (`Progress`, `HasDuration`, and `NoDuration` remain published on the property surface but are not drawn). `Seek` remains implemented in the service contract and Lyrion Server (LMS `time <sec>`) for completeness, but no user gesture can reach it.
- **The now-playing page is built to a card budget, against rules that are only discoverable on hardware.** Crestron Home exposes no padding, sizing, or styling controls, so vertical space is bought only by using fewer controls. Crestron Home — not the SDK — interprets `UiDefinition.xml`, and neither the build nor ManifestUtil validates it, so every constraint below was found by deploying and looking. They are recorded at the top of that file because they are invisible from the source:
  - **A `buttongroup` holds at most five buttons.** A six-button group does not render at all; the row silently vanishes rather than wrapping. A 1.0.6 attempt to put power + transport + modes on one line disappeared entirely for this reason.
  - **An icon and a label compete for the same button width,** and the failure mode depends on the row width. At three or four across the label is shown and *truncated* (`icon="#icPlus" label="Vol +"` rendered as `+  V...`). At five across the label is dropped and only the icon shows. So at three or four across a button gets an icon or a label, never both; at five across the icon must carry the button alone; at two across both fit.
  - **A `buttongroup`/`segmentedslider`/`buttongroup` "flanked" `controlgroup` does not lay out on one line** — it stacks, orphaning the trailing button in a card of its own.
  The resulting page is: header, track card (four lines including timing), a five-button playback row, a four-button power+volume row, and the preset rows at two across (the width where a preset's name and icon both fit).
- **Room on/off is a Crestron Home mapping, not a driver behaviour.** A source driver reporting its own power does not move a Crestron Home room's on/off state; the installer opts in by mapping the Source's Power Is On / Power Is Off to Room On / Room Off. The Source is the correct driver to map (the Receiver mirrors the same signal for the same MAC, so mapping both double-fires, and the Receiver is optional). Whether to map at all is a per-room judgement about how many sources the room has, not about which receiver is installed — see BUILD.md.
- **`Room On` requires a default route; the driver cannot supply one.** A Crestron Home room is on when a source is *routed* to it. `Room On` — from a mapping, a Quick Action, or a scene — routes the room's default source, so the room must have its Default Source (Source Routes → Available Sources) and Preferred Routing (Source and Audio Endpoint) set to the Lyrion devices. Without them `Room On` silently does nothing while `Room Off` keeps working, which presents exactly like a driver that emits `PoweredOff` but not `PoweredOn`. It is not: the Source emits both symmetrically on every real transition (verified 2026-09-02, including from the framework's IL). Diagnose this with a driver-free `Room On` Quick Action before touching power code; 1.0.8 skipped that step, changed the driver, and regressed. The Helper showing the player as on proves the registry raised the edge and nothing about the room — it publishes a level, not an edge.
- **Source capabilities are fixed by the RAD Bluray Player type.** The Source exposes only Play/Pause/Stop/Next/Previous and power — no volume, mute, shuffle, repeat, seek, or metadata. Rich UI lives exclusively in the Helper; volume lives exclusively in the Receiver.

- **"Preset" is the Crestron word; "playlist" and "favourite" are the LMS words.** They name the same thing from two directions. Crestron's vocabulary for a named, recallable device shortcut is *preset* — `IPresetController.RecallPreset`, `ITuner.PresetRecall`, `ADevicePreset`, tuner/pool/mixer/camera presets — so that is what the feature is called in the Crestron Home setup app, the Helper's UI, and the sequence editor. What a preset actually *starts* is an LMS playlist, favourite, or stream. Note that the driver does not implement Crestron's `IPresetController`: these are ordinary extension-device commands plus `[ProgrammableOperation]`s, and the "Presets" heading on the Helper page is the driver's own label, chosen to match installer expectations rather than inherited from the framework.
- **Presets are installer-declared, never discovered.** Each of the Helper's four preset slots is one pipe-delimited user attribute, `Name|Icon|Command` (e.g. `KCRW|icBroadcastRegular|favorites playlist play item_id:2`). The command is the CLI text *after* the MAC; the driver supplies the MAC and the newline. Parsing is deliberately forgiving (two-field `Name|Command` form, a `#`-prefixed icon, surrounding whitespace, and a `|` inside the command all work) and a slot that fails to parse renders as unconfigured rather than as a dead button. Presets reach LMS through `ILyrionServerService.SendPlayerCommand`, which strips control characters — the CLI is newline-delimited, so an embedded newline would otherwise let one configured value issue a second, unintended command. Each slot is also exposed to Crestron Home sequences via `[ProgrammableOperation]` on the Helper; those four names are fixed at compile time, because a driver's programming surface is baked into the package at build time (`programming/HelperDriver.json`) and cannot carry the installer's own labels.

### Dormant plumbing (present, unwired, no commitment)

- **JSON-RPC client.** Retained and structurally complete, reserved for future use; all live traffic is CLI.

### Configuration surface

| Driver | Fields |
|---|---|
| Lyrion Server | LMS hostname/IPv4 (required); HTTP port (default 9000); CLI port (default 9090); username/password (optional) |
| Source | Player MAC address (required) |
| Helper | Player MAC address (required, must match the room's Source) |
| Receiver | Player MAC address (required, must match the room's Source); volume step size (default 2) |

### Routing model

The Source declares one Coaxial Digital output (connector 30) and one RCA Analog output (connector 40). The Receiver declares matching Coaxial Digital and RCA Analog inputs plus speaker outputs. In Crestron Home Source Routes, the Source's output is routed to the Receiver's input (or to a third-party AVR used as the room endpoint). The Receiver (or AVR) owns room volume/mute/power as the routing endpoint; the Helper additionally surfaces volume/mute on its now-playing page (both route to the same Lyrion Server commands, and the Helper's step follows the Receiver's configured `VolumeStep`, shared per-MAC via the Lyrion Server). The Source never controls volume.

## Testing Decisions

- **Verification is manual, on real hardware.** Changes are validated against a live Crestron Home processor and a live LMS instance: deploy the `.pkg` files, configure a room, and exercise routing, transport, metadata display, power (including changes made externally via an LMS app such as Material Skin, to confirm push-driven state), volume/mute, shuffle/repeat, and the offline behaviors (server reboot for freeze/clear/reconcile; player reboot for per-MAC availability).
- **There is no automated test suite**, and adding one is not in scope for this document. The build itself provides the only automated gate (compile + `.pkg` production per driver).
- A good verification exercises **external behavior only**: what Crestron Home displays and what LMS actually does — never internal registry state. The acceptance-style checks that matter: only the Lyrion Server opens LMS connections; an LMS reboot produces a handful of Info lines total; a player reboot logs only for the affected MAC; no per-player logs during a server outage; routine power flips and steady playback produce zero log lines.

## Out of Scope

Removed or excluded from user-visible behavior (unchanged from the original design):

- **Sleep** — no capability, no timer, no UI.
- **Browsing, favorites, playlists, queue** — no browse trees, no favorites, no queue inspection/editing, no raw LMS command pass-through.
- **Player sync groups** — no group playback, group volume, or group coordination.
- **Volume in the Source** — the Source (RAD Bluray Player) never controls volume. Volume is owned by the Receiver (or a third-party AVR) and additionally mirrored on the Helper page (Vol±/Mute buttons sharing the Receiver's configured step via the Lyrion Server); it is never exposed on the Source.
- **High-frequency polling or chatty updates** — the change-gated push subscription and 1s position tick are the only sanctioned periodic activity.
- **Automated tests / CI** — verification remains manual on hardware.

**Playlist browsing** is a deliberate non-goal, and presets are the answer to it. An LMS library can hold hundreds of playlists and favourites, only a handful of which belong on a room's page; enumerating them would mean a browsing UI, paging, and a discovery/refresh cycle in the Lyrion Server, all to surface a list the homeowner would immediately want filtered. Presets invert that: the installer names the few entries that matter for the room, and the driver never has to scan the server at all.

## Further Notes

- **Document hierarchy.** This PRD is the authoritative description of product intent and behavior. `CLAUDE.md` contains only working instructions (build, conventions, invariants) and defers to this document. `BUILD.md` covers the build/packaging flow (Crestron SDK path, ManifestUtil, `.pkg` output). `README.md` and `RELEASE_NOTES.md` are user-facing.
- **Versioning convention.** Crestron Home reloads a driver only when the `DriverVersion` in its `Driver.json` changes; the suite's four driver versions are bumped together at release time. Code changes between releases do not bump versions individually.
- **Design lineage.** The server-centric architecture (single LMS connection, central registry, server-dominant availability, reconnect-as-refresh-boundary, capability-driven fallback, 0–100 volume) was validated against the Home Assistant Squeezebox integration; its chattier behaviors (frequent polling, per-event updates, browse/favorites, sync groups, monolithic player entity) were deliberately not adopted.
