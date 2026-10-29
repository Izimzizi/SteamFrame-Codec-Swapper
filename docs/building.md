# Building from source

## Requirements

- Windows 11 x64
- .NET 9 SDK
- Visual Studio 2022 or later, or Build Tools, with **Desktop development with C++** and a Windows SDK. The scripts find Visual Studio automatically.
- Python 3 on PATH for the supervisor tests.

## Build and test

```powershell
dotnet build SteamFrameTray -c Release
dotnet test Tests\CoreTests
dotnet test Tests\TrayTests
dotnet test Tests\PatcherTests
dotnet test Tests\StreamingHealthTests
dotnet run --project Tests\CompositorTimingTests -c Release
cd Frame\Codec; python -m unittest
```

Native pieces:

```powershell
.\Native\build.ps1                                   # encoder probe (SteamFrameEncoderProbe.dll) and its tests
.\Native\H264Hook\build.ps1 -OutputDirectory <dir>   # SteamFrameH264Hook.dll, hook_control.exe and the hook tests
dotnet run --project Native\tests\managed -- Native\build\SteamFrameEncoderProbe.dll
```

None of these tests modify your Steam configuration, and none load anything into a running SteamVR.

## Package

`Package-Tray.ps1` publishes the tray (self-contained by default) and assembles `CodecLab` beside it, then writes a zip:

```powershell
.\Package-Tray.ps1 -CodecLabSource <folder with runtime\ (isolated Python), Assets\ and Backend\> -OutputDirectory artifacts\SteamFrameTray
```

`Backend\` holds `SteamFrameH264Hook.dll` and `hook_control.exe`; pass `-BuildBackend` to compile them from `Native\H264Hook` instead. Exit any running tray (and end any leftover supervisor) before packaging over the same folder.

## Source map

| Path | Contents |
| --- | --- |
| `SteamFrameTray` | Tray app, setup wizard, stats window, run log |
| `Core` | Shared UI-free code (see the project layout in the root README) |
| `Native` | C++ NVENC encoder probe, quality-map logic and tests; see [its README](../Native/README.md) |
| `Native\H264Hook` | The H.264 hook DLL, its controller and tests; see [its README](../Native/H264Hook/README.md) |
| `Frame\Codec` | Supervisor, root session, Frame client patcher and debug scripts |
| `Tests` | xUnit projects |
