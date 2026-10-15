# Stream controls and foveation

The **Stream settings** page sets how SteamVR encodes and sends video to the Frame.
The editable [High quality HEVC preset](hevc-high-quality.md) loads a complete
400 Mbps target / 90 Hz setup, with encoder and network controls under Advanced.

## Bitrate and encoders

| Control | Setting | Notes |
| --- | --- | --- |
| Video target | `targetBandwidth` | 25–500 Mbps. A stock driver caps manual targets at 232 Mbps while wireless is present; the [driver patcher](driver-patcher.md) raises that. |
| Automatic bandwidth | `automaticBandwidth` | When on, SteamVR picks the target itself, starting from the rate below and adjusting from its own link measurements. The Overview's Wireless link card shows the estimate it arrives at. |
| Automatic starting bandwidth | `recommendedBandwidthMbit` | 25–500 Mbps, whole numbers. Where the automatic mode starts; Valve's default is 200. Applied with the other stream controls. |
| Display refresh rate | `steamvr/preferredRefreshRate` on PC and Frame | 90 or 120 Hz, including HEVC. Sets the host preference first, changes the Frame's display through paired SSH, then verifies active display Hz. Reconnects SteamVR if needed. Preserving an already active rate does not require SSH. |
| Allow AMD AMF encoder | `enableAmfEncoder` | Needs compatible AMD hardware. Takes effect after reconnecting or restarting SteamVR. |
| Allow additional streaming links | `allowMultipleLinks` | Permits available dedicated wireless, local-network and USB links. The researched transport duplicates datagrams across eligible links rather than striping video; this does not enable additional decoders. Confirm active links in the dashboard. |
| Stream format width | `streamFormatWidth` | 512–4096 pixels, in multiples of 32. A larger width may improve center detail while increasing encoder/decoder work; reconnecting may be needed. |
| Automatic stream width | `automaticStreamFormatWidth` | Allows Valve to choose the width. |
| NVENC rate control | Public NVENC parameter hook | Experimental Valve default, VBR or CBR without filler/padding. Requires the updated live monitor. |
| NVENC encoding passes | Public NVENC parameter hook | Experimental Valve default, single pass or quarter-resolution multipass. Extra work may add latency or stutter. |
| Advanced Supersample Filtering | `steamvr/allowSupersampleFiltering` | Toggle Valve's filtering setting with live readback, or save it for the next SteamVR session. |

The **Advanced Supersample Filtering** toggle is applied with the other stream
settings when you click Apply, then shows the value SteamVR accepted. When
SteamVR is stopped, it saves the override atomically.
The codec session must finish reconnecting before changing it.

Refresh has a separate **active display** readback. Selecting a rate only edits
the request; use **Apply** to change it. A real HEVC mode change requires paired
wireless SSH under Connection. An unreachable headset or mismatched display rate
is an error, not a successful change. Reconnecting may close a running game.
The app verifies the OpenVR display-frequency property rather than treating a
saved preference or measured encoder fps as proof of the display mode. Retune a
benchmark preset after changing Hz: 120 Hz gives each frame less time than 90 Hz.

The target is a request, not a guaranteed minimum. Variable bitrate can drop well below
it in simple scenes. CBR aims at the target; it does not guarantee a useful minimum.
No filler or artificial floor is added. The controls preserve Valve's preset, frame
rate, GOP, reference frames, lookahead and VBV settings.

An explicit policy change briefly nudges the freshly read live target by 1 Mbps for
400 ms to request a driver reconfigure, then restores it only if another control has
not changed it. The dashboard confirms the last configuration NVENC accepted.
If the stream is inactive or its public ABI is incompatible, reconnecting or updating
the helper may be necessary. Changing helper versions requires a SteamVR restart.
Preset selection is separate under [Advanced controls](advanced-controls.md) and
requires reconnecting; ordinary Stream settings preserve it.

**Stop live monitor** clears the requested encoder policy and requests restoration
before removing hooks. If an active encoder has not accepted Valve's original policy,
restoration hooks stay installed and the app asks you to reconnect and retry Stop.
Closing the app leaves the selected quality/policy in the current SteamVR session;
SteamVR exit removes the helper.

## Wireless advisor and presets

The **Wireless advisor** card repeats the Overview recommendation: one manual target with the
first reason, and **Use N Mbps** to load it (automatic bitrate is switched off in the editor).
Apply activates it. The advice combines SteamVR's link estimate and capacity histogram, held
frames, bad-link step-downs, headset loss and latency, the dongle's radio signal and PHY rate,
its USB speed and the driver ceiling. It is a reading of measurements, not a quality guarantee;
the [capacity sweep](live-dashboard.md#capacity-sweep) on Overview measures the limit directly
while you play, and [Auto tune](auto-tune.md) compares complete presets.

**Balanced wireless (stock driver)** is a built-in preset for an unpatched driver: a fixed
200 Mbps target in place of Valve's automatic mode, 90 Hz, 1536-pixel width, 10-bit, dongle-only
links and Valve's encoder defaults. It is a starting point the advisor, sweep and Auto tune raise from.

## How changes are saved

- **SteamVR running:** values go through SteamVR's live settings API and are read back to
  confirm they were accepted.
- **SteamVR stopped:** `steamvr.vrsettings` is rewritten atomically. The previous revision is
  kept as `steamvr.vrsettings.SteamLinkVRSettings.bak`, and the very first save also keeps
  your untouched original as `steamvr.vrsettings.SteamLinkVRSettings.original.bak`.

Unapplied edits survive switching tabs. Controls you haven't touched follow the live
target, so applying foveation never reintroduces an older saved bitrate.

## Foveation bias

Steam Frame streams with a gaze-tracked quality map. **Custom foveation bias** adds
offsets on top of Valve's own map; the moving gaze region, masks and timing tiles are preserved.

| Offset | Range | Effect |
| --- | --- | --- |
| Where you are looking | −20 to +12 | Negative values improve quality in the gaze region. |
| Outside your gaze | −12 to +31 | Positive values trade peripheral detail for encoding budget. |

These are QP offsets, not resolution percentages or absolute codec QP values.
Peripheral offsets above +12 are aggressive, and +31 can make peripheral detail very coarse.

- **Quality focus** sets gaze −4 and periphery +8.
- **Valve defaults** turns the bias off.
- **Restart the monitor and reapply my foveation, encoding policy and advanced requests after SteamVR restarts** keeps your selected bias and requests
  active across SteamVR restarts while the app stays open.

Foveation needs the live encoder monitor and an NVIDIA (NVENC) stream. The bias lasts until
you click **Stop live monitor**, apply different settings, or SteamVR exits. If a future
driver changes its private foveation layout, bias is refused while video measurement keeps working.

## Temporary unattended diagnostics

**Keep SteamVR awake for an unattended test** sets the SteamVR `power` section's
standby pause to false and screen timeout to 3600 seconds. It saves original values
before writing them, restores them on disable or normal app close, and recovers an
app-created lease after a crash when SteamVR is available. A newer different value
is retained. Failed restoration keeps the recovery file and allows normal app close.
The app records restoration as pending and retries when its next OpenVR connection
is available; a running `vrserver` can still reject OpenVR during a headset disconnect.
This does not change headset Wi-Fi, privacy sensors, or other scripts' backups.

## Advanced: `reqEncMode`

`reqEncMode` selects the transport encryption mode. It is not an NVENC quality preset;
leave it on `auto` for normal streaming.
