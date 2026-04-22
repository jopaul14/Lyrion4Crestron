# Lyrion Media Server - Crestron Certified Driver

Crestron Certified Driver (SDK V2 / Entity Model) that integrates [Lyrion Media Server](https://lyrion.org/) (formerly Logitech Media Server / Squeezebox Server) with Crestron control systems. Multi-room audio control is exposed via a Platform driver that presents each configured Lyrion player as a Crestron `ManagedDevice`.

The driver targets `.NET Framework 4.7.2` and requires Crestron driver runtime **25.0000.0033** or later.

## What it supports

Driver controller (root entity — the LMS server itself) exposes:

- `lyrion:connectionState` — `Disconnected` / `Connecting` / `Connected` / `Faulted`
- `lyrion:serverVersion` — LMS server version string
- `platform:managedDevices` — dictionary of configured players (keyed by ControllerId)

Per-player `ManagedDevice` entities expose:

| Capability | Properties | Commands |
|---|---|---|
| Transport | `transport:playbackState` | `transport:play`, `transport:pause`, `transport:stop`, `transport:nextTrack`, `transport:previousTrack` |
| Volume / mute | `audio:volume`, `audio:muted` | `audio:setVolume`, `audio:volumeUp`, `audio:volumeDown`, `audio:setMute`, `audio:toggleMute` |
| Power | `power:on` | `power:setPower`, `power:sleep` (30 min) |
| Playlist modes | `lyrion:repeatMode`, `lyrion:shuffleMode` | `lyrion:setRepeatMode`, `lyrion:setShuffleMode` |
| Now Playing | `media:title`, `media:artist`, `media:album`, `media:artworkUrl`, `media:durationSec`, `media:elapsedSec`, `media:isRemote`, `media:stationName`, `media:playlistIndex`, `media:playlistLength` | — |
| Queue control | — | `media:playFavorite`, `media:playPlaylist` |
| Browsing | `media:lastBrowseResult` (raw JSON response) | `media:browse`, `media:browseFavorites`, `media:queryPlaylistTracks` |
| Online state | `lyrion:online` | — |

## Architecture at a glance

```
                    +---------------------------+
                    |  EntryPoint (assembly     |
                    |  DriverAssemblyEntryPoint)|
                    +-------------+-------------+
                                  |
                                  v
                      +-----------+-----------+
                      |      DriverMain       |  (root entity, ManagedDevices)
                      +-----------+-----------+
                       |          |           |
              +--------+   +------+-----+    +--+-----------------+
              |            |            |       |
              v            v            v       v
          LmsCliClient  LmsJsonRpcClient  PlayerEntity ...  PlayerEntity
             (9090)           (9000)       (one per configured player MAC)
```

- **`LmsCliClient`** — persistent async TCP client with exponential reconnect (2s→180s). Line-oriented; subscribes to unsolicited notifications (`listen 1`); parses each line via `LmsCliParser` and raises `MessageReceived` events.
- **`LmsJsonRpcClient`** — stateless HTTP client for JSON-RPC browse / favorites / playlist queries.
- **`PlayerEntity`** — one per configured MAC; receives only messages routed to its MAC; fires-and-forgets CLI and RPC commands via delegates injected by `DriverMain`.
- **`DriverMain`** — root `ReflectedAttributeDriverEntity`. Owns both transport clients, parses the `_Players_` config, instantiates `PlayerEntity` + `PlatformManagedDevice` pairs, and routes CLI notifications by MAC.

The `ManagedDevices` collection is treated as immutable per the Crestron V2 contract: every change copies the dictionary, mutates the copy, reassigns, and emits an appropriate `NotifyPropertyChanged` update.

## Project layout

```
Lyrion4CrestronRepo/
  LICENSE
  README.md
  BUILD.md
  Platform_Lyrion_LMS_IP/
    Platform_Lyrion_LMS_IP.csproj
    Driver.json
    EntryPoint.cs
    DriverMain.cs
    PlayerEntity.cs
    Definitions/
      DeviceUxCategory.cs
      PlatformManagedDevice.cs
      PlaybackState.cs
    Models/
      NowPlayingInfo.cs
      PlayerConfig.cs
      PlayerState.cs
    Properties/
      AssemblyInfo.cs
    Protocol/
      LmsCliCommands.cs
      LmsCliParser.cs
      LmsJsonRpcRequests.cs
      LmsTokenCodec.cs
    Transport/
      LmsCliClient.cs
      LmsJsonRpcClient.cs
```

## Building and deploying

See [BUILD.md](BUILD.md) for prerequisites, build instructions (Visual Studio or MSBuild command line), SDK path overrides, and deployment steps.

## License

MIT — see [LICENSE](LICENSE).
