# Steam Link VR Settings

A WinUI 3 desktop app for editing the Steam Link VR streaming driver settings that
SteamVR does not expose in its own UI.

## What it does

SteamVR's Steam Link driver (`driver_vrlink`) has a large number of tunables —
bandwidth targets, encode/render resolutions, encoder mode, FEC, QoS — that live
only in JSON config files. This app surfaces them as sliders, toggles, and dropdowns.

- Reads driver defaults from
  `SteamVR\drivers\vrlink\resources\settings\default.vrsettings`
- Reads and writes your overrides in `Steam\config\steamvr.vrsettings`
- Merges the two so the UI shows *effective* values, marking anything you've
  overridden away from the default
- Preserves unknown keys already present in the config when saving

Settings are grouped into five categories: **Streaming Quality**, **Network**,
**Display**, **Tracking & Input**, and **Advanced**.

## Requirements

- Windows 10 2004 (build 19041) or later
- .NET 9 SDK with the Windows App SDK workload
- Visual Studio 2022 (for `build.cmd`)

## Building

```cmd
build.cmd
```

Or directly:

```cmd
msbuild SteamLinkVRSettings\SteamLinkVRSettings.csproj -p:Configuration=Release -p:Platform=x64 -restore
```

Output lands in `SteamLinkVRSettings\bin\x64\Release\net9.0-windows10.0.22621.0\`.

## Running

The app requires administrator rights — it writes to `steamvr.vrsettings` under
`C:\Program Files (x86)\Steam\config\`. The manifest requests elevation, so you'll
get a UAC prompt on launch.

> **Close SteamVR before saving.** SteamVR rewrites `steamvr.vrsettings` on
> shutdown, so changes saved while it is running will be overwritten.

## Layout

```
SteamLinkVRSettings/
  MainWindow.xaml(.cs)
  Views/SettingsPage.xaml(.cs)
  ViewModels/SettingsViewModel.cs
  Services/SettingsService.cs      # vrsettings read / merge / write
  Helpers/SettingDefinitions.cs    # the setting catalog
  Models/SettingItem.cs
  Converters/Converters.cs
  Themes/AppTheme.xaml
```
