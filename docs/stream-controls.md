# Stream controls and foveation

The **Stream controls** tab sets how SteamVR encodes and sends video to the Frame.

## Bitrate and encoders

| Control | Setting | Notes |
| --- | --- | --- |
| Video target | `targetBandwidth` | 25–500 Mbps. A stock driver caps manual targets at 232 Mbps while wireless is present; the [driver patcher](driver-patcher.md) raises that. |
| Automatic bandwidth | `automaticBandwidth` | When on, SteamVR picks the target itself. |
| Allow AMD AMF encoder | `enableAmfEncoder` | Needs compatible AMD hardware. Takes effect after reconnecting or restarting SteamVR. |
| Allow the USB cable to join | `allowMultipleLinks` | Lets USB join a stream discovered over Wi-Fi. It does not force an exclusive wired route. |

The target is a request, not a guaranteed minimum. Variable bitrate can drop well below
it in simple scenes, and no CBR filler or artificial floor is added.

**Apply stream controls** leaves `recommendedBandwidthMbit` unchanged. Edit it under
[All streaming settings](settings-editor.md) if you need to.

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
| Where you are looking | −12 to +12 | Negative values improve quality in the gaze region. |
| Outside your gaze | −12 to +31 | Positive values trade peripheral detail for encoding budget. |

These are QP offsets, not resolution percentages or absolute codec QP values.
Peripheral offsets above +12 are aggressive, and +31 can make peripheral detail very coarse.

- **Quality focus** sets gaze −4 and periphery +8.
- **Valve defaults** turns the bias off.
- **Restart the monitor and reapply my bias after SteamVR restarts** keeps your bias
  active across SteamVR restarts while the app stays open.

Foveation needs the live encoder monitor and an NVIDIA (NVENC) stream. The bias lasts until
you click **Stop live monitor**, apply different settings, or SteamVR exits. If a future
driver changes its private foveation layout, bias is refused while video measurement keeps working.

## Advanced: `reqEncMode`

`reqEncMode` selects the transport encryption mode. It is not an NVENC quality preset;
leave it on `auto` for normal streaming.
