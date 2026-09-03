# Lyrion4Crestron — Working Instructions

Four-driver Crestron Home suite integrating Lyrion Media Server (LMS). The
four-driver refactor is **complete** (all drivers ship together at 1.0.14).

**The authoritative product/architecture document is [docs/PRD.md](docs/PRD.md).**
It describes the system as-built: architecture, driver contracts, behavioral
requirements (reconnect reconciliation, metadata freeze/clear, power semantics,
logging surface), platform limitations, dormant plumbing, and non-goals. Read it
before making behavioral changes; where any other document disagrees, the PRD wins.

## Layout

| Project | Role | Device type |
|---|---|---|
| `Server_Lyrion_LMS_IP` | Lyrion Server — sole LMS client, owns all state | Platform (Entity Model SDK) |
| `Source_Lyrion_Player` | Per-room routable audio source | Bluray Player (RAD) |
| `Helper_Lyrion_Player` | Per-room rich now-playing UI | Media Player extension (RAD) |
| `Receiver_Lyrion_Player` | Optional per-room volume/routing endpoint | AV Receiver (RAD) |
| `Common` | Shared service contract (`ILyrionServerService`, DTOs, registry) | class library |

## Invariants (never violate)

- **Only the Lyrion Server opens LMS connections** (CLI socket + reserved JSON-RPC).
  Source/Helper/Receiver never touch the network; they consume the Lyrion Server's
  service via `LyrionServerServiceRegistry` and bind by MAC address.
- **Source/Helper/Receiver are thin adapters** — no business logic, no state
  ownership. All derivation lives in the Lyrion Server's `PlayerRegistry`.
- **Logging is minimal and flash-safe.** Allowed: server connectivity
  transitions (smoothed, oscillation-suppressed), one reconcile summary per
  reconnect, bound-MAC-missing warning, real errors, one startup line per
  consumer driver. Not allowed: per-player power-change logs, retry-attempt
  logs, auth-success logs, or anything that fires during normal playback.
- **All registry mutations are change-gated** — no change, no event, no log.
  "Change" means a change in the EFFECTIVE value (see the next rule). Exactly
  three sanctioned publishes without one, each of which IS a change in
  disguise: the first explicit power report for an available record
  (`HasExplicitPower` false→true); `RepublishAll` after a committed server
  reconnect (a hard state boundary); and the availability-restore metadata
  publish that lifts a freeze (`IsFrozen` true→false). Anything else that
  publishes without a change is a bug.
- **The registry owns every derivation, and applies "unavailable ⇒ powered
  off and stopped" at the publish boundary, not in the record.** Records hold
  the RAW values LMS last reported; every publish, snapshot, and republish
  exposes the EFFECTIVE value (raw when available, off/stopped when not —
  `PlayerRegistry.EffectivePower/EffectivePlayback`). A mutation while
  unavailable changes the raw value and publishes nothing; availability loss
  publishes the effective edges then `AvailabilityChanged(false)`; restore
  publishes `AvailabilityChanged(true)` then the effective edges. Consumers
  never derive from availability and never publish a field edge while they
  report themselves disconnected. (1.0.12 lowered the raw fields instead and
  re-armed the first-report rule on loss; that let a keep-alive for a
  disconnected client republish PoweredOn, fixed in 1.0.13.)
- **A record becomes Online only from a full status response, and only after
  that response's fields are in the record.** `ApplyStatusResponse` notes
  lifecycle LAST; `client new`/`reconnect` only trigger a status query; a
  server-level loss resets every lifecycle to Unknown. So "available" is a
  postcondition of "freshly observed", and a restore can only ever publish
  values from the reply that made the player available — never cached
  pre-outage state (1.0.13 did, and a player switched off during an LMS
  restart produced a PoweredOn edge followed by the real OFF: fixed 1.0.14).
- **Every player command is gated on the player being available**, not just
  on the server being connected (`LyrionServerServiceImpl.CanCommand`). A
  command for an unreachable player is dropped silently; it is never handed
  to LMS as a stored preference to fire on reconnect.
- **Consumers serialise every write AND every read of RAD-facing state under
  one apply lock** (`_applyGate`: bind+snapshot+apply, each event handler,
  `DoCommand`, `SetUserAttribute`-driven writes such as presets, Dispose's
  unbind, invalid-MAC unbind). Lock order is `_applyGate` then `_gate`, never
  the reverse. `Update*` methods assign only (through a change-gated `Set`);
  the unit of work that called them commits once. A bind-time snapshot is
  applied for OBSERVED records only — for an unobserved one, touch nothing
  but `Connected`; do not "call it un-forced", an un-forced value still
  passes the change-gate when the consumer holds the opposite. `Connect()`
  applies `_lastAvailability` alone (initialised true; driven false by a
  loss or an invalid-MAC unbind) — never `!bound || available`.
- **Never force-publish a value the Lyrion Server has not observed.** The
  only honest signal is `LyrionPlayerSnapshot.IsObserved` (set after a FULL
  status response is applied). `IsAvailable` is now set after it, but it is
  still the wrong test: a record can be observed and unavailable.
- Volume (0–100, no rescaling) is owned by the Receiver but also surfaced on the
  Helper page (Vol±/Mute buttons); both route to the same
  Lyrion Server `SetVolume`/`VolumeUp`/`VolumeDown`/`SetMute`. The Helper's step follows
  the Receiver's configured `VolumeStep`, shared per-MAC through the Lyrion Server
  registry. Shuffle/repeat are booleans exposed only by the Helper. Seek is
  contract-only (Crestron Home has no draggable seek bar).
- **Presets are installer-declared, never discovered.** Four `Name|Icon|Command`
  user attributes on the Helper; the command is the LMS CLI text that follows the
  MAC. The driver never scans the server for playlists — browsing the library is
  an explicit non-goal (see the PRD). Presets reach LMS only through
  `ILyrionServerService.SendPlayerCommand`, which strips control characters so
  one configured value cannot smuggle in a second CLI command. The four
  `[ProgrammableOperation]` names on `HelperDriver` are baked into the package at
  build time (`programming/HelperDriver.json`) and cannot carry installer labels.

## Build

Requires .NET Framework 4.7.2 targeting and the Crestron Certified Drivers SDK
(default path `C:\Lyrion4Crestron\Crestron_SDK`; override with the
`CrestronSdkPath` env var or `/p:CrestronSdkPath=...`). Full details in
[BUILD.md](BUILD.md).

```
msbuild Lyrion4Crestron.sln /p:Configuration=Release /restore
```

Each driver's output is `<project>\bin\Release\net472\<project>.pkg` (produced
by ManifestUtil in an AfterBuild target; a missing `.pkg` fails the build, a
missing ManifestUtil downgrades to a dll-only warning).

## Conventions

- Crestron Home reloads a driver only when `Driver.json`'s `DriverVersion`
  changes. All four driver versions are bumped **together at release time**;
  individual code changes between releases do not bump versions.
- `Driver.json` is embedded as `<AssemblyName>.json`. `Lyrion_Common.dll` is
  embedded as a resource in the SERVER package only (its EntryPoint resolves
  it); the three consumers ship it as a plain package dependency declared in
  their Driver.json (RAD drivers have no AssemblyResolve hook), so removing
  that Dependencies entry breaks the consumer at load. All four packages share
  DependencyGroup `LyrionLMS` (same AppDomain).
- Verification is manual on real hardware (Crestron Home processor + live LMS);
  there is no automated test suite. See the PRD's Testing Decisions.
