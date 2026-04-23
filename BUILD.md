# Building the Lyrion Crestron Driver

This document walks through building `Platform_Lyrion_LMS_IP.pkg` from source and deploying it to a Crestron control system. It also lists the one-time prerequisites a developer needs on their Windows machine.

## 1. One-time prerequisites

Install the following on the machine you will use to build the driver. Everything must be installed before the first build will succeed.

### 1.1 Visual Studio

Install **Visual Studio 2019** or **Visual Studio 2022** (Community, Professional, or Enterprise all work).

During installation, select the **.NET desktop development** workload, and make sure the following individual components are checked:

- **.NET Framework 4.7.2 targeting pack**
- **.NET Framework 4.7.2 SDK**
- **MSBuild**

These are needed because the driver targets `.NET Framework 4.7.2` (the maximum version supported by the Crestron driver runtime on 3-Series and 4-Series controllers).

### 1.2 Crestron Certified Drivers SDK

Install the **Crestron Certified Drivers SDK** (version **27.0000.0024** or later) from Crestron. By default the project expects the SDK to be installed at:

```
C:\Lyrion4Crestron\Crestron_SDK
```

If you installed the SDK to a different location, see section 3 (Overriding the SDK path).

After installation, confirm that the following files exist — the build will fail up-front with a clear error if they are missing:

- `C:\Lyrion4Crestron\Crestron_SDK\Libraries\Crestron.DeviceDrivers.SDK.dll`
- `C:\Lyrion4Crestron\Crestron_SDK\Libraries\Crestron.DeviceDrivers.EntityModel.dll`
- `C:\Lyrion4Crestron\Crestron_SDK\Libraries\Crestron.DeviceDrivers.API.dll`
- `C:\Lyrion4Crestron\Crestron_SDK\Libraries\Crestron.DeviceDrivers.Core.dll`
- `C:\Lyrion4Crestron\Crestron_SDK\ManifestUtil\ManifestUtil.exe`

`ManifestUtil.exe` is the tool that wraps the built `.dll` plus `Driver.json` into the single `.pkg` file a Crestron controller can load. The build will still produce a `.dll` if `ManifestUtil.exe` is missing, but no `.pkg` will be generated and you will see a build warning.

### 1.3 Crestron Device Driver Configuration Tool (optional, for testing)

Install the **Crestron Device Driver Configuration Tool** (also from the SDK distribution). You will use it on your workstation to configure the driver (LMS host/port, credentials, player list) and to validate a running control system end-to-end.

### 1.4 Git (optional, for source control)

Install Git for Windows if you want to clone or push to the repository at <https://github.com/jopaul14/Lyrion4Crestron>.

## 2. Building

### 2.1 Via Visual Studio

1. Open `Lyrion4CrestronRepo\Platform_Lyrion_LMS_IP\Platform_Lyrion_LMS_IP.csproj` in Visual Studio 2019 or 2022.
2. Select the **Release** configuration.
3. Build > **Build Solution** (Ctrl+Shift+B).

Output (after a successful build):

```
Lyrion4CrestronRepo\Platform_Lyrion_LMS_IP\bin\Release\net472\Platform_Lyrion_LMS_IP.dll
Lyrion4CrestronRepo\Platform_Lyrion_LMS_IP\bin\Release\net472\Platform_Lyrion_LMS_IP.pkg
```

The `.pkg` file is what gets deployed to the Crestron controller.

#### A note on the build log

On a successful build you will see ManifestUtil print several `Null Exception: String reference not set to an instance of a String.` messages — one for each of the four `Crestron.DeviceDrivers.*.dll` assemblies that are copy-local next to the driver. These are harmless; ManifestUtil scans every DLL in the output folder looking for an embedded driver manifest, and complains (but keeps going) when it finds one without a manifest.

You will also see a `System.IO.FileLoadException` for `Microsoft.Office.Interop.Word`, followed by `Error creating DAT file`, near the end of ManifestUtil's output:

```
Exception: Could not load file or assembly 'Microsoft.Office.Interop.Word, Version=15.0.0.0...'
   at ManifestUtil.Helpers.DocumentHelper.ConvertDocumentsToPdf(String dllPath)
   at ManifestUtil.PkgFile.WriteToDisk(String outputDir)
```

This is expected and harmless on a machine that does not have Microsoft Office installed. After writing the `.pkg` file, ManifestUtil tries to generate a companion `.dat` file that contains **optional programmer-facing reference documentation**, and it does so by invoking Microsoft Word via COM interop to convert `.doc`/`.docx` help files into embedded PDFs. With no Word installed, the COM call fails and ManifestUtil exits non-zero, but the `.pkg` has already been written by that point. The project's build target is aware of this pattern: it ignores ManifestUtil's exit code and instead fails only if the `.pkg` file was not actually produced.

The missing `.dat` file does **not** affect the driver at runtime — the Crestron control system only needs the `.pkg`. The `.dat` file is only used to surface class-level reference documentation inside Crestron's programming tools. If you do want a `.dat` generated (e.g. to ship a fully-documented driver), install Microsoft Office with the Primary Interop Assemblies on the build machine and supply matching `.doc`/`.docx` help files alongside each class per the Crestron SDK's documentation conventions.

### 2.2 Via the command line

From a **Developer Command Prompt for Visual Studio** (this puts `msbuild.exe` on `PATH`):

```bat
cd C:\Lyrion4Crestron\Lyrion4CrestronRepo\Platform_Lyrion_LMS_IP
msbuild Platform_Lyrion_LMS_IP.csproj /p:Configuration=Release /restore
```

The first build performs a NuGet restore (there are no NuGet dependencies, but `Microsoft.NET.Sdk` requires the step). Subsequent builds can drop `/restore`.

## 3. Overriding the Crestron SDK path

If the Crestron SDK is not at `C:\Lyrion4Crestron\Crestron_SDK`, you have two options:

### Option A: environment variable (persistent)

Set a `CrestronSdkPath` environment variable pointing at your SDK install:

```bat
setx CrestronSdkPath "D:\Crestron\Crestron_SDK"
```

Open a new shell (or restart Visual Studio) for the change to take effect.

### Option B: per-build override

Pass the path on the MSBuild command line:

```bat
msbuild Platform_Lyrion_LMS_IP.csproj /p:Configuration=Release /p:CrestronSdkPath=D:\Crestron\Crestron_SDK
```

The project's `ValidateCrestronSdk` target will error out with a clear message if the path it resolves to does not contain the expected SDK assemblies.

## 4. Deploying the driver to a Crestron control system

1. Copy `Platform_Lyrion_LMS_IP.pkg` from the `bin\Release\net472` folder to the control system's `/user/` folder using Crestron Toolbox (File Manager or FTP).
2. Add the driver to the control system's program using SIMPL or SIMPL# Pro; select the `.pkg` file when prompted for the driver.
3. Configure the driver using the **Crestron Device Driver Configuration Tool**. The three configuration steps are:
   1. **Transport** — LMS hostname or IP, CLI port (default `9090`), HTTP port (default `9000`).
   2. **Authentication** — optional username and password (leave blank if your LMS does not require authentication).
   3. **Players** — a multi-line list, one player per line, in the form:

      ```
      MAC,Friendly Name[,Description]
      ```

      Example:

      ```
      00:04:20:aa:bb:cc,Kitchen,Kitchen Squeezebox Radio
      00:04:20:dd:ee:ff,Living Room
      # Lines starting with '#' are ignored.
      ```

      MACs must be lowercase hex with colon separators. Duplicate MACs or friendly names will produce a configuration error for the offending line.

4. Restart the control system program (or hot-reload via Toolbox) to pick up the new driver.

## 5. Troubleshooting

- **`error : Crestron SDK not found at '...'.`** — The SDK path the build resolved to does not contain `Libraries\Crestron.DeviceDrivers.SDK.dll`. Install the SDK or pass `/p:CrestronSdkPath=...` as described in section 3.
- **`warning : ManifestUtil.exe not found ...`** — The `.dll` built successfully but no `.pkg` was produced. Install the full Crestron Certified Drivers SDK (ManifestUtil is bundled with it) or copy `ManifestUtil.exe` to `$(CrestronSdkPath)\ManifestUtil\` and rebuild.
- **ManifestUtil prints `Null Exception: String reference not set to an instance of a String.` for each Crestron SDK DLL.** — Harmless. ManifestUtil scans every DLL in the output directory; these messages are emitted when it finds one without an embedded driver manifest (i.e. the Crestron SDK reference assemblies). The build is unaffected.
- **ManifestUtil prints `Could not load file or assembly 'Microsoft.Office.Interop.Word'` and `Error creating DAT file`, but the build still succeeds.** — Harmless. The `.pkg` is produced before this point; only the optional `.dat` programmer-documentation file is skipped. See section 2.1, "A note on the build log," for the full explanation. The `.dat` file is not required for the driver to run on a Crestron controller.
- **Build succeeds, driver does not load on the controller.** — Confirm that `Driver.json`'s `MinSdkVersion` (`25.0000.0033`) is less than or equal to the driver runtime version on the controller. If the controller is older, it cannot load the driver.
- **"Player not found" in the configuration tool.** — The driver only exposes players that are configured in the _Players_ list. Verify the MAC matches exactly the one LMS reports (LMS Server Web UI → Settings → Information shows each player's MAC).
- **Driver connects but never receives playback updates.** — The driver subscribes to all CLI notifications (`listen 1`) on connect. If your LMS is behind a firewall, make sure TCP port 9090 is reachable from the Crestron controller.
