# H.264 hook for SteamVR streaming

`SteamFrameH264Hook.dll` is injected into `vrserver.exe` by `hook_control.exe`. It intercepts the public NVENC factory only for SteamVR's VRLink driver (`hook_entry.cpp`) and translates the driver's HEVC-facing SDK 11.1 function table (`legacy_api_bridge.cpp`) into an H.264 encoder session, so the Frame receives H.264 up to 500 Mbps. The tray starts and removes it through the supervisor (`Frame/Codec/codec_supervisor.py`); you normally never run it by hand.

The hook produces H.264 only. Two encoder options are an experiment; they are applied live (`hook_control ... configure`, queued and adopted by the running encoder at its next reconfigure) or at the next arm: the rate control (VBR, the default, or CBR where the peak equals the average) the NVENC preset (P1 to P7, default P2) and the VBV buffer size (1 to 16 frames of the average bitrate, default 2: one frame undershoots the target by about a quarter). The tray sets them from its "Encoder experiment" menu and logs them in the run header.

## Build

Run `build.ps1` from Windows PowerShell with Visual Studio C++ and a Windows SDK installed. Output goes to `artifacts/H264Hook/`:

- `SteamFrameH264Hook.dll` – the hook.
- `hook_control.exe` – `hook_control PID DLL install|configure|stats|identity|remove [average_bps peak_bps fixed_target [fps [codec rate_control preset vbv_frames]]]`. Codec must be `2` (H.264, 500 Mbps limit), `rate_control` is `vbr` or `cbr`, `preset` is 1 to 7, `vbv_frames` is 1 to 16.
- `hook_control PID DLL mode h264|stock` and `switch-info` (experimental, branch `live-codec-switch`): the hook opens the driver's own SDK 11.1 (HEVC) encoder beside the H.264 one for every session SteamVR opens, and SteamVR's buffer handles are virtual, wrapping both. `mode` chooses which encoder takes the frames mapped from then on (the first frame after a switch is a keyframe with parameter sets); the Frame's client must change at the same moment, which the supervisor does while the stream is stopped. `switch-info` prints the mode and counters. The hook must have been loaded when SteamVR started, because it only wraps sessions opened afterwards. The driver checks every session open against the API version of the process's latest `NvEncodeAPICreateInstance` call, so the bridge asks for each version again right before opening a session of it.
- `codec_caps.exe` – prints the NVENC codec capabilities of the installed GPU.
- `picture_abi_test.exe`, `bridge_gpu_test.exe` – the guards below.

This directory is also a standalone CMake project (`CMakeLists.txt`).

## Layout rules

The SDK 11.1 picture's QP-map offset differs from SDK 12.1. Never copy the whole legacy picture prefix and then read its fields through a modern struct. `legacy_picture.cpp` only extracts the legacy fields explicitly; `legacy_api_bridge.cpp` separately maps them into the modern picture. `picture_abi_test.exe` guards the layout. `bridge_gpu_test.exe [async|sync] [plain|fixed100|fixed400|fixed500] [90|120] [vbr|cbr] [preset 1-7] [vbv frames 1-16]` exercises preserved inputs, memory canaries, resources, completion events and reconfiguration on a real GPU, and confirms the encoder accepts the chosen rate control and preset.

`start_steamvr_trial.ps1 -Mbps N` launches SteamVR and installs the hook early, without the tray, for isolated trials.

The exported entry points are `HookInstallTrial`, `HookReconfigure`, `HookRemoveTrial`, `HookReadTrialStats`, `HookReadTrialIdentity`, `HookSwitchMode` and `HookReadSwitchInfo`. The managed side reads them in `Core/EncoderProbe.Hook.cs`.
