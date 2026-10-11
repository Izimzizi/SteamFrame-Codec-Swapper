# Building from source

## Requirements

- Windows 11 x64
- .NET 9 SDK
- Visual Studio 2022 or later, or Build Tools, with **Desktop development with C++** and a
  Windows SDK. The scripts find Visual Studio and its bundled CMake and Ninja automatically.
- Python 3 on PATH, only for the receiver-diagnostics tests. They're skipped without it.

The WinUI app must be built with Visual Studio's MSBuild. `dotnet build` can't build it because
the .NET CLI lacks the Windows App SDK packaging tasks; the test projects build fine with it.

## Build

```powershell
.\Build.ps1
```

This builds the native encoder probe and runs its tests, builds the app, and runs the test
projects. Output goes to `SteamLinkVRSettings\bin\x64\Release\net9.0-windows10.0.22621.0\`.

Close any copy of the app running from that folder first, or build elsewhere:

```powershell
.\Build.ps1 -OutputDirectory "artifacts\BuildCheck"
```

Options: `-Configuration Debug`, `-SkipNativeTests`.

Opening the solution in Visual Studio also works before the native helper has been built. You'll
see a warning, and encoder monitoring stays unavailable until `Native\build.ps1` has run.

## Package

```powershell
.\Package.ps1
```

Produces a self-contained portable folder and ZIP under `artifacts\`:
`SteamLinkVRSettings-SteamFrame-win-x64\` and `SteamLinkVRSettings-SteamFrame-win-x64.zip`.
The package includes the .NET and Windows App SDK runtimes, the README and third-party licenses.
Use `-SkipBuild` to package an existing build.

## Tests

The projects under `Tests\` are xUnit projects in the solution and appear in Test Explorer.

```powershell
dotnet test Tests\CoreTests
dotnet test Tests\StreamingHealthTests
dotnet test Tests\StreamingFrameTraceTests
dotnet test Tests\FrameReceiverDiagnosticsTests
```

| Project | Covers |
| --- | --- |
| CoreTests | Settings file safety, the settings editor view model, Steam paths, preferences, rate math, traffic selection |
| StreamingHealthTests | PC log parsing, rotation, truncation and privacy of captured events |
| StreamingFrameTraceTests | Recovery-trace timing without inventing drops |
| FrameReceiverDiagnosticsTests | The headset-side collector script, run locally against fixture logs |
| PatcherTests | Exact patch and restore hashes, refusal paths, backups and adaptive discovery |
| CompositorTimingTests | PC presentation counter handling (console project, run by `Build.ps1`) |

**PatcherTests** needs a verified original `driver_vrlink.dll`, which is never committed:

```powershell
$env:SLVR_PATCHER_FIXTURE = "path\to\verified-original-driver_vrlink.dll"
$env:SLVR_PATCHER_CURRENT_FIXTURE = "path\to\later-original.dll"   # optional
dotnet test Tests\PatcherTests
```

Native tests run as part of `Native\build.ps1`. The managed probe harness checks the built helper:

```powershell
dotnet run --project Native\tests\managed -- Native\build\SteamFrameEncoderProbe.dll
```

None of these tests modify your Steam configuration, and none load anything into a running SteamVR.

## Source map

| Path | Contents |
| --- | --- |
| `SteamLinkVRSettings\Views` | Frame dashboard and controls page, settings editor page |
| `SteamLinkVRSettings\ViewModels` | Settings editor view model |
| `SteamLinkVRSettings\Frame` | SteamVR paths and live settings, connections, patcher, encoder probe controller, diagnostics |
| `SteamLinkVRSettings\Services` | Atomic `steamvr.vrsettings` reading and writing |
| `Native` | C++ NVENC encoder probe, quality-map logic and tests; see [its README](../Native/README.md) |
