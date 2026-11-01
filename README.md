# SteamFrame Codec Swapper

A Windows tray app (**SteamFrame Codec Swapper**) for the Steam Frame that streams **H.264 at up to 500 Mbps** from SteamVR, switches between H.264 and the stock HEVC stream while you play, and shows what the stream is doing, both in a monitor window and on an **in-VR HUD** attached to a controller.

> ## ⚠️ Heavily work in progress
>
> This project changes how SteamVR's streaming driver and the Steam Frame's Steam Link client behave, and it is developed against one hardware setup. Expect rough edges, behavior that changes from commit to commit, and things that only worked on the author's PC.
> Several parts are **untested on real hardware** (listed under [Known limitations](#known-limitations)). Nothing here is signed, supported by Valve, or guaranteed to survive a SteamVR or Steam Link update.
> Back up what matters, keep the Frame's stock client restorable (Disarm / Default (HEVC) does this), and do not run it on a machine you cannot afford to fiddle with.

> **Platform:** Windows 11 x64 · SteamVR with the Steam Link VR driver · NVIDIA GPU (NVENC) · Steam Frame in Developer Mode, reachable over SSH (Wi-Fi, the dongle, or both).

## What it does

- **H.264 / Default (HEVC) switch.** The stock Steam Link client on the Frame hardcodes HEVC. The tray patches a copy of the client on the Frame and loads a hook into SteamVR's driver, so one choice flips both halves together. "Default (HEVC)" is the untouched stock stream.
- **Up to 500 Mbps.** The bitrate slider covers 25–500 Mbps. SteamVR's own driver streams at most 232 Mbps and its own slider stops at 350: the tray sets any bitrate up to 350 Mbps without the patch (with a warning above 232 that it has no effect yet), and asks for the bitrate patch, which it can install and remove, only above 350.
- **Fast switching.** The SteamVR-side driver stays loaded and the SSH sessions to the Frame stay open, so going H.264 ↔ HEVC restarts only the Frame's stream (about 9 s end to end on the author's setup), not SteamVR.
- **Encoder options.** Rate control (VBR / CBR), NVENC preset (P1–P7) and VBV buffer (1–16 frames of the average bitrate). They apply live to H.264 and also to Default (HEVC).
- **Stream monitor.** Encoded Mbps with a 60-second average, FPS, what the encoder was asked for, frames held back by the sender and dropped/late on the PC, encode errors, the active codec and a 60-second graph. It can also record a CSV run log.
- **VR HUD.** A panel on a controller with the same readouts and laser-pointer controls (see [VR HUD](#in-vr-hud)).
- **Connection modes.** Works with the dongle only, plain Wi-Fi only, or both at once (multilink).

## How it works

```
 Tray (WinForms)  ──►  Codec supervisor (Python, per session) ──SSH──►  Steam Frame
   │  HUD helper process                 │                               patched Steam Link client
   │  hevc-options.txt                  ▼                               (bind-mounted over the stock one)
   │                              SteamVR (vrserver.exe)
   └───────────────────────────►   driver_steamframe.dll  (the hook, as a SteamVR driver)
                                    ├─ H.264 bridge: SDK 11.1 HEVC calls → an H.264 NVENC session
                                    └─ stock forwarding: untouched HEVC (+ optional option overrides)
```

- **The Frame side.** `root_session.py` finds the one place in the Frame's Steam Link client (`vrlink`) that loads the codec name `HEVC` (two ARM64 instructions), patches a copy to `H264` and bind-mounts that copy over the stock one (root, via SSH and sudo). Going back to HEVC un-mounts it. Stream start/stop and the codec flip are driven through Steam's own debugger channel (`frame_cef_server.py`, one long-lived SSH session instead of a login per command).
- **The PC side.** `Native/H264Hook` builds `SteamFrameH264Hook.dll`. It detours `NvEncodeAPICreateInstance` for calls coming from `driver_vrlink.dll` and hands back a bridge table that turns the driver's HEVC-shaped encoder session into an H.264 one. A second path forwards to the real NVENC untouched ("stock"). The active codec is one global mode that can be flipped while SteamVR runs.
- **The SteamVR driver.** The same DLL is installed as a SteamVR driver (`driver_steamframe.dll` plus `driver.vrdrivermanifest`, registered with `vrpathreg adddriver`), so it is loaded at every SteamVR start, begins on stock HEVC and only switches when you ask. No injection, no SteamVR restart. Install it from the tray's *SteamVR driver* menu.
- **The supervisor.** `Frame/Codec/session_supervisor.py` is the session between tray and Frame: it keeps the choice (H.264 or HEVC) across SteamVR restarts, tolerates an unreachable Frame (the choice is applied when it returns), and restores stock on disarm or when the tray goes away.
- **The tray.** `CodecSwapper/` arms/disarms, reads and writes SteamVR's bitrate (live through OpenVR, or in `steamvr.vrsettings` when SteamVR is closed), samples stats and runs the HUD.

## Install and first run

1. On the Frame: turn on Developer Mode and set a user password (Developer → Set User Password).
2. Extract the whole zip. `CodecSwapper.exe` needs the `CodecLab` and `Native` folders beside it. It is unsigned, so Windows SmartScreen may warn (More info → Run anyway).
3. The **Setup / connection check** opens by itself. The address is `frame` over Wi-Fi, or `10.35.78.1` with the dongle. It shows the Frame's host key fingerprint to trust once, creates the app's own SSH key and asks for the Frame password once to authorize that key. The password goes to Windows Credential Manager only after the Frame accepts it; it is never written to settings, files or command lines. Windows' optional **OpenSSH Client** feature is required.
4. **Required for H.264:** tray menu → **SteamVR driver** → *Install* (choosing H.264 without it offers to install it, or tells you what to do if SteamVR is running; SteamVR is never restarted by the tray). The tray also brings an out-of-date driver up to date at start when SteamVR is closed.
   Optional: **Bitrate limit patch** → *Install* (SteamVR closed; if Steam lives under Program Files, use *Restart as administrator* once) so that bitrates above 232 Mbps actually stream (the tray asks for it above 350).
5. Choose **H.264** in the tray menu (or on the HUD). With SteamVR closed the choice is remembered and applied when SteamVR starts; the tray never launches SteamVR itself.

## Using it

### Tray menu

| Item | What it does |
|---|---|
| **H.264 / Default (HEVC)** | The codec switch. Choosing Default keeps the session open so switching back is fast. |
| **Bitrate slider** | 25–500 Mbps, applied when you let go. SteamVR's own slider and the tray follow each other. |
| **Encoder settings** | Rate control, preset and VBV, for both codecs. |
| **Stream monitor** | The monitor. Double-clicking the tray icon opens/closes it. |
| **Record stream log** | One CSV row per second, setup in `#` header lines. |
| **VR HUD** | Show or hide the HUD (on by default), attach left/right, reset position. |
| **Start with Windows** | Re-arms at login if it was armed. |
| **Frame connection…**, **Password** | Reopen setup; set or forget the stored Frame password. |
| **Bitrate limit patch**, **SteamVR driver** | Install/remove the 500 Mbps patch and the SteamVR driver. |
| **Open logs folder / session folder** | Where logs and the supervisor session live. |
| **Exit** | Restores stock HEVC on the Frame and ends the session. |

### Stream monitor

Codec (verified against the hook, `?` if it is not), encoded Mbps and its 60-second average, FPS (one decimal), target, "encoder asked for X avg / Y peak (rate control, preset, VBV, fps)", **encode time** (average and worst milliseconds per frame over the last second, measured inside the hook from submitting a picture to its bitstream being ready; this is how to see the effect of the preset), frames the sender held back (total and last 60 s) plus PC dropped/late frames, encode errors, a status note and the 60-second graph. While the monitor or HUD is open on **stock HEVC** the tray injects a read-only probe into `vrserver` to read encoder statistics; it is not injected otherwise.

### VR HUD

The HUD is **on by default**: when SteamVR is running the panel appears on the chosen controller. Its *Turn off HUD* button (or *VR HUD → Show HUD on a controller* in the tray menu) switches it off, and the same menu item brings it back. SteamVR only passes laser input to overlays while its **dashboard is open**, so the HUD has two states: with the dashboard closed (while playing) it shows the readouts only and cannot press anything, so it can never steal input from a game; with the dashboard open it adds the controls. It is a SteamVR overlay driven by laser pointer:

- the codec buttons, a **bitrate slider** (drag with the laser, applied on release; an amber mark at 350 Mbps, the top of SteamVR's own slider, pulls a click near it onto exactly 350), and three **dropdowns** — *Rate control*, *Preset*, *VBV* — each opening a list of options with a short description;
- every readout the monitor has: codec, Mbps, 60 s average, FPS, target, encoder-asked line, held-back / dropped / late / errors, note and the 60 s graph;
- **Move**: hold the trigger on the panel with the other hand's laser to carry it to a new spot on the controller, then *Save*, *Cancel*, or *Default* (reset); *Smaller/Larger* resize it.

Because the HUD is drawn on the PC and streamed, the Frame's own dashboard and Steam window are drawn over it; the PC's windows are inside the stream, so the HUD stays in front of those. Moving the HUD off the area the Frame's windows cover is the workaround. (Drawing it on the Frame instead was tried and dropped: it needs an SSH round trip per update, so it lags.)

Only things that are not working are announced: a warning or error from the tray (the SteamVR driver is missing or out of date, the bitrate patch is needed, an action failed) is shown as an amber or red banner on the HUD for about 14 seconds as well as in a desktop balloon. Successful changes say nothing. The helper exits when SteamVR does and is restarted by the tray, which gives it its settings again, so the HUD comes back by itself when SteamVR restarts.

Internals worth knowing: the HUD runs in its **own helper process** (`CodecSwapper.exe --hud-host`) because it talks to SteamVR's native overlay API and a native fault there must not take the tray down; the helper restarts itself. The picture is written into one Direct3D 11 texture, so it updates without flicker. It never starts SteamVR.

### Encoder options and Default (HEVC)

For **H.264** the options are queued on the hook and applied to the running encoder on SteamVR's next reconfigure, with no restart. For **Default (HEVC)** the tray writes `hevc-options.txt` (`rate_control`, `preset`, `vbv`); the hook reads it about twice a second and applies it to every reconfigure SteamVR makes (before each frame), including the preset, which SteamVR's own reconfigures would otherwise reset. A change of rate control, buffer or preset asks the encoder for one rate-control reset and a keyframe. The hook writes a line to `hevc-hook.log` when the HEVC encoder is created and on each change, so you can see what reached NVENC. The defaults (VBR, P2, 2 frames) apply to Default (HEVC) too; without the file the stock stream is exactly what SteamVR configured. To judge a preset, compare the encode time (and the Video Encode load in Task Manager) at P1 and P7: on a recent NVIDIA GPU the picture differs little, the encode time does.

## Where things live

Everything the tray owns is under `%LOCALAPPDATA%\CodecSwapper` (it never touches the old `SteamLinkVRSettings` folder):

| File | Purpose |
|---|---|
| `settings.json` | Preferences (bitrate target, encoder options, startup). Never holds the Frame password. |
| `hud.json` | HUD on/off, hand, placement, size. |
| `hevc-options.txt` | Encoder options for Default (HEVC), read by the hook. |
| `h264-limit.txt` | Optional width/height limit override for the H.264 bridge. |
| `tray.log`, `hud.log` | Arm/disarm requests (with who asked) and HUD steps/button presses. |
| `CodecSession\` | Supervisor session: `status.json`, `supervisor.log`, request/response files. |
| `Logs\` | Recorded stats CSVs. |
| `known_hosts`, `frame_ed25519` | The pinned Frame host key and the app's own SSH key. |

Versions before the rename kept their files in `%LOCALAPPDATA%\SteamFrameTray` and stored the Frame password under `SteamFrameAdvancedOptions/FrameUserPassword` in Windows Credential Manager. The app does not read them; delete them if you no longer need them (you will set up the Frame connection again).

## Project layout

```
CodecSwapper/        The tray app (WinForms): menu, setup wizard, stats window, run log
CodecSwapper/Hud/    VR HUD: panel drawing, laser controller, OpenVR overlay API, helper process, settings
Core/                  Shared C# (SteamFrame.Core): SteamVR/Frame access, codec control, bitrate patcher,
                       encoder probe, health readers, advanced encoder controls, capacity sweep, wireless link tools
Native/H264Hook/       The hook DLL (H.264 bridge, stock forwarding, option overrides), its controller and tests
Native/                NVENC encoder probe (C++), vendored MinHook / NVIDIA headers / OpenVR, multistream research
Frame/Codec/           Supervisor, root session, client patcher, Frame stream scripts (start/stop, debugger channel)
Tests/                 xUnit projects (Core, Tray, Patcher, StreamingHealth, CompositorTiming)
docs/                  Guides and research notes (some predate the tray and describe the removed WinUI app)
Package-CodecSwapper.ps1       Builds the portable package (artifacts/CodecSwapper and CodecSwapper.zip)
```

## Build, test and package

See [docs/building.md](docs/building.md) for requirements (.NET 9 SDK, Visual Studio C++ build tools, Python 3).

```powershell
dotnet build CodecSwapper -c Release
dotnet test Tests\CoreTests -c Release
dotnet test Tests\CodecSwapperTests -c Release
cd Frame\Codec; python -m unittest
.\Native\H264Hook\build.ps1 -OutputDirectory <dir>     # hook DLL, hook_control.exe, native tests (GPU needed)
.\Package-CodecSwapper.ps1 -CodecLabSource <CodecLab folder with runtime\, Assets\ and Backend\> -OutputDirectory artifacts\CodecSwapper
```

Close a running tray before packaging. The tests do not modify your Steam configuration or load anything into a running SteamVR.

## Known limitations

- **H.264 wider than 1024 px** is not verified on the Frame; the bridge takes its limit from the GPU's H.264 caps (override with `h264-limit.txt`).
- **HEVC option overrides** are confirmed live on the headset: rate control, buffer and preset (P1 to P7 moved the Video Encode load from about 7% to 16% and the measured encode time, without a restart).
- **The HUD** has been tested on one controller setup; laser coordinates assume SteamVR's current GL-space convention (`FlipLaserY` in `hud.json` flips it). Dropdown rows are small targets.
- **An unexplained close** right after reaching H.264 happened once before logging existed (likely an accidental HUD *Default (HEVC)* press); `tray.log` now records who asks for each change.
- **The Steam UI on the Frame** can get stuck after repeated stream restarts; a restart of the Frame's Steam UI is the workaround, and it is not automated.
- **Frame updates.** The client patch is found by its instructions, not by a fixed file hash, so Frame updates that keep the client's layout need no new release (checked on SteamOS 0.4.5, 0.5.6 beta and 0.6.0 preview). If the client changes so that there is not exactly one place to patch, H.264 is refused with a message and Default (HEVC) keeps working. The Frame's kernel version is not checked.
- **Updates.** A SteamVR or Steam Link update can change the driver layout the hook and bitrate patch rely on; the patch refuses unrecognized drivers rather than guessing.
- **Not done yet:** a "Save Frame log" menu item, user docs for the HUD beyond this README, signed binaries.

## Credits

This project is a **fork** of the original **Steam Link VR settings editor / Steam Frame edition** by **[elliotttate](https://github.com/elliotttate)** ([elliotttate/SteamFrame-Advanced-Options](https://github.com/elliotttate/SteamFrame-Advanced-Options)), released under the MIT license. The original project provided the foundation this work started from: the SteamVR/Steam Link settings access, the bitrate patcher, the NVENC encoder probe, the streaming-health readers and the research notes under `docs/`.

After forking it was **heavily modified**: the tray app, the H.264 hook and Frame client patcher, the supervisor, the SteamVR driver, live H.264 ↔ HEVC switching with a kept session, the shared Frame debugger channel, dongle / Wi-Fi / multilink support, the encoder-option plumbing (including the HEVC overrides), the stream monitor changes and the in-VR HUD were built on top of it. Bugs in this fork are not the original author's.

Third-party components keep their own licenses: MinHook/HDE and the NVIDIA Video Codec SDK header under `Native/vendor`, and OpenVR's headers/API. The MIT license in [LICENSE](LICENSE) carries the original copyright notice.
