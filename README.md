# Steam Link VR Settings — Steam Frame edition

A Windows app for tuning and diagnosing **Steam Frame** streaming from a PC.
It sets the stream's bitrate and foveation, measures what the encoder actually
produces, records streaming faults on both the PC and the headset, and can raise
the SteamVR driver's bitrate ceiling with a fully reversible patch.

The experimental codec selector can also start **AV1 or H.264** on a supported
Steam Frame, applying the PC and headset changes as one supervised session.
The dashboard verifies the codec from fresh encoder output.

It also includes a full editor for every Steam Link VR (`driver_vrlink`) setting,
including the ones SteamVR doesn't show in its own UI.

![Live dashboard measuring a Steam Frame stream: encoded bitrate, requested target, video path and a 60-second graph](docs/images/live-dashboard.png)

> **Platform:** Windows 11 x64 · SteamVR with the Steam Link VR driver · NVIDIA GPU
> for encoder measurements and foveation bias (AMD AMF can be enabled, but isn't measured)

## Features

| Area | What it does |
| --- | --- |
| **Stream controls** | Set the video bitrate target, automatic bandwidth, AMD AMF and USB link joining. Changes apply live to running SteamVR, or are saved for its next start. |
| **SteamVR filtering** | Toggle Advanced Supersample Filtering with accepted live readback or a saved setting for the next SteamVR session. |
| **Foveation bias** | Shift quality between where you look and the periphery on top of Valve's own gaze-tracked map. |
| **Advanced controls** | Tune CQ, maximum QP, peak bitrate, VBV, chroma, AQ and presets, plus resolution, buffering and sending settings, with accepted/pending/rejected readbacks and restoration. |
| **Live dashboard** | Graphs actual encoded video Mbps and FPS, the requested target, and adapter traffic over the last 60 seconds. |
| **Codec selector** | Switch between HEVC, AV1 and H.264, choose a 25–200 Mbps request and 90/120 Hz, and restore stock HEVC. AV1/H.264 are exact-build experiments and restart the stream. |
| **Streaming issue capture** | Timestamps recovery requests, bad-link events, encoder stalls, PC presentation drops and headset receiver faults, and saves them as JSONL. |
| **Driver patcher** | Raises the driver's 232/350 Mbps ceilings to 500 Mbps and selects HEVC level 6.2, with verified backups and one-click restore. |
| **Connection tools** | Reads headset link status over paired wireless SSH or ADB, and can restart a stalled Frame USB connection. |
| **All streaming settings** | Every known `driver_vrlink` setting, with defaults, reset buttons, and warnings for experimental options. |

## Download and run

1. Download `SteamLinkVRSettings-SteamFrame-win-x64.zip` from the
   [latest release](../../releases/latest).
2. Extract the **whole** folder anywhere. The ZIP bundles the .NET and Windows App SDK
   runtimes, so nothing else needs to be installed.
3. Run `SteamLinkVRSettings.exe`.

The app runs as your normal user account. If an action is denied access, such as
patching a driver in a protected folder, the status bar offers **Restart as
administrator**. Opening the app never launches SteamVR, modifies a driver, or
starts encoder monitoring on its own.

## Quick start

1. Keep Wi-Fi on the Frame enabled and start a VR stream.
2. Open **Stream controls**, choose a bitrate target, and click **Apply stream controls**.
3. On **Live dashboard**, click **Start live monitor** to see the real encoded bitrate.
4. Optionally enable **Custom foveation bias**. **Quality focus** (gaze −4, periphery +8)
   is a good starting point.
5. To request more than 232 Mbps on a stock driver, close SteamVR and use
   **Driver patcher → Install 500 Mbps override** first.

For AV1 or H.264, follow the [codec setup guide](docs/codec-modes.md) first.
The release includes the companion runtime and AV1 decoder, so supported devices
can be patched through the app without building the source or installing Python
on the PC. Developer Mode, trusted SteamOS Devkit SSH pairing and the Frame user
password are required. The companion generates the client patch from the user's
own installed file. It refuses unknown headset builds.

## Documentation

| Guide | Covers |
| --- | --- |
| [Stream controls and foveation](docs/stream-controls.md) | Bitrate, stream width, VBR/CBR, encoding passes, additional links, gaze offsets and temporary test power settings |
| [Advanced controls](docs/advanced-controls.md) | CQ, max QP, peak/VBV, chroma/AQ/presets, driver width/pooling/sharpening/sending experiments, readbacks and restoration |
| [Codec modes](docs/codec-modes.md) | AV1/H.264 setup, supported builds, hardware requirements, switching, verification and rollback |
| [Live dashboard and diagnostics](docs/live-dashboard.md) | Every readout, capture files, recovery traces, PC presentation counters |
| [Driver patcher](docs/driver-patcher.md) | What the patch changes, compatibility checks, backups and restore |
| [Connection setup](docs/connection-setup.md) | Wireless SSH pairing, optional ADB, USB recovery |
| [All streaming settings](docs/settings-editor.md) | The full settings editor, resets, backups, experimental options |
| [Building from source](docs/building.md) | Toolchain, build and package scripts, tests |
| [Encoder probe internals](Native/README.md) · [Compatibility evidence](Native/VERIFICATION.md) | How the NVENC helper works and how it was verified |

## Safety and privacy

- **Reversible by design.** Config saves are atomic and keep both the previous revision and
  your original file. Driver patches keep a hash-verified original and a receipt.
- **Measurement only.** The encoder monitor counts compressed frame sizes and never reads,
  stores or copies video. Log capture stores synthesized events, not raw log lines.
- **No surprises.** Monitoring and patching only happen when you click them. SSH uses strict
  host checking with your own trusted host file and never auto-trusts a device.
- **Nothing personal is shipped.** SSH keys, passwords, personal settings, logs and
  Valve binaries are never packaged. The codec companion uses the dedicated dongle's
  fixed device address. It includes patch recipes and the experimental open-source
  decoder module; the original Valve client and firmware come from the headset.

## Known limitations

- The bitrate target is a request, not a minimum. Simple scenes can encode below it,
  and no filler is added.
- 500 Mbps is a software ceiling. One measured USB 2 connection carried about
  318–325 Mbps of UDP payload; real headroom depends on the link and scene.
- Encoder measurement and foveation bias support NVENC only.
- Experimental NVENC rate-control and pass controls use guarded public API 11.1
  initialize/reconfigure calls. The dashboard separates the requested policy from
  the last accepted encoder configuration and reports whole-frame QP separately
  from gaze-map deltas. CBR without padding is a target, not a useful minimum.
- Quarter-resolution multipass may use more of the target for useful detail but
  can add work or stutter. Valve's default encoding policy remains the app default;
  reconnect-only presets are available in Advanced controls with Valve's stream
  policy preserved. No preset or AQ experiment is enabled by default.
- A future SteamVR driver with a rewritten encoder can disable the patcher or foveation
  until the app is updated. Both refuse layouts they can't verify rather than guessing.
- Experimental AV1/H.264 sessions require Frame kernel `6.18.0-ge66bc2ca6f8c`
  and the specific stock VRLink client documented in the codec guide. They do not
  add general codec negotiation or support unknown future Frame builds.
- AV1 has been tested on an RTX 5090 and the Frame's Qualcomm hardware decoder.
  A 100 Mbps request at 90 Hz is the initial viewing setting; 200 Mbps failed the
  earlier fresh-frame rate test. H.264 currently uses a smaller packed image.
  Available controls are experiments, not a guarantee of higher quality or full FPS.

## Project layout

```
SteamLinkVRSettings/   WinUI 3 app (views, view models, services)
  Frame/               Steam Frame runtime, connection, patcher, probe and diagnostics services
Native/                NVENC encoder probe (C++), its tests and vendored MinHook/NVIDIA headers
Frame/AV1/             Supervised codec companion, Frame decoder port and test/build tools
Tests/                 xUnit test projects
docs/                  User and developer guides
```

Third-party licenses for MinHook/HDE and the NVIDIA Video Codec SDK header live under
`Native/vendor` and are copied into the release package. The codec package also
includes CPython's license and the GPL-2.0-only decoder's corresponding source.
