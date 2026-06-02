Lyrion Crestron Driver – Authoritative Refactor Specification
This file is the single source of truth for refactoring this repository.
All instructions here are FINAL and must not be re-interpreted or guessed.


PROJECT CONTEXT (CURRENT STATE)


This repository currently implements one Crestron Driver SDK V2 Platform driver (Platform_Lyrion_LMS_IP) that:

Maintains Lyrion Media Server (LMS) connectivity

CLI over TCP (persistent socket)
JSON-RPC over HTTP (/jsonrpc.js)


Exposes per-player ManagedDevice entities (one per configured MAC)
Provides:

Transport
Volume / mute
Power and Sleep
Repeat / shuffle (multi-state)
Browsing / favorites / queue controls



This architecture must be refactored.


TARGET END STATE (NEW ARCHITECTURE)


Refactor to a four-driver architecture designed specifically for Crestron Home.
Crestron Home does not support 3rd-party "Media Player" devices as routable
sources. To get a routable audio source plus a rich now-playing UI, the work
must be split across a routing-capable RAD driver (Bluray Player) and an
extension device (Media Player) that owns the UI. This split is the same
pattern used by BluOS, Linn, WiiM, and VSSL integrations.

DRIVER 1 – LYRION SERVER (GATEWAY)

Single instance per home
The ONLY driver that connects to LMS
Owns:

All LMS connectivity (CLI + JSON-RPC)
Reconnect and backoff logic
Player discovery and registry
Availability, power, and playback state derivation
Metadata lifecycle
Logging


Exposes a service API using Crestron SDK service registration

DRIVER 2 – LYRION SOURCE (PER-ROOM ROUTABLE AUDIO SOURCE)

One instance per room / per player
Configured by MAC address only
Never connects to LMS directly
Sends transport intent to Driver 1
Receives playback / power / availability state from Driver 1
Routable audio source — declares analog and digital audio outputs
Exposes only the transport / power controls natively supported by the
RAD Bluray Player type (Play / Pause / Stop / Next / Prev / Power)
No volume control
No rich now-playing UI (that lives in Driver 3)

DRIVER 3 – LYRION HELPER (PER-ROOM RICH UI / EXTENSION)

One instance per room / per player
Configured by MAC address only
Extension device (IsExtensionDevice = true); no routing role
Never connects to LMS directly
Hosts the full Crestron Home media-player UI:

Now-playing title / artist / album / elapsed / duration
Transport controls (Play / Pause / Stop / Next / Prev / Seek)
Shuffle (boolean) and Repeat (boolean)
Power commands


Sends user intent to Driver 1
Receives state and metadata events from Driver 1

DRIVER 4 – LYRION RECEIVER (OPTIONAL PER-ROOM ENDPOINT)

One instance per room / per player
Configured by MAC address only
Never connects to LMS directly
Provides room volume, mute, power, and routing endpoint
Declares analog and digital audio inputs plus speaker outputs
Optional (a 3rd-party AVR may be used as the room endpoint instead)



PACKAGING & ARCHITECTURE (FINAL)




Implement four separate Crestron driver packages / assemblies:

Gateway_Lyrion_LMS_IP   → Driver 1 (Lyrion Server)    — Entity Model SDK, DeviceType "Platform"
Source_Lyrion_Player    → Driver 2 (Lyrion Source)    — RAD framework, DeviceType "Bluray Player"
Helper_Lyrion_Player    → Driver 3 (Lyrion Helper)    — RAD framework extension, DeviceType "Media Player"
Receiver_Lyrion_Player  → Driver 4 (Lyrion Receiver)  — RAD framework, DeviceType "AV Receiver"



Driver 1 is the ONLY LMS network client


Drivers 2, 3, and 4 must NEVER:

Open sockets to LMS
Issue LMS CLI or JSON-RPC commands



Inter-driver communication MUST use Crestron SDK service registration

Driver 1 exposes a service (ILyrionGatewayService) via LyrionGatewayServiceRegistry
Drivers 2, 3, and 4 consume it
All four packages share DependencyGroup "LyrionLMS" so they load into the same AppDomain





CRESTRON HOME DEVICE TYPES & UI IDENTITY (FINAL)


DRIVER 1 – LYRION SERVER

Display name: Lyrion Server
DeviceType: Platform
Represents the LMS instance
No room assignment

DRIVER 2 – LYRION SOURCE

Display name: Lyrion Source
DeviceType: Bluray Player (RAD)
Acts as the routable audio source in the Crestron Home Source Routes graph
Must expose only what RAD Bluray Player supports natively:

Play / Pause / Stop
ForwardSkip (Next) / ReverseSkip (Previous)
PowerOn / PowerOff / TogglePower


Must declare audio outputs in CrestronSerialDeviceApi.Api.AudioInOut.Outputs:

One digital audio output (Coaxial Digital, connector 30)
One analog audio output (RCA Analog, connector 40)


Must NOT expose volume, mute, shuffle, repeat, seek, or rich metadata.

DRIVER 3 – LYRION HELPER

Display name: Lyrion Helper
DeviceType: Media Player (RAD extension)
ExtensionDeviceData.IsExtensionDevice: true
Not part of the audio routing graph
Hosts the rich Crestron Home media-player UI for one room
Must expose:

Now-playing metadata (Title, Artist, Album, Elapsed, Duration)
Transport controls (Play / Pause / Stop / Next / Previous / Seek)
Shuffle (boolean) and Repeat (boolean)
PowerOn / PowerOff / PowerToggle


Custom controls / now-playing layout are defined via a PairedExtensionUi.xml
file shipped inside the package.

DRIVER 4 – LYRION RECEIVER

Display name: Lyrion Receiver
DeviceType: AV Receiver (RAD)
Acts as a routing endpoint in the Crestron Home Source Routes graph
Must expose:

Volume (0–100, absolute + step up/down)
Mute
PowerOn / PowerOff / TogglePower
Input selection (routes Source's digital or analog output)


Must declare in CrestronSerialDeviceApi.Api.AudioInOut:

One digital audio input (Coaxial Digital, connector 30)
One analog audio input (RCA Analog, connector 40)
Speaker outputs for the room





CONFIGURATION REQUIREMENTS (FINAL)


DRIVER 1 – LYRION SERVER

Server hostname or IPv4 address (required)
HTTP Port (required, default 9000, user-changeable)
CLI Port (required, default 9090, user-changeable)
Username (optional)
Password (optional)

DRIVER 2 – LYRION SOURCE

Player MAC address (required)

DRIVER 3 – LYRION HELPER

Player MAC address (required) — must match the Source for the same room

DRIVER 4 – LYRION RECEIVER

Player MAC address (required) — must match the Source for the same room
Volume step size (required, default 2, user-changeable)



EXPLICITLY REMOVED FEATURES (DO NOT IMPLEMENT)


The following features must be completely removed from user-visible behavior:

Sleep (no capability, no timer, no UI)
Browsing / favorites / queue APIs

No playFavorite
No playPlaylist
No browse trees


Any per-player LMS network logic outside Driver 1
Volume control in Drivers 2 and 3
Now-playing / shuffle / repeat / seek in Driver 2 (these live in Driver 3 only)



WHAT TO KEEP (DO NOT REWRITE UNLESS NECESSARY)


KEEP THESE MOSTLY INTACT:
Protocol Layer:

LmsCliCommands.cs (do not expose Sleep)
LmsJsonRpcRequests.cs
LmsTokenCodec.cs
LmsCliParser.cs

Transport:

Persistent CLI client structure
Stateless JSON-RPC HTTP client structure
Modify retry, backoff, and logging behavior only

Inter-driver service:

ILyrionGatewayService contract in Common/Service
LyrionGatewayServiceRegistry
LyrionPlayerSnapshot / LyrionMetadata / LyrionPlaybackState / MacAddress

Build & SDK:

Entity Model SDK for Driver 1 (Gateway)
RAD framework (CrestronSerialDeviceApi JSON + RAD base classes) for Drivers 2, 3, 4
net472 targeting
Existing build and packaging flow (adapted to 4 packages)



MAJOR REQUIRED REFACTORS


A) CENTRALIZE ALL INTELLIGENCE IN DRIVER 1

Eliminate the "PlayerEntity does everything" model
Driver 1 owns:

State
Lifecycle
Power
Availability
Logging


Drivers 2, 3, and 4 are thin adapters:

Bind by MAC
Send commands
Display state



B) SHUFFLE / REPEAT (BOOLEAN ONLY)
Expose to Crestron Home via Driver 3 (Helper):

ShuffleEnabled (boolean)
RepeatEnabled (boolean)

Internal mapping:

Shuffle ON → LMS Shuffle Song
Repeat ON → LMS Repeat Playlist

C) POWER SEMANTICS (DRIVER 1 OWNS)
Expose to Drivers 2, 3, and 4:

PowerOn
PowerOff
PowerToggle
OnPowerStateChanged(mac, isOn)

Derivation:

isOn = true  → play or pause
isOn = false → stop or unavailable

D) VOLUME (DRIVER 4 ONLY)

Scale: 0–100
Absolute volume + step up/down
Mute
Default step = 2

E) METADATA OFFLINE BEHAVIOR

Freeze metadata immediately on disconnect
Clear metadata after 30 seconds if still offline
Republish metadata after reconnect

--------------------------------------------------------------------
RECONNECT STATE RECONCILIATION (REQUIRED)
--------------------------------------------------------------------

Server reconnect is a hard state boundary.

When Driver 1 transitions to CONNECTED after a disconnect, it MUST NOT
trust cached or incremental state.

On reconnect, Driver 1 must:

- Refresh the full player list from LMS
- Re-resolve player IDs for all configured MAC addresses
- Recompute availability for each player
- Recompute logical power state
- Recompute playback state (play / pause / stop)
- Recompute volume and mute state
- Recompute shuffle and repeat state
- Republish all derived state to Drivers 2, 3, and 4
- Then republish metadata according to the metadata freeze/clear rules

Reconnect handling must be idempotent and safe to execute more than once.



DRIVER 1 INTERNAL PLAYER REGISTRY (IMPLEMENT EXACTLY)


Dictionary keyed by MAC address → PlayerRecord
PlayerRecord fields:
Identity & Capabilities:

MacAddress
PlayerId (ephemeral)
CanPowerOff
SupportsVolume

Lifecycle:

State: UNKNOWN / OFFLINE / ONLINE / INVALID_SESSION
LastSeenUtc

Derived:

IsAvailable
IsPoweredOn

Playback:

State: Playing / Paused / Stopped
PositionSeconds
DurationSeconds

Modes:

ShuffleEnabled
RepeatEnabled

Metadata:

Title, Artist, Album
IsFrozen
FrozenAtUtc
LastMetadataUpdateUtc

Presets:

Optional list of { id, displayName }



RETRY & CONNECTION RULES (DRIVER 1 ONLY)


SERVER CONNECTION

Backoff sequence:
2s → 5s → 10s → 30s → 60s (cap)
No reconnect attempts triggered by commands
Commands dropped when server is not connected

PLAYER INVALID SESSION

On player ID rejection:

Mark INVALID_SESSION
Rediscover once
Retry command once


If still failing → OFFLINE
Never infinite retries



STRUCTURED LOGGING (MINIMAL & FLASH-SAFE)


GENERAL

Driver 1 is the only meaningful logger
Drivers 2, 3, and 4 log only once at startup:
"Source: Bound to MAC …" / "Helper: Bound to MAC …" / "Receiver: Bound to MAC …"

SERVER LOGGING (DRIVER 1)

Log state transitions only:
DISCONNECTED / CONNECTING / CONNECTED
Minimum stable time: 5 seconds

OSCILLATION SUPPRESSION

When rapid oscillation is detected, log once:
"INFO Gateway Server connectivity unstable — suppressing transition logs"
Suppress intermediate logs
Always log the final stable transition

PLAYER AVAILABILITY LOGGING

Log per-player availability only when server is CONNECTED
Do NOT log per-player availability caused by server disconnect
Player flapping uses the same suppression pattern

ERROR LOGGING ONLY

Authentication failure
Retry exhaustion
Fatal protocol errors
No auth-success logs
No retry-attempt logs



DRIVER CONTRACTS (MANDATORY)


DRIVER 1 → DRIVER 2 EVENTS (Source)

OnAvailabilityChanged(mac, bool)
OnPowerStateChanged(mac, bool)
OnPlaybackStateChanged(mac, Playing|Paused|Stopped)

DRIVER 2 → DRIVER 1 COMMANDS (Source)

Play / Pause / Stop / Next / Previous
PowerOn / PowerOff / PowerToggle

DRIVER 1 → DRIVER 3 EVENTS (Helper)

OnAvailabilityChanged(mac, bool)
OnPowerStateChanged(mac, bool)
OnPlaybackStateChanged(mac, Playing|Paused|Stopped)
OnMetadataUpdated(...)
OnShuffleChanged(bool)
OnRepeatChanged(bool)
OnPresetsUpdated(optional)

DRIVER 3 → DRIVER 1 COMMANDS (Helper)

Play / Pause / Stop / Next / Previous / Seek
SetShuffle(bool)
SetRepeat(bool)
PowerOn / PowerOff / PowerToggle
ActivatePreset(optional)

DRIVER 1 → DRIVER 4 EVENTS (Receiver)

OnAvailabilityChanged(mac, bool)
OnPowerStateChanged(mac, bool)
OnVolumeChanged(mac, 0..100)
OnMuteChanged(mac, bool)

DRIVER 4 → DRIVER 1 COMMANDS (Receiver)

SetVolume(0..100)
VolumeUp / VolumeDown
SetMute(bool)
PowerOn / PowerOff / PowerToggle



CRESTRON HOME ROUTING EXPECTATIONS



Driver 2 (Source) appears as a routable audio source with one digital and one analog output
Driver 4 (Receiver) appears as a routing endpoint with one digital input, one analog input, and speaker outputs
Source outputs are routed to Receiver inputs (or to a 3rd-party AVR in place of Driver 4)
Driver 4 controls room volume, mute, and power
Drivers 2 and 3 never control volume directly



ACCEPTANCE CRITERIA (MUST PASS)



Only Driver 1 opens LMS connections
LMS reboot produces 2–3 Info logs total
Player reboot logs only for affected MAC
No per-player logs during server outage
Sleep is completely removed
Favorites / browsing not present
Driver 2 appears as a routable source in Crestron Home Source Routes
Driver 3 surfaces the rich media-player UI in the Crestron Home room view
Driver 4 appears as a routable endpoint and controls room volume 0–100
Shuffle and Repeat map correctly via Driver 3
Metadata freeze/clear works as specified



APPENDIX – LESSONS FROM HOME ASSISTANT
(NON‑GOALS + CONFIRMED CHOICES)
This appendix documents lessons learned from reviewing the Home Assistant
"Squeezebox / Lyrion" integration.  It exists to clarify which design patterns
are CONFIRMED CORRECT for this project, and which behaviors are EXPLICIT
NON‑GOALS despite existing in other platforms.
This appendix does not introduce new requirements; it prevents incorrect
interpretation or over‑implementation.

A. CONFIRMED ARCHITECTURAL CHOICES
The following design decisions used in this project are validated by the
Home Assistant implementation and should be preserved.

SERVER‑CENTRIC ARCHITECTURE

Home Assistant uses a single LMS connection with a central coordinator and
player registry. Players do not own network connections.
This confirms the correctness of this project's design where:

Driver 1 is the only LMS client
All player state is owned and derived centrally
Drivers 2, 3, and 4 are thin adapters


SERVER‑DOMINANT AVAILABILITY

Home Assistant derives player availability from server reachability plus
player presence. If the server is offline, players are not available.
This confirms the invariant used in this project:

If server state != CONNECTED, all players are unavailable
Player availability changes caused by server disconnects are not logged
individually


RECONNECT AS A STATE BOUNDARY

Home Assistant treats server reconnect as a refresh boundary, because:

Events may be missed during disconnects
State changes may arrive out of order

This confirms the decision in this project that:

Driver 1 must refresh and recompute full player state after reconnect
Cached incremental deltas must not be trusted across reconnects


CAPABILITY‑DRIVEN BEHAVIOR (NOT ERRORS)

Home Assistant adapts behavior based on player capabilities (e.g. power‑off
support) without generating warnings.
This confirms the decision in this project to:

Avoid logging "capability mismatch" warnings
Silently fall back to supported behavior (e.g. Stop instead of PowerOff)


SINGLE VOLUME SCALE (0–100)

Home Assistant uses the LMS native 0–100 volume scale everywhere.
This confirms the choice to use:

A single 0–100 scale end‑to‑end
No internal rescaling or logarithmic mapping


B. EXPLICIT NON‑GOALS (DO NOT IMPLEMENT)
The following Home Assistant behaviors are intentionally NOT adopted.

NO CHATTER / HIGH‑FREQUENCY STATE UPDATES

Home Assistant updates state very frequently and assumes disk wear and
log volume are not concerns.
This project explicitly prioritizes:

Minimal logging
Flash safety
State‑change logging only
Suppression during oscillation

Do NOT introduce:

Frequent polling
High‑frequency logging
Per‑event UI updates beyond required state changes

PERMITTED EXCEPTION — CHANGE‑GATED STATUS SUBSCRIPTION

Driver 1 SHOULD open a per‑player subscribing status query
("<mac> status - 1 subscribe:N tags:..."). This is the authoritative,
notification‑format‑independent source of power, playback mode, and
metadata: LMS pushes a full status whenever the player status changes
(including changes triggered at the device itself, which the granular
"listen" notifications can miss), with N acting only as a keep‑alive
ceiling. This is NOT the "frequent polling" prohibited above:

- It is push‑on‑change, not a poll loop.
- N=30 (matching LMS Material) yields ~2 keep‑alive pushes/min per
  player at idle — far less than the existing global "listen 1" stream.
- Every registry mutator is change‑gated, so a push that carries no
  change raises no events: zero UI updates, zero logging, zero flash
  writes. The flash‑safety and minimal‑logging guarantees are preserved.

The subscription is established per bound MAC when the server connects
(and re‑established on every reconnect, since it dies with the CLI
connection).

PERMITTED EXCEPTION — 1‑SECOND ELAPSED POSITION TICK

LMS does not push elapsed position every second (the status subscribe
above only re‑pushes on change plus the keep‑alive), and the Helper UI
does NOT interpolate position itself, so without help the elapsed time
jumps in ~30s steps. Driver 1 therefore advances PositionSeconds by one
second for each Playing player on its existing 1s pump and republishes
metadata. This is bounded and spec‑safe:

- Only Playing + available players tick; paused/stopped players and an
  idle system raise nothing.
- At most one MetadataUpdated per second per actively‑playing player,
  and only the position field advances.
- It performs no logging and no flash writes.
- Each authoritative status push re‑seeds the position and corrects drift.

PERMITTED EXCEPTION — EXPLICIT POWER IS AUTHORITATIVE

The §C power derivation (play/pause ⇒ on, stop ⇒ off) is a FALLBACK only.
When LMS reports an explicit power state (a "power"/"prefset power"
notification or the status "power" field), that state wins: playback may
only RAISE power (playing ⇒ on); a stop lowers power ONLY for players
that have never reported an explicit power state. This prevents a player
that is powered on but idle (stopped) from being shown as off.


NO BROWSE / FAVORITES / QUEUE MODEL

Home Assistant exposes browse trees, favorites, playlist manipulation,
and raw command interfaces.
This project intentionally does NOT expose:

Browsing APIs
Favorites
Queue inspection or editing
Raw LMS command pass‑through

These are explicitly deferred or excluded.

NO PLAYER SYNC / GROUP MANAGEMENT

Home Assistant supports player sync groups and group state coordination.
This project intentionally does NOT support:

LMS player sync groups
Group playback coordination
Group volume logic

Player grouping is a non‑goal for this phase.

NO MONOLITHIC DEVICE MODEL

Home Assistant represents each player as a single "media_player" entity.
This project intentionally separates:

Routable audio source (Driver 2)
Rich now-playing UI extension (Driver 3)
Audio endpoint / volume control (Driver 4)

Do NOT collapse these into a single device abstraction.

C. GUIDING PRINCIPLE
If Home Assistant does something that conflicts with any rule in CLAUDE.md,
CLAUDE.md always wins.
Home Assistant is used here only as:

Architectural validation
Defensive design reference

It must not be treated as a template to copy features from.
