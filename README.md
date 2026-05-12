# Lyrion Media Server - Crestron Certified Drivers

Three-driver suite (Crestron Certified Drivers SDK V2 / Entity Model, .NET Framework 4.7.2) that integrates [Lyrion Media Server](https://lyrion.org/) (formerly Logitech Media Server / Squeezebox Server) with Crestron Home. The three drivers split responsibilities for a clean, room-based control surface.

| Driver | Role | Instances | Connects to LMS? |
|---|---|---|---|
| **Gateway_Lyrion_LMS_IP** (Lyrion Server) | Owns the single CLI + JSON-RPC connection to LMS. Publishes a shared service consumed by the other two drivers. | 1 per home | Yes |
| **Media_Lyrion_Player** (Lyrion Source) | Per-room Extension Media Player. Surfaces transport, now-playing, shuffle, repeat, power. | 1 per player | No |
| **Volume_Lyrion_Player** (Lyrion Receiver) | Per-room AV receiver-style endpoint. Surfaces volume (0-100), mute, power. Optional. | 1 per player | No |

All drivers require Crestron driver runtime **25.0000.0033** or later.

## Architecture

```
                            (one per home)
        +-------------------------------------------------+
        |              Gateway_Lyrion_LMS_IP              |
        |                                                 |
        |  +------------+      +---------------------+   |
        |  | LmsCliClient|----->| PlayerRegistry      |   |
        |  +------------+      | (one PlayerRecord   |   |
        |  | LmsJsonRpc  |----->|  per bound MAC)    |   |
        |  +------------+      +----------+----------+   |
        |                                  |               |
        |              +-------------------v-----------+   |
        |              | ILyrionGatewayService (publ.) |   |
        |              +-------------------+-----------+   |
        +----------------------------------|---------------+
                                           |
                +--------------------------+--------------------------+
                |                                                     |
   (one per player)                                       (one per player)
+--------------v---------------+                  +-------------------v-----+
|   Media_Lyrion_Player        |                  | Volume_Lyrion_Player    |
|   (Extension Media Player)   |                  | (AV Receiver endpoint)  |
|                              |                  |                         |
|  Transport / NowPlaying      |                  |  Volume (0-100)         |
|  Shuffle / Repeat (bool)     |                  |  Mute                   |
|  Power                       |                  |  Power                  |
|                              |                  |                         |
|  Digital + Analog audio out  | -- routed to --> |  Analog audio in        |
+------------------------------+                  +-------------------------+
```

Inter-driver communication uses a process-wide service registry (`LyrionGatewayServiceRegistry`). The Gateway registers an `ILyrionGatewayService` on startup; the Media and Volume drivers wait for it via `Subscribe(...)`. Commands flow from Media/Volume to the Gateway; events flow back the other way. **Only the Gateway opens sockets to LMS.**

## Configuration

| Driver | Configuration |
|---|---|
| Gateway | LMS hostname/IP, HTTP port (default 9000), CLI port (default 9090), optional username/password |
| Source  | Player MAC address |
| Receiver | Player MAC address, volume step size (default 2) |

## Features

### Lyrion Source (Driver 2) exposes

- `transport:play`, `transport:pause`, `transport:stop`, `transport:nextTrack`, `transport:previousTrack`, `transport:seek`
- `transport:playbackState` (Playing / Paused / Stopped)
- `power:on`, `power:off`, `power:toggle`, `power:on` (property)
- `lyrion:setShuffle(bool)`, `lyrion:shuffleEnabled`
- `lyrion:setRepeat(bool)`, `lyrion:repeatEnabled`
- `media:title`, `media:artist`, `media:album`, `media:artworkUrl`, `media:durationSec`, `media:elapsedSec`
- `lyrion:available`

### Lyrion Receiver (Driver 3) exposes

- `audio:setVolume(0-100)`, `audio:volumeUp`, `audio:volumeDown`, `audio:volume`
- `audio:setMute(bool)`, `audio:toggleMute`, `audio:muted`
- `power:on`, `power:off`, `power:toggle`, `power:on` (property)
- `lyrion:available`

### What's intentionally NOT exposed

Per CLAUDE.md "EXPLICITLY REMOVED FEATURES":

- No sleep timer
- No browse / favorites / queue APIs
- No raw LMS command pass-through
- No volume control on the Source driver
- No player sync/group management

## Behavioral guarantees

- **Reconnect is a hard state boundary.** When the Gateway reconnects to LMS it re-queries every bound MAC, recomputes availability/power/playback/volume/mute/shuffle/repeat, then republishes everything.
- **Metadata freezes immediately when a player becomes unavailable.** If it stays unavailable for 30 seconds, metadata is cleared.
- **Logging is flash-safe.** The Gateway logs state transitions only, with a 5-second minimum stable time and oscillation suppression. Source and Receiver each log a single `Bound to MAC ...` line.
- **Backoff is bounded.** CLI reconnect schedule: 2s → 5s → 10s → 30s → 60s (cap).
- **Capability-driven fallbacks.** Players that don't accept power-off receive `stop` instead, without warnings.

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
  Media_Lyrion_Player/                  (Driver 2)
    Media_Lyrion_Player.csproj
    Driver.json
    EntryPoint.cs / MediaDriver.cs
  Volume_Lyrion_Player/                 (Driver 3)
    Volume_Lyrion_Player.csproj
    Driver.json
    EntryPoint.cs / VolumeDriver.cs
```

## Building and deploying

See [BUILD.md](BUILD.md) for prerequisites, build instructions, SDK path overrides, and deployment steps.

## License

MIT — see [LICENSE](LICENSE).
