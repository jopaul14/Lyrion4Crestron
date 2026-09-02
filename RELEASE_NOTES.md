# Release Notes

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
   Lyrion Server driver (or bump its version and re-import it alone). **The
   off player's room shows off; the playing player's room shows on.**

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
