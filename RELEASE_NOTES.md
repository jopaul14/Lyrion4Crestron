# Release Notes

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
single Gateway owns the one and only connection to LMS.

### Drivers in this release

| Driver | Crestron device type | Instances | Version |
|---|---|---|---|
| `Gateway_Lyrion_LMS_IP` (Lyrion Server) | Platform (Entity Model) | 1 per home | 1.0.0 |
| `Source_Lyrion_Player` (Lyrion Source) | Bluray Player (RAD) | 1 per player | 1.0.0 |
| `Helper_Lyrion_Player` (Lyrion Helper) | Media Player extension (RAD) | 1 per player | 1.0.0 |
| `Receiver_Lyrion_Player` (Lyrion Receiver) | AV Receiver (RAD) | 1 per player (optional) | 1.0.0 |

Each driver ships as an independent `.pkg`. The Gateway is installed once per
home; the Source, Helper, and (optional) Receiver are installed once per
room/player and bound by the player's MAC address.

### Highlights

- **Single LMS connection.** Only the Gateway opens sockets to LMS — one
  persistent CLI connection plus stateless JSON-RPC over HTTP. The Source,
  Helper, and Receiver drivers never touch the network; they communicate with
  the Gateway through a process-wide service registry (`ILyrionGatewayService`).
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
- Transport/power retained for Crestron Home programming even when the source
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

- **Reconnect is a hard state boundary.** On reconnect the Gateway re-queries
  every bound MAC and recomputes availability, power, playback, volume, mute,
  shuffle, and repeat before republishing — no stale or out-of-order state.
- **Metadata freeze/clear.** Metadata freezes the instant a player goes
  unavailable and is cleared after 30 seconds if it stays offline.
- **Flash-safe, low-chatter logging.** The Gateway logs connectivity
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

Deploy the `.pkg` files via Crestron Toolbox, add the **Gateway first** (one per
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
