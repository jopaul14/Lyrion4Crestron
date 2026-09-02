# Lyrion4Crestron — Working Instructions

Four-driver Crestron Home suite integrating Lyrion Media Server (LMS). The
four-driver refactor is **complete** (all drivers ship together at 1.0.12).

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
  Exactly three sanctioned publishes without a value change, each of which
  IS a change in disguise: the first explicit power report for a record
  (`HasExplicitPower` false→true — re-armed whenever the registry lowers a
  record on availability loss); `RepublishAll` after a committed server
  reconnect (a hard state boundary); and the availability-restore metadata
  publish that lifts a freeze (`IsFrozen` true→false). Anything else that
  publishes without a change is a bug.
- **The registry owns every derivation, including "unavailable ⇒ powered off
  and stopped".** Consumers never derive state from availability; on loss the
  registry lowers its own copy and publishes the edges, on restore the first
  real observation publishes. A consumer that keeps a second copy the registry
  does not know about will drift, and the registry's change-gate cannot see
  it (1.0.8, and the per-player desync fixed in 1.0.12).
- **Never force-publish a value the Lyrion Server has not observed.** The
  only honest signal is `LyrionPlayerSnapshot.IsObserved` (set after a FULL
  status response is applied). `IsAvailable` is not a proxy — it flips
  before power is parsed, and on `client new/reconnect` with no status.
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
- `Driver.json` is embedded as `<AssemblyName>.json`; `Lyrion_Common.dll` is
  embedded as a resource in each consumer package. All four packages share
  DependencyGroup `LyrionLMS` (same AppDomain).
- Verification is manual on real hardware (Crestron Home processor + live LMS);
  there is no automated test suite. See the PRD's Testing Decisions.
