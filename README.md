# Lyrion Media Server - Crestron Home Drivers

## Disclaimer
This software is provided as-is and is not an official Crestron product.  This project is an independent, open-source driver for use with Crestron systems.
Crestron® and Crestron Home are trademarks or registered trademarks of Crestron Electronics, Inc.  This project is not affiliated with, endorsed by, or sponsored by Crestron Electronics, Inc.
Lyrion Media Server is an open-source project and is also not affiliated with this repository.

## Description
Four-driver suite (Crestron Drivers SDK V2 / Entity Model + RAD, .NET Framework 4.7.2) that integrates [Lyrion Media Server](https://lyrion.org/) (formerly Logitech Media Server / Squeezebox Server) with Crestron Home. The four drivers split responsibilities for a clean, room-based control surface that fits Crestron Home's source-routing graph.

| Driver | Role | Instances | Connects to LMS? |
|---|---|---|---|
| **Server_Lyrion_LMS_IP** (Lyrion Server) | Owns the single CLI + JSON-RPC connection to LMS. Publishes a shared service consumed by the other three drivers. | 1 per home | Yes |
| **Source_Lyrion_Player** (Lyrion Source) | Per-room routable audio source (RAD Bluray Player). Surfaces Play/Pause/Stop/Next/Prev/Power; declares analog + digital audio outputs. | 1 per player | No |
| **Helper_Lyrion_Player** (Lyrion Helper) | Per-room rich UI extension (RAD Media Player extension). Surfaces now-playing metadata, transport, shuffle, repeat, power. | 1 per player | No |
| **Receiver_Lyrion_Player** (Lyrion Receiver) | Per-room routable AV receiver (RAD AV Receiver). Surfaces volume (0-100), mute, power; declares analog + digital audio inputs and speaker outputs. Optional. | 1 per player | No |

All drivers require Crestron driver runtime **25.0000.0033** or later.

## Download and install

Pre-built driver packages are attached to each [GitHub Release](https://github.com/jopaul14/Lyrion4Crestron/releases/latest). Download the `.pkg` files from the latest release:

| Package | Install | Required? |
|---|---|---|
| `Server_Lyrion_LMS_IP.pkg` | Once per home | Required |
| `Source_Lyrion_Player.pkg` | Once per player (room) | Required |
| `Helper_Lyrion_Player.pkg` | Once per player (room) | Required |
| `Receiver_Lyrion_Player.pkg` | Once per player (room) | Optional (omit if using an external amp/AVR) |

Quick install — see [BUILD.md](BUILD.md) for the full walk-through:

1. Copy the `.pkg` files to `Internal Flash/user/ThirdPartyDrivers/Import` on the control system using Crestron Toolbox.
2. In the Crestron Home Setup app, add the **Lyrion Server first** (one per home): set the LMS hostname/IP, HTTP port (default 9000), CLI port (default 9090), and optional username/password.
3. For each room/player, add **Source** and **Helper** (and optionally **Receiver**) using the **same player MAC address** on all three.
4. Route the Source's digital or analog output to the Receiver input (or a 3rd-party AVR), then on to the room speakers. Then set the room's **Default Source** (Source Routes → Available Sources) to the Lyrion Source, and on **Preferred Routing** set the Source to the Lyrion Source and the Audio Endpoint to the Lyrion Receiver (or your AVR). Without a default route, `Room On` silently does nothing. See [BUILD.md](BUILD.md) step 6.
5. Optionally, in **Source Routes → Available Sources**, deselect "Lyrion Source" for the room to hide its tile. Most setups should leave it visible — the room's media on/off indication in the Crestron Home app comes from that tile. See [BUILD.md](BUILD.md) step 7.

Prefer to build from source instead? See [BUILD.md](BUILD.md).

## Architecture

```
+--------------------------------------------------------------------------------+
|  Server_Lyrion_LMS_IP                                         (one per home)  |
|                                                                                |
|   +--------------+                                                             |
|   | LmsCliClient |----+      +------------------+                              |
|   +--------------+    |      | PlayerRegistry   |                              |
|   +--------------+    +----->| (PlayerRecord    |                              |
|   | LmsJsonRpc   |    |      |  per bound MAC)  |                              |
|   +--------------+----+      +------------------+                              |
|                                        |                                       |
|                         +--------------v-------------+                         |
|                         | ILyrionServerService (API)|                         |
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
|                        |  | Power                  |  |                        |
| Digital + analog       |  |                        |  | Digital + analog       |
| audio out  --->        |  | (no audio routing)     |  | --->  audio in         |
|                        |  |                        |  |                        |
+------------+-----------+  +------------------------+  +------------+-----------+
             |                                                       |
             +--------------  routed by Crestron Home  --------------+
```

Inter-driver communication uses a process-wide service registry (`LyrionServerServiceRegistry`). The Lyrion Server registers an `ILyrionServerService` on startup; the Source, Helper, and Receiver drivers wait for it via `Subscribe(...)`. Commands flow from Source/Helper/Receiver to the Lyrion Server; events flow back the other way. **Only the Lyrion Server opens sockets to LMS.**

## Configuration

| Driver | Configuration |
|---|---|
| Lyrion Server | LMS hostname/IP, HTTP port (default 9000), CLI port (default 9090), optional username/password |
| Source  | Player MAC address |
| Helper  | Player MAC address (matches the Source for the same room), plus up to 4 optional presets |
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
- Elapsed/total time on the track card (`00:29 / 02:54`, or elapsed alone for radio streams with no duration). There is no progress bar: Crestron Home has no draggable seek bar, so it could only ever be a read-only line, and it cost a full-height card to draw one.
- Transport: Play / Pause / Stop / Next / Previous
- Shuffle (bool) and Repeat (bool), shown as state-driven button icons
- PowerOn / PowerOff / TogglePower
- Volume Up / Down and Mute, stepping by the Receiver's configured amount. There is no volume level bar — it cost a full-height card to draw one thin, unadjustable line.
- Up to 4 configurable presets — see [Presets](#presets) below
- Custom now-playing layout via UiDefinition.xml. Crestron Home interprets that file on the processor and nothing validates it at build time, so the rendering constraints it depends on (a five-button ceiling per row, and how icons and labels compete for width) are documented at the top of it.
- Room-page tile reports the player's own on/off state (power badge + 'Off'/now-playing status text). Note this is the *player's* state, not the room's media on/off indication — that comes from the Source tile.

### Lyrion Receiver (Driver 4) — routable audio endpoint

- Volume (0-100, absolute + step up/down)
- Mute / unmute
- PowerOn / PowerOff / TogglePower
- Declares one digital audio input (Coaxial Digital) and one analog audio input (RCA Analog) plus speaker outputs.

### Presets

> **A note on wording.** Crestron and Lyrion use different words for the same
> idea. Crestron's vocabulary for a named, recallable shortcut on a device is a
> **preset** — the same word it uses for tuner presets, pool presets, and camera
> presets. In Lyrion Media Server the things you are recalling are **playlists**
> and **favourites**. This driver follows Crestron's word, because that is what
> installers will look for in the Crestron Home setup app: a *preset* here
> starts a Lyrion *playlist* or *favourite*.

Each Helper can carry up to four presets. Configure each one in the Crestron
Home setup app as a single field:

```
Name|Icon|Command
```

For example:

```
KCRW|icBroadcastRegular|favorites playlist play item_id:2
```

- **Name** — the button label.
- **Icon** — a Crestron icon name. Leave it empty for the default
  (`icBroadcastRegular`); `Name|Command` on its own works too.
- **Command** — the LMS CLI text that follows the player MAC. The driver adds
  the MAC and the line feed, so don't include either.

Configured presets appear as buttons under a **Presets** heading on the
now-playing page. Empty or malformed slots are hidden, so a room that uses no
presets looks exactly as it did before.

The same four presets also appear in Crestron Home's event/scene editor as
**Play Preset 1** … **Play Preset 4**, so one button press can power a player
on, set its volume, and start a preset.

Presets are declared, not discovered: the drivers never scan the server for
playlists. An LMS library can hold hundreds, and naming the handful that belong
in a room is both simpler and faster than browsing them all.

#### Finding the item_id for a favourite

`favorites playlist play item_id:2` needs a number, and nothing in the Lyrion
web UI or Material Skin shows it. Ask the server directly — connect to the LMS
CLI port (9090) with any terminal and run:

```
favorites items 0 50
```

The reply lists your favourites in order. **`item_id` is the position in that
list, counting from 0.** For a server whose favourites are KCSN, KQED, KCRW,
KEXP, `item_id:2` is KCRW — the third entry.

For a favourite that contains multiple streams (`hasitems:1` in the reply), you
can browse into it with `favorites items 0 50 item_id:2` and address a specific
stream with a dotted index such as `item_id:2.1`.

Two things to watch out for:

- **Ignore the `id:` field in the reply.** It looks like a stable identifier but
  its leading hash is regenerated on every query — `id:39ccbad3.2` one moment,
  `id:d4773a61.2` the next. Use the position, not that value.
- **Reordering your favourites renumbers them.** A preset points at a position,
  so inserting or removing a favourite above it silently repoints the button at
  a different station. Re-check your presets after reorganising favourites.

Saved playlists are listed with `playlists 0 50` instead, which returns entries
like `id:414211 playlist:The Current`. Those ids are stable, so a preset built
on one does not drift the way a favourite's position can.

Whatever command you settle on, the quickest way to confirm it is to run it once
over the CLI with your player's MAC in front of it — exactly what the driver
will send:

```
aa:bb:cc:dd:ee:ff favorites playlist play item_id:2
```

If that starts the right thing, dropping the MAC and putting the rest in a
preset field will too.

### What's intentionally NOT exposed

Per [docs/PRD.md](docs/PRD.md) "Out of Scope":

- No sleep timer
- No browse / favorites / queue APIs — presets cover the common case by letting
  the installer name specific entries up front
- No arbitrary LMS command pass-through at runtime — only the fixed command
  strings an installer configures as presets, with control characters stripped
- No volume control on the Source driver
- No player sync/group management

## Behavioral guarantees

- **Reconnect is a hard state boundary.** When the Lyrion Server reconnects to LMS it re-queries every bound MAC, recomputes availability/power/playback/volume/mute/shuffle/repeat, then republishes everything.
- **Metadata freezes immediately when a player becomes unavailable.** If it stays unavailable for 30 seconds, metadata is cleared.
- **Logging is flash-safe.** The Lyrion Server logs state transitions only, with a 5-second minimum stable time and oscillation suppression. Source, Helper, and Receiver each log a single 'Bound to MAC ...' line.
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
    ILyrionServerService.cs
    LyrionServerServiceRegistry.cs
    LyrionMetadata.cs / LyrionPlayerSnapshot.cs / LyrionPlaybackState.cs / LyrionPresetConfig.cs
    MacAddress.cs
  Server_Lyrion_LMS_IP/                (Driver 1)
    Server_Lyrion_LMS_IP.csproj
    Driver.json
    EntryPoint.cs / ServerDriver.cs
    Lifecycle/ServerConnectivityFsm.cs
    Protocol/ (LmsCliCommands, LmsCliParser, LmsTokenCodec, LmsJsonRpcRequests)
    Transport/ (LmsCliClient, LmsJsonRpcClient)
    Registry/ (PlayerRecord, PlayerRegistry, PlayerLifecycleState)
    Services/LyrionServerServiceImpl.cs
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
