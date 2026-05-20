# Building the Lyrion Crestron Drivers

The repository ships three Crestron Certified Drivers that build into three `.pkg` files:

| Driver | Output |
|---|---|
| `Gateway_Lyrion_LMS_IP` | `Gateway_Lyrion_LMS_IP.pkg` |
| `Media_Lyrion_Player`   | `Media_Lyrion_Player.pkg`   |
| `Volume_Lyrion_Player`  | `Volume_Lyrion_Player.pkg`  |

Each `.pkg` is deployed to a Crestron control system independently. The Gateway must be installed once per home; the Media and Volume drivers are installed once per room/player.

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
Media_Lyrion_Player\bin\Release\net472\Media_Lyrion_Player.pkg
Volume_Lyrion_Player\bin\Release\net472\Volume_Lyrion_Player.pkg

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
   - Media_Lyrion_Player.pkg
   - Volume_Lyrion_Player.pkg (if using Lyrion to control volume/mute)

2. **Reboot the Crestron Home processor** - while it may not always be necessary, it sometimes helps.

3. **Gateway first.** Using the Crestron Home Setup app, Configure Pro, etc., add the Gateway - it's recommended to add this to an equipment or similar room.  Only one is needed per Crestron processor:
   - Server hostname or IP
   - HTTP port (default 9000)
   - CLI port (default 9090)
   - Optional username/password

4. **Then Source / Receiver drivers.** For each player you want to control, deploy the files listed below :
   - `Media_Lyrion_Player.pkg` (configure with the player MAC), and/or
   - `Volume_Lyrion_Player.pkg` (configure with the player MAC and volume step size).

   If you are using an external amp / AVR for the room you do not need the Receiver driver — just install the Source driver.

5. **Route audio.** In Crestron Home, route the Source's analog output to the Receiver's analog input, and from there to the room speakers.  If using a 3rd-party receiver, you can route the analog or digital output to the appropriate input on the 3rd-party receiver.

## 5. Troubleshooting

- **`Crestron SDK not found at '...'`** — The SDK path the build resolved to does not contain the SDK libraries. Install the SDK or pass `/p:CrestronSdkPath=...` (see section 3).
- **`ManifestUtil.exe not found ...` warning** — `.dll` built, but no `.pkg` was produced. Install the full Crestron SDK (ManifestUtil is bundled).
- **`Null Exception` from ManifestUtil for each SDK DLL** — Harmless (see section 2.3).
- **`Microsoft.Office.Interop.Word` load failure** — Harmless. The `.pkg` is produced before this point; only the optional `.dat` doc file is skipped.
- **Driver loads but Source/Receiver shows "unavailable"** — The Gateway is not connected to LMS, or the configured MAC does not match a player. Check the Gateway's `lyrion:connectionState` diagnostic property; the LMS web UI shows each player's MAC under Settings → Information.
- **Source/Receiver does nothing** — The Gateway must be deployed and successfully connected to LMS before commands take effect. Commands issued while the Gateway is `DISCONNECTED` are dropped silently per design (CLAUDE.md "Commands dropped when server is not connected").
