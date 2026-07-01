# Lyrion Media Server - Crestron Home Drivers

## Disclaimer
This software is provided as-is and is not an official Crestron product.  This project is an independent, open-source driver for use with Crestron systems.
Crestron® and Crestron Home are trademarks or registered trademarks of Crestron Electronics, Inc.  This project is not affiliated with, endorsed by, or sponsored by Crestron Electronics, Inc.
Lyrion Media Server is an open-source project and is also not affiliated with this repository.

## Description
Four-driver suite (Crestron Drivers SDK V2 / Entity Model + RAD, .NET Framework 4.7.2) that integrates [Lyrion Media Server](https://lyrion.org/) (formerly Logitech Media Server / Squeezebox Server) with Crestron Home. The four drivers split responsibilities for a clean, room-based control surface that fits Crestron Home's source-routing graph.

| Driver | Role | Instances | Connects to LMS? |
|---|---|---|---|
| **Gateway_Lyrion_LMS_IP** (Lyrion Server) | Owns the single CLI + JSON-RPC connection to LMS. Publishes a shared service consumed by the other three drivers. | 1 per home | Yes |
| **Source_Lyrion_Player** (Lyrion Source) | Per-room routable audio source (RAD Bluray Player). Surfaces Play/Pause/Stop/Next/Prev/Power; declares analog + digital audio outputs. | 1 per player | No |
| **Helper_Lyrion_Player** (Lyrion Helper) | Per-room rich UI extension (RAD Media Player extension). Surfaces now-playing metadata, transport, shuffle, repeat, seek, power. | 1 per player | No |
| **Receiver_Lyrion_Player** (Lyrion Receiver) | Per-room routable AV receiver (RAD AV Receiver). Surfaces volume (0-100), mute, power; declares analog + digital audio inputs and speaker outputs. Optional. | 1 per player | No |

All drivers require Crestron driver runtime **25.0000.0033** or later.

## Download and install

Pre-built driver packages are attached to each [GitHub Release](https://github.com/jopaul14/Lyrion4Crestron/releases/latest). Download the `.pkg` files from the latest release:

| Package | Install | Required? |
|---|---|---|
| `Gateway_Lyrion_LMS_IP.pkg` | Once per home | Required |
| `Source_Lyrion_Player.pkg` | Once per player (room) | Required |
| `Helper_Lyrion_Player.pkg` | Once per player (room) | Required |
| `Receiver_Lyrion_Player.pkg` | Once per player (room) | Optional (omit if using an external amp/AVR) |

Quick install — see [BUILD.md](BUILD.md) for the full walk-through:

1. Copy the `.pkg` files to `Internal Flash/user/ThirdPartyDrivers/Import` on the control system using Crestron Toolbox.
2. In the Crestron Home Setup app, add the **Gateway first** (one per home): set the LMS hostname/IP, HTTP port (default 9000), CLI port (default 9090), and optional username/password.
3. For each room/player, add **Source** and **Helper** (and optionally **Receiver**) using the **same player MAC address** on all three.
4. Route the Source's digital or analog output to the Receiver input (or a 3rd-party AVR), then on to the room speakers.
5. In **Source Routes → Available Sources**, deselect "Lyrion Source" for the room to hide its tile while keeping the Helper's rich now-playing UI.

Prefer to build from source instead? See [BUILD.md](BUILD.md).

## Architecture

```
+--------------------------------------------------------------------------------+
|  Gateway_Lyrion_LMS_IP                                         (one per home)  |
|                                                                                |
|   +--------------+                                                             |
|   | LmsCliClient |----+      +------------------+                              |
|   +--------------+    |      | PlayerRegistry   |                              |
|   +--------------+    +----->| (PlayerRecord    |                              |
|   | LmsJsonRpc   |    |      |  per bound MAC)  |                              |
|   +--------------+----+      +------------------+                              |
|                                        |                                       |
|                         +--------------v-------------+                         |
|                         | ILyrionGatewayService (API)|                         |
|                         +----------------------------+                         |
+--------------------------------------------------------------------------------+
                                         |
                                         |
             +---------------------------+---------------------------+
             |                           |                           |
+------------v-----------+  +------------v-----------+  +------------v-----------+
| Source_Lyrion_Player   |  | Helper_Lyrion_Player   |  | Receiver_Lyrion_Player |
| (RAD: Bluray Player)   |  | (RAD ext: Media Plyr)  |  | (RAD: AV Receiver)     |
|                        |  |                        |  |                        |
| Play / Pause / Stop    |  | Title/Artist/Album     |  | Volume (0-100)         |
| Next / Prev            |  | Elapsed / Duration     |  | Mute                   |
| Power                  |  | Shuffle / Repeat       |  | Power                  |
|                        |  | Seek,  Power           |  |                        |
| Digital + analog       |  |                        |  | Digital + analog       |
| audio out  --->        |  | (no audio routing)     |  | --->  audio in         |
|                        |  |                        |  |                        |
+------------+-----------+  +------------------------+  +------------+-----------+
             |                                                       |
             +--------------  routed by Crestron Home  --------------+
```

Inter-driver communication uses a process-wide service registry (`LyrionGatewayServiceRegistry`). The Gateway registers an `ILyrionGatewayService` on startup; the Source, Helper, and Receiver drivers wait for it via `Subscribe(...)`. Commands flow from Source/Helper/Receiver to the Gateway; events flow back the other way. **Only the Gateway opens sockets to LMS.**

## Configuration

| Driver | Configuration |
|---|---|
| Gateway | LMS hostname/IP, HTTP port (default 9000), CLI port (default 9090), optional username/password |
| Source  | Player MAC address |
| Helper  | Player MAC address (matches the Source for the same room) |
| Receiver | Player MAC address (matches the Source for the same room), volume step size (default 2) |

## Features

### Lyrion Source (Driver 2) — routable audio source

- Play / Pause / Stop
- Next ('ForwardSkip') / Previous ('ReverseSkip')
- PowerOn / PowerOff / TogglePower
- Declares one digital audio output (Coaxial Digital) and one analog audio output (RCA Analog) in the Crestron Home routing graph.

### Lyrion Helper (Driver 3) — rich UI extension

- Source-name header (LMS player name) at the top of the now-playing screen
- Now-playing metadata: title, artist, album, track number, elapsed, duration
- Read-only progress bar with hh:mm:ss elapsed/total (hidden when duration is unknown, e.g. radio streams)
- Transport: Play / Pause / Stop / Next / Previous / Seek
- Shuffle (bool) and Repeat (bool), shown as state-driven button icons
- PowerOn / PowerOff / TogglePower
- Custom now-playing layout via UiDefinition.xml.
- Room-page tile shows the player's on/off state (power badge + 'Off'/now-playing status text), so the room still indicates whether the player is on even when the Source tile is hidden from Available Sources.

### Lyrion Receiver (Driver 4) — routable audio endpoint

- Volume (0-100, absolute + step up/down)
- Mute / unmute
- PowerOn / PowerOff / TogglePower
- Declares one digital audio input (Coaxial Digital) and one analog audio input (RCA Analog) plus speaker outputs.

### What's intentionally NOT exposed

Per CLAUDE.md "EXPLICITLY REMOVED FEATURES":

- No sleep timer
- No browse / favorites / queue APIs
- No raw LMS command pass-through
- No volume control on the Source or Helper drivers
- No player sync/group management

## Behavioral guarantees

- **Reconnect is a hard state boundary.** When the Gateway reconnects to LMS it re-queries every bound MAC, recomputes availability/power/playback/volume/mute/shuffle/repeat, then republishes everything.
- **Metadata freezes immediately when a player becomes unavailable.** If it stays unavailable for 30 seconds, metadata is cleared.
- **Logging is flash-safe.** The Gateway logs state transitions only, with a 5-second minimum stable time and oscillation suppression. Source, Helper, and Receiver each log a single 'Bound to MAC ...' line.
- **Backoff is bounded.** CLI reconnect schedule: 2s → 5s → 10s → 30s → 60s (cap).
- **Capability-driven fallbacks.** Players that don't accept power-off receive 'stop' instead, without warnings.

## Project layout

```
Lyrion4Crestron/
  LICENSE
  README.md
  BUILD.md
  CLAUDE.md
  Lyrion4Crestron.sln
  Common/Service/                       (shared service contract)
    ILyrionGatewayService.cs
    LyrionGatewayServiceRegistry.cs
    LyrionMetadata.cs / LyrionPlayerSnapshot.cs / LyrionPlaybackState.cs / LyrionPreset.cs
    MacAddress.cs
  Gateway_Lyrion_LMS_IP/                (Driver 1)
    Gateway_Lyrion_LMS_IP.csproj
    Driver.json
    EntryPoint.cs / GatewayDriver.cs
    Lifecycle/ServerConnectivityFsm.cs
    Protocol/ (LmsCliCommands, LmsCliParser, LmsTokenCodec, LmsJsonRpcRequests)
    Transport/ (LmsCliClient, LmsJsonRpcClient)
    Registry/ (PlayerRecord, PlayerRegistry, PlayerLifecycleState)
    Services/LyrionGatewayServiceImpl.cs
  Source_Lyrion_Player/                 (Driver 2)
    Source_Lyrion_Player.csproj
    Driver.json
    SourceDriver.cs / SourceProtocol.cs / SourceTransport.cs
  Helper_Lyrion_Player/                 (Driver 3)
    Helper_Lyrion_Player.csproj
    Driver.json
    HelperDriver.cs / HelperProtocol.cs / HelperTransport.cs
    IncludeInPkg/UiDefinitions/UiDefinition.xml
  Receiver_Lyrion_Player/               (Driver 4)
    Receiver_Lyrion_Player.csproj
    Driver.json
    ReceiverDriver.cs / ReceiverProtocol.cs / ReceiverTransport.cs
```

## Building and deploying

See [BUILD.md](BUILD.md) for prerequisites, build instructions, SDK path overrides, and deployment steps.

## License

MIT — see [LICENSE](LICENSE).
