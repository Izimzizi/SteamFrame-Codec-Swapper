# SteamFrame Advanced Options

A small Windows tray app (**SteamFrame Tray**) that keeps **H.264 up to 500 Mbps** applied to a Steam Frame while the tray is running and armed, with the bitrate following SteamVR's own slider. It also shows live stream stats (encoded Mbps and its 60-second average, FPS, the rate the encoder was configured with, dropped frames) and can record a CSV run log.

> **Platform:** Windows 11 x64 · SteamVR with the Steam Link VR driver · NVIDIA GPU · Steam Frame in Developer Mode

How to install, set up and use it: [SteamFrameTray/README.md](SteamFrameTray/README.md).

## How it works

- The stock Steam Link client on the Frame hardcodes HEVC. `Frame/Codec/stage_client_codec.py` patches two instructions in a copy of the client, and `root_session.py` bind-mounts that copy over the stock one (root, via SSH and sudo). Disarming or exiting restores the stock client.
- On the PC, `Native/H264Hook` builds `SteamFrameH264Hook.dll`, injected into `vrserver.exe` by `hook_control.exe`. It translates the driver's HEVC encoder session into H.264.
- `Frame/Codec/codec_supervisor.py` keeps both halves applied across SteamVR restarts and restores everything on disarm.
- The tray (`SteamFrameTray/`) arms and disarms, reads and writes SteamVR's bitrate (live through OpenVR, or in `steamvr.vrsettings` when SteamVR is closed), and shows stats.

## Project layout

```
SteamFrameTray/        The tray app (WinForms): menu, setup wizard, stats window, run log
Core/                  Shared C# (namespace SteamFrame.Core): SteamVR/Frame access, codec control,
                       bitrate patcher, encoder probe, health readers, and the kept extras (advanced
                       encoder controls, SteamVR stream settings, capacity sweep, wireless link tools)
Native/                NVENC encoder probe (C++), its tests, vendored MinHook / NVIDIA headers / OpenVR
Native/H264Hook/       The H.264 hook DLL and its controller
Native/Multistream/    Square multistream research sender
Frame/Codec/           Supervisor, root session, client patcher, debug scripts (steamvr_soak, read_trial_logs)
Frame/Multistream/     Square multistream research receiver
Tests/                 xUnit test projects
docs/                  Guides and research notes
```

Some guides under `docs/` were written for the earlier WinUI app, which has been removed; the research and codec notes still apply.

Third-party licenses for MinHook/HDE and the NVIDIA Video Codec SDK header live under `Native/vendor`. See [docs/building.md](docs/building.md) to build from source.
