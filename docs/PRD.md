# Lyrion4Crestron — Product Requirements Document

**Status:** Authoritative. This document supersedes the original refactor specification (formerly in `CLAUDE.md`) and describes the system **as built** at driver version 1.0.2. Where this document and older documents disagree, this document wins.

**Audience:** Developers and contributors maintaining or extending the driver suite.

---

## Problem Statement

Lyrion Media Server (LMS, formerly Logitech Media Server / Squeezebox Server) owners who also run Crestron Home have no supported way to use their Squeezebox players as first-class audio sources in their home. Crestron Home does not allow third-party "Media Player" devices to act as routable sources, so a naive single-driver integration can offer controls but can never appear in the Source Routes graph, never be selected as a room's audio source, and never participate in whole-home audio routing.

Users want to walk up to a Crestron touchpanel or open the Crestron Home app, pick their Squeezebox player as the room's source, see what is playing, and control playback and volume — with the same reliability they expect from natively supported sources, and without the integration degrading the processor (log spam, flash wear) or misbehaving when the LMS server or a player goes offline.

## Solution

A suite of four cooperating Crestron Home drivers that together present each LMS player as a routable, controllable, richly-displayed audio source:

1. **Lyrion Server (Gateway)** — one instance per home. The only component that talks to LMS. Owns connectivity, player discovery, state derivation, metadata lifecycle, and logging. Exposes an in-process service that the other drivers consume.
2. **Lyrion Source** — one per room/player, bound by MAC address. A RAD "Bluray Player" driver that exists to be routable: it appears in the Crestron Home Source Routes graph with one digital and one analog audio output and offers only basic transport and power.
3. **Lyrion Helper** — one per room/player, bound by MAC address. A RAD "Media Player" extension device (not routable) that hosts the rich now-playing UI: title/artist/album, elapsed/duration with progress bar, transport, shuffle/repeat, and power.
4. **Lyrion Receiver** — optional, one per room/player, bound by MAC address. A RAD "AV Receiver" driver that acts as the routing endpoint and owns room volume, mute, power, and input selection. A third-party AVR driver may be used in its place.

This Source + Helper split (routable shell + extension-device UI) is the same pattern used by the BluOS, Linn, WiiM, and VSSL integrations, because it is the only way to get both routability and a rich media UI out of Crestron Home today.

## User Stories

### Homeowner — routing and playback

1. As a homeowner, I want my Squeezebox player to appear as a selectable audio source in a Crestron Home room, so that I can route it like any native source.
2. As a homeowner, I want to see the current track's title, artist, and album in the room view, so that I know what is playing without opening another app.
3. As a homeowner, I want to see elapsed time and track duration with a progress bar, so that I can tell where I am in a track.
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

15. As an installer, I want to configure the Gateway once with the LMS hostname/IP, HTTP port (default 9000), CLI port (default 9090), and optional username/password, so that server settings live in exactly one place.
16. As an installer, I want to configure Source, Helper, and Receiver instances with nothing but a player MAC address, so that per-room setup is trivial and cannot drift from the server config.
17. As an installer, I want a configurable volume step size on the Receiver (default 2), so that volume ramping matches the client's speakers.
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

26. As a developer, I want all LMS protocol knowledge (CLI framing, JSON-RPC, token encoding, parsing) isolated in the Gateway, so that the other three drivers stay trivial and protocol changes touch one assembly.
27. As a developer, I want a single typed service contract between the Gateway and the per-room drivers, so that adding a consumer or extending an event is a contract change, not a protocol change.
28. As a developer, I want per-player state centralized in one registry keyed by MAC, so that every derived value (availability, power, playback, modes, metadata) has exactly one owner.
29. As a developer, I want all registry mutations change-gated, so that redundant LMS pushes produce zero events, zero UI updates, and zero log lines.
30. As a developer, I want reconnect handled as a hard state boundary with idempotent full reconciliation, so that missed or out-of-order events during an outage can never corrupt state.

## Implementation Decisions

### Architecture

- **Four packages, one AppDomain.** Gateway (Entity Model SDK, DeviceType "Platform"), Source (RAD, "Bluray Player"), Helper (RAD extension, "Media Player", `IsExtensionDevice: true`), Receiver (RAD, "AV Receiver"). All four share DependencyGroup `LyrionLMS` so they load into the same AppDomain, and all depend on a shared `Lyrion_Common` assembly (embedded in each package) that carries the service contract and DTOs.
- **Gateway is the sole LMS client.** It maintains a persistent CLI socket (the live channel: commands, notifications, subscriptions) and a stateless JSON-RPC HTTP client (retained in the codebase but **reserved/unused** in v1.0.x). Source, Helper, and Receiver never open sockets and never speak LMS protocol.
- **In-process service rendezvous.** The Gateway registers an `ILyrionGatewayService` implementation in a process-wide static registry; consumer drivers look it up (and are notified when it appears) and bind by MAC address. The contract carries bind/unbind, a snapshot query, ~11 state/metadata events, and transport/power/volume/mute/preset commands. Notable additions beyond the original spec, kept deliberately: a server-connectivity-changed event, a point-in-time snapshot query, and per-player capability flags (supports-power, supports-volume).
- **Thin adapters.** Source, Helper, and Receiver contain no business logic: they translate Crestron commands into service calls and service events into Crestron property updates. Each logs exactly one startup line ("Bound to MAC …").

### State model

- **Player registry.** A dictionary keyed by MAC → player record holding identity (name, ephemeral player ID, capabilities), lifecycle (UNKNOWN / OFFLINE / ONLINE / INVALID_SESSION, last-seen), derived state (availability, power), playback (state, position, duration), modes (shuffle, repeat), and metadata (title/artist/album/track number, freeze bookkeeping). Every mutator is change-gated: no change → no event.
- **Availability is server-dominant.** If the server is not CONNECTED, all players are unavailable, and per-player availability changes caused by a server disconnect are not logged individually.
- **Power semantics: explicit wins, derivation is fallback.** When LMS reports explicit power (a power notification or the status `power` field), that state is authoritative. Playback may only *raise* power (playing ⇒ on); a stop lowers power only for players that have never reported explicit power. This keeps an on-but-idle player showing ON.
- **Shuffle/repeat are booleans at the contract.** Shuffle ON maps to LMS "Shuffle Song"; Repeat ON maps to LMS "Repeat Playlist". Multi-state LMS values collapse to booleans.
- **Volume is 0–100 end-to-end**, no rescaling, and is exposed only through the Receiver (absolute set, step up/down with configurable step, mute).

### Connectivity and reconciliation

- **Server backoff:** 2s → 5s → 10s → 30s → 60s (capped). Commands never trigger reconnect attempts; while disconnected, commands are dropped silently by design.
- **Reconnect is a hard state boundary.** On transition to CONNECTED the Gateway refreshes the full player list, re-resolves player IDs for all bound MACs, recomputes availability/power/playback/volume/mute/shuffle/repeat, republishes all derived state to consumers, then republishes metadata per the freeze/clear rules. Reconciliation is idempotent.
- **Per-player status subscription.** For each bound MAC the Gateway opens a change-gated LMS subscribing status query (`<mac> status - 1 subscribe:30 tags:…`). This is push-on-change (not polling); the 30s figure is only a keep-alive ceiling. Subscriptions die with the CLI connection and are re-established on every (re)connect and on bind.
- **1-second position tick.** LMS does not push elapsed position continuously, and the Helper UI does not interpolate. The Gateway advances position by one second for each *Playing, available* player on its existing 1s pump and republishes metadata (position field only). Authoritative status pushes re-seed position and correct drift. No logging, no flash writes.
- **Invalid session handling.** On player-ID rejection: mark INVALID_SESSION, rediscover once, retry the command once; if still failing, mark OFFLINE. Never infinite retries.
- **Metadata freeze/clear.** On loss of availability, metadata freezes immediately (frozen timestamp recorded). A 1s sweep clears metadata still frozen after 30 seconds. Reconnect republishes fresh metadata.

### Logging (normative surface)

The Gateway is the only meaningful logger. The complete intended log surface is:

- Server connectivity **state transitions** (DISCONNECTED / CONNECTING / CONNECTED), smoothed with a 5-second minimum-stable window; rapid oscillation is collapsed into one "connectivity unstable — suppressing transition logs" notice plus the final stable transition.
- One **reconcile summary** line per reconnect (player/connect/disconnect counts).
- A **warning when a bound MAC is not present on LMS** (misconfiguration signal).
- **Errors only** otherwise: authentication failure, retry exhaustion, fatal protocol errors. No auth-success logs, no retry-attempt logs.
- **Explicitly excluded:** per-player power-state change logging. Power changes are normal behavior and occur constantly during ordinary use (playback can derive power changes); they must not be logged. (A diagnostic power-trace log existed in 1.0.2 and has been removed.)
- Consumer drivers log exactly one startup line each.

### Platform limitations (documented, not gaps)

- **Seek is not user-invokable.** Crestron Home's media-player extension UI does not support a draggable/tappable seek bar, so the Helper's progress bar is deliberately read-only (elapsed/duration hidden when duration is unknown). `Seek` remains implemented in the service contract and Gateway (LMS `time <sec>`) for completeness, but no user gesture can reach it.
- **Source capabilities are fixed by the RAD Bluray Player type.** The Source exposes only Play/Pause/Stop/Next/Previous and power — no volume, mute, shuffle, repeat, seek, or metadata. Rich UI lives exclusively in the Helper; volume lives exclusively in the Receiver.

### Dormant plumbing (present, unwired, no commitment)

- **Presets.** The contract defines preset DTOs, an activate-preset command, and a presets-updated event; the Gateway can send the LMS preset button command if invoked. However, the Gateway never discovers presets from LMS (status tags don't request them) and the Helper neither subscribes to preset events nor renders preset controls. The event only ever fires with an empty list. This plumbing is documented as reserved; it is neither a roadmap commitment nor a declared non-goal.
- **JSON-RPC client.** Retained and structurally complete, reserved for future use; all live traffic is CLI.

### Configuration surface

| Driver | Fields |
|---|---|
| Gateway | LMS hostname/IPv4 (required); HTTP port (default 9000); CLI port (default 9090); username/password (optional) |
| Source | Player MAC address (required) |
| Helper | Player MAC address (required, must match the room's Source) |
| Receiver | Player MAC address (required, must match the room's Source); volume step size (default 2) |

### Routing model

The Source declares one Coaxial Digital output (connector 30) and one RCA Analog output (connector 40). The Receiver declares matching Coaxial Digital and RCA Analog inputs plus speaker outputs. In Crestron Home Source Routes, the Source's output is routed to the Receiver's input (or to a third-party AVR used as the room endpoint). The Receiver (or AVR) owns room volume/mute/power; the Source and Helper never control volume.

## Testing Decisions

- **Verification is manual, on real hardware.** Changes are validated against a live Crestron Home processor and a live LMS instance: deploy the `.pkg` files, configure a room, and exercise routing, transport, metadata display, power (including changes made externally via an LMS app such as Material Skin, to confirm push-driven state), volume/mute, shuffle/repeat, and the offline behaviors (server reboot for freeze/clear/reconcile; player reboot for per-MAC availability).
- **There is no automated test suite**, and adding one is not in scope for this document. The build itself provides the only automated gate (compile + `.pkg` production per driver).
- A good verification exercises **external behavior only**: what Crestron Home displays and what LMS actually does — never internal registry state. The acceptance-style checks that matter: only the Gateway opens LMS connections; an LMS reboot produces a handful of Info lines total; a player reboot logs only for the affected MAC; no per-player logs during a server outage; routine power flips and steady playback produce zero log lines.

## Out of Scope

Removed or excluded from user-visible behavior (unchanged from the original design):

- **Sleep** — no capability, no timer, no UI.
- **Browsing, favorites, playlists, queue** — no browse trees, no favorites, no queue inspection/editing, no raw LMS command pass-through.
- **Player sync groups** — no group playback, group volume, or group coordination.
- **Volume in Source or Helper** — volume belongs to the Receiver (or a third-party AVR) only.
- **High-frequency polling or chatty updates** — the change-gated push subscription and 1s position tick are the only sanctioned periodic activity.
- **Automated tests / CI** — verification remains manual on hardware.

Presets are intentionally *not* listed here: they are dormant plumbing (see Implementation Decisions), not an excluded feature.

## Further Notes

- **Document hierarchy.** This PRD is the authoritative description of product intent and behavior. `CLAUDE.md` contains only working instructions (build, conventions, invariants) and defers to this document. `BUILD.md` covers the build/packaging flow (Crestron SDK path, ManifestUtil, `.pkg` output). `README.md` and `RELEASE_NOTES.md` are user-facing.
- **Versioning convention.** Crestron Home reloads a driver only when the `DriverVersion` in its `Driver.json` changes; the suite's four driver versions are bumped together at release time. Code changes between releases do not bump versions individually.
- **Design lineage.** The server-centric architecture (single LMS connection, central registry, server-dominant availability, reconnect-as-refresh-boundary, capability-driven fallback, 0–100 volume) was validated against the Home Assistant Squeezebox integration; its chattier behaviors (frequent polling, per-event updates, browse/favorites, sync groups, monolithic player entity) were deliberately not adopted.
