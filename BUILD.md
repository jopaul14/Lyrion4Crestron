# Building the Lyrion Crestron Drivers

The repository ships four Crestron Certified Drivers that build into four `.pkg` files:

| Driver | Output |
|---|---|
| `Gateway_Lyrion_LMS_IP`   | `Gateway_Lyrion_LMS_IP.pkg`   |
| `Source_Lyrion_Player`    | `Source_Lyrion_Player.pkg`    |
| `Helper_Lyrion_Player`    | `Helper_Lyrion_Player.pkg`    |
| `Receiver_Lyrion_Player`  | `Receiver_Lyrion_Player.pkg`  |

Each `.pkg` is deployed to a Crestron control system independently. The Gateway must be installed once per home; the Source, Helper, and Receiver drivers are installed once per room/player.

## 1. One-time prerequisites

### 1.1 Visual Studio

Install **Visual Studio 2019** or **Visual Studio 2022**. Select the **.NET desktop development** workload with these individual components:

- **.NET Framework 4.7.2 targeting pack**
- **.NET Framework 4.7.2 SDK**
- **MSBuild**

### 1.2 Crestron Certified Drivers SDK

Install the **Crestron Certified Drivers SDK** (version **27.0000.0024** or later). The projects default to:

```
C:\Lyrion4Crestron\Crestron_SDK
```

Override via the `CrestronSdkPath` environment variable or per-build with `/p:CrestronSdkPath=...`.

The build will fail up-front with a clear error if the SDK is not where it is expected. The files that must exist:

- `<sdk>\Libraries\Crestron.DeviceDrivers.SDK.dll`
- `<sdk>\Libraries\Crestron.DeviceDrivers.EntityModel.dll`
- `<sdk>\Libraries\Crestron.DeviceDrivers.API.dll`
- `<sdk>\Libraries\Crestron.DeviceDrivers.Core.dll`
- `<sdk>\ManifestUtil\ManifestUtil.exe`

If `ManifestUtil.exe` is missing, the `.dll` will still build but no `.pkg` is produced; you get a warning instead of an error.

## 2. Building

### 2.1 Visual Studio

1. Microsoft Word must be installed on the computer where you're building, to generate the required DAT file inside the .pkg files.
2. Open `Lyrion4Crestron.sln`.
3. Select the **Release** configuration.
4. Build > **Build Solution** (Ctrl+Shift+B).

Output (per project):

```
<project>\bin\Release\net472\<project>.dll
<project>\bin\Release\net472\<project>.pkg
```

### 2.2 Command line

From a **Developer Command Prompt for Visual Studio**:

```bat
cd C:\Lyrion4Crestron\Lyrion4CrestronRepo
msbuild Lyrion4Crestron.sln /p:Configuration=Release /restore
```

To build just one driver:

```bat
msbuild Gateway_Lyrion_LMS_IP\Gateway_Lyrion_LMS_IP.csproj /p:Configuration=Release /restore
```

### 2.3 A note on the ManifestUtil output

You will see ManifestUtil print `Null Exception: String reference not set to an instance of a String.` once for each Crestron SDK reference DLL — it scans every DLL in the output folder. These messages are harmless.

You may also see `System.IO.FileLoadException` for `Microsoft.Office.Interop.Word` followed by `Error creating DAT file`. This is also harmless: ManifestUtil writes the `.pkg` first, then tries to generate a companion `.dat` file via Word COM interop. With no Word installed the `.dat` is skipped. The `.dat` is not required for the driver to run on a Crestron controller; the build target ignores ManifestUtil's exit code and verifies that the `.pkg` was actually produced.

### 2.4 .pkg location

After building, the .pkg files will be located under the following locations (dependent on your higher-level directory structure)
Gateway_Lyrion_LMS_IP\bin\Release\net472\Gateway_Lyrion_LMS_IP.pkg
Source_Lyrion_Player\bin\Release\net472\Source_Lyrion_Player.pkg
Helper_Lyrion_Player\bin\Release\net472\Helper_Lyrion_Player.pkg
Receiver_Lyrion_Player\bin\Release\net472\Receiver_Lyrion_Player.pkg

## 3. Overriding the SDK path

### Option A: environment variable

```bat
setx CrestronSdkPath "D:\Crestron\Crestron_SDK"
```

Open a new shell or restart Visual Studio for the change to take effect.

### Option B: per-build

```bat
msbuild Lyrion4Crestron.sln /p:Configuration=Release /p:CrestronSdkPath=D:\Crestron\Crestron_SDK
```

## 4. Deploying to a Crestron control system

Deploy in this order:

1. **Copy .pkg files** to `Internal Flash/user/ThirdPartyDrivers/Import` on the control system using Crestron Toolbox
   - Gateway_Lyrion_LMS_IP.pkg
   - Source_Lyrion_Player.pkg
   - Helper_Lyrion_Player.pkg
   - Receiver_Lyrion_Player.pkg (if using Lyrion to control volume/mute)

2. **Reboot the Crestron Home processor** - while it may not always be necessary, it sometimes helps.

3. **Gateway first.** Using the Crestron Home Setup app, Configure Pro, etc., add the Gateway - it's recommended to add this to an equipment or similar room.  Only one is needed per Crestron processor:
   - Server hostname or IP
   - HTTP port (default 9000)
   - CLI port (default 9090)
   - Optional username/password

4. **Then Source / Helper / Receiver drivers.** For each player you want to control, deploy the files listed below — use the same player MAC address on all three:
   - `Source_Lyrion_Player.pkg` (configure with the player MAC) — required for source routing
   - `Helper_Lyrion_Player.pkg` (configure with the same player MAC, plus up to four optional presets — see step 5) — required for the rich now-playing UI, shuffle, repeat, seek
   - `Receiver_Lyrion_Player.pkg` (configure with the same player MAC and a volume step size) — optional

   If you are using an external amp / AVR for the room you do not need the Receiver driver — just install the Source and Helper.

5. **Optional: configure presets on the Helper.** A *preset* is Crestron's word for a named, recallable shortcut — the same word it uses for tuner and camera presets. Here, each one starts a Lyrion **playlist** or **favourite**. Each Helper has four optional preset fields (`Preset 1` … `Preset 4`) sitting alongside the MAC address; leaving them all empty is fine and changes nothing about the room.

   Each field takes one pipe-delimited value:

   ```
   Name|Icon|Command
   ```

   For example, in the `Preset 1` field:

   ```
   KCRW|icBroadcastRegular|favorites playlist play item_id:2
   ```

   - **Name** — the button label shown to end-users (`KCRW`).
   - **Icon** — a Crestron icon name (`icBroadcastRegular`). Leave it empty for the default; the shorter `Name|Command` form works too.
   - **Command** — the LMS CLI text that follows the player MAC. **Do not include the MAC address or a line feed** — the driver adds both.

   To find the `item_id` for a favourite, connect to your LMS server's CLI port (9090) with any terminal and run `favorites items 0 50`. The reply lists favourites in order, and `item_id` is the position counting from **0** — so for favourites KCSN, KQED, KCRW, KEXP, `item_id:2` is KCRW. Ignore the `id:` field in that reply: its leading hash is regenerated on every query and is not a stable identifier. Saved playlists are listed with `playlists 0 50` instead, and their numeric ids *are* stable.

   To verify a command before configuring it, send it over the CLI with the player's MAC in front — exactly what the driver will send:

   ```
   aa:bb:cc:dd:ee:ff favorites playlist play item_id:2
   ```

   Once configured, presets show up in two places:

   - As buttons under a **Presets** heading on the Helper's now-playing page. Empty or malformed fields are hidden rather than shown as dead buttons, so a typo costs you a missing button, not a broken one.
   - As operations named **Play Preset 1** … **Play Preset 4** in Crestron Home's event / scene / button-press editor. Combine them with the existing power and volume operations to build, for example, a single keypad button that powers the player on, sets volume to 30, and starts Preset 1.

   Note that the sequence editor shows the fixed names *Play Preset 1…4*, not your labels (`KCRW`) — a driver's programming surface is fixed when the package is built, so it cannot carry per-room configuration. Keep preset ordering consistent across rooms if you plan to use them in shared scenes.

   Because a preset stores a position rather than a name, **reordering your Lyrion favourites renumbers them** and will silently repoint a button at a different station. Re-check presets after reorganising favourites.

6. **Route audio.** In Crestron Home, route the Source's digital or analog output to the Receiver's matching input (or to a 3rd-party AVR), and from there to the room speakers.

7. **Hide the Source tile from the room UI.** The Lyrion Source is a routable Blu-ray Player, so by default Crestron Home shows it as a selectable source in the room. It carries full transport and power controls (Play/Pause/Stop/Next/Previous/Power) — these are intentionally retained so they are available to Crestron Home programming (Quick Actions, scenes, schedules, etc.), but you do not want end-users seeing a second control surface alongside the Helper's rich now-playing UI. To keep the controls available for programming while hiding the tile from end-users:

   - In the Crestron Home Setup app, go to **Source Routes**, select the room, and open the **Available Sources** tab.
   - **Deselect (uncheck) "Lyrion Source"** for that room. This removes its tile from the room user interface; the source remains usable for audio routing (configured in step 4, which is independent of Available Sources) and its transport/power commands remain available to programming.
   - Leave the **Lyrion Helper** selected/visible — it is an extension device, not a source, and is unaffected by this setting. It remains the single rich control surface end-users see.
   - Repeat for every room that has a Lyrion Source.

   The Helper's room-page tile carries the player's on/off state (a power badge plus a status line that reads `Off` when powered down, or the now-playing track / playback state when on), so hiding the Source tile does not lose the at-a-glance on/off indication for the room.

   Note: a deselected source can still appear inside the routing/route-selection menus used during configuration, but it will not be a user-facing source tile in the room view.

8. **Decide whether the player's power should drive the room on/off.** By default it does not. Crestron Home treats a room's on/off state as a room-level concept, and a source driver reporting its own power does not move it — so out of the box, powering the Lyrion player on from Material Skin (or the player's own front panel) leaves the room showing off. If you want the room to follow the player, you have to say so explicitly.

   **Always map the Lyrion Source, not the Lyrion Receiver.** Both drivers bind to the same MAC and mirror the same power signal from the Gateway, so mapping both fires the room action twice for one event. The Source is the right one to use because it is the routable device that *is* the Lyrion player in the room, and because it is always installed — the Lyrion Receiver is optional, so mapping the Source keeps this step identical whether you use it or a third-party AVR.

   Then pick the behaviour that matches the room. **This choice depends on how many sources the room has, not on which receiver you use:**

   | Room | Power Is On | Power Is Off | Use when |
   |---|---|---|---|
   | **A. Lyrion-only** | Room On | Room Off | Lyrion is the room's only or dominant audio source — typically a Lyrion Receiver or a small amp driving that room and nothing else. |
   | **B. Shared / multi-source** | Nothing | Nothing | The room has a third-party AVR or TV where Lyrion is one input among several. |
   | **C. Wake-only** | Room On | Nothing | You want music started from a phone to wake the room, but a Lyrion power-off must never shut down a room that is doing something else. |

   Set this on the Lyrion Source in the Crestron Home Setup app: under the device's **Media Function** settings there is a **Power Is On** and a **Power Is Off** entry, each with a **Mode** you set to *Room On*, *Room Off*, or *Nothing*.

   **Room B is the important one to get right.** In a room with a shared AVR, the Lyrion player may power on or off for reasons that have nothing to do with what the room is doing — a whole-house scene, someone else's app, or the player simply stopping. If those events are mapped, selecting a different input can be interrupted by the room switching itself to Lyrion, or a Lyrion power-off can black out a room mid-movie. Leaving both on *Nothing* costs you nothing visible: the **Lyrion Helper's room-page tile still shows the player's on/off state** (power badge plus a status line reading `Off` or the current track), so the room still indicates whether the player is on — it just does not act on it.

   Room C's trade-off: if Lyrion is the active source and it powers off, the room stays on and silent until someone turns it off. That is usually preferable to the room going dark unexpectedly, but it is a real difference from Room A.

   > **Requires driver version 1.0.5 or later.** On 1.0.4 and earlier, LMS's own power-off sequence briefly reported the player as ON again about a millisecond after reporting it OFF. With Room A or C configured, Crestron Home acted on that spurious edge and turned the room straight back on — the room would appear to power off and then bounce back on with the music playing, one to two seconds later. If you see that symptom, you are running a cached older driver; confirm the version and re-import.

9. Restart the control system program (or hot-reload via Toolbox) to pick up the new drivers.

The Source, Helper, and Receiver drivers will each log a single `Bound to MAC ...` line and then surface state as it changes; the Gateway logs server connectivity transitions only.

## 5. Troubleshooting

- **`Crestron SDK not found at '...'`** — The SDK path the build resolved to does not contain the SDK libraries. Install the SDK or pass `/p:CrestronSdkPath=...` (see section 3).
- **`ManifestUtil.exe not found ...` warning** — `.dll` built, but no `.pkg` was produced. Install the full Crestron SDK (ManifestUtil is bundled).
- **`Null Exception` from ManifestUtil for each SDK DLL** — Harmless (see section 2.3).
- **`Microsoft.Office.Interop.Word` load failure** — Harmless. The `.pkg` is produced before this point; only the optional `.dat` doc file is skipped.
- **Driver loads but Source/Helper/Receiver shows "unavailable"** — The Gateway is not connected to LMS, or the configured MAC does not match a player. Check the Gateway's `lyrion:connectionState` diagnostic property; the LMS web UI shows each player's MAC under Settings → Information.
- **Source/Helper/Receiver does nothing** — The Gateway must be deployed and successfully connected to LMS before commands take effect. Commands issued while the Gateway is `DISCONNECTED` are dropped silently per design (CLAUDE.md "Commands dropped when server is not connected").
