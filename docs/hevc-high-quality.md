# HEVC presets

Select **Stream settings → Tuned HEVC (built-in)** for our current tuned setup,
or **High quality HEVC (built-in)** for the earlier 2048-width setup. This fills the editor;
use Apply after reviewing the values. Loading the preset does not change the stream.
Every setting remains editable, and **Save preset…** saves your own version without
replacing the built-in. Existing personal presets remain available.

## Bundled choices

All four presets request 90 Hz and 10-bit HEVC. Tuned HEVC uses 1664-pixel stream
width; the other three use 2048. They
disable automatic bitrate/width selection, retain Valve's NVENC preset, VBV and
AQ policy, and leave the GOP-consistency/keyframe-ratio overrides unset. Each
includes the compatible bitrate-ceiling override and Reapply after restart.

| Preset | Target / encoding passes | Quality and sending | Intended use |
| --- | --- | --- | --- |
| Tuned HEVC | 400 Mbps / CBR, quarter-resolution multipass | 1664 width; center −20 / outside gaze −12; synchronous sending | Current playable setup, with stronger compression quality at a lower stream width |
| Smooth HEVC | 300 Mbps / VBR, single pass | Valve gaze quality; synchronous sending | Fallback based on the smooth baseline, with less encoding work than multipass |
| High quality HEVC | 400 Mbps / CBR, quarter-resolution multipass | Center −2 / outside gaze −6; asynchronous sending | The measured high-quality setup |
| Full-resolution HEVC — Experimental | 400 Mbps / CBR, full-resolution multipass | Same as High quality HEVC | Compare the analysis pass alone; quality/bitrate gains remain unverified and frame timing can worsen |

Selecting a preset fills the editor; Apply activates it. All controls remain
editable. Smooth HEVC explicitly requests the VBR/single-pass behavior observed
in the baseline instead of depending on the driver's current defaults. The
measurements below identify which setup was observed. Full-resolution multipass
has not demonstrated a quality or throughput gain.

## Tuned HEVC settings

This preset saves the restored HEVC configuration used during the latest research.
Compared with High quality HEVC below, it changes stream width to **1664**, center
quality offset to **−20**, outside-gaze offset to **−12**, and asynchronous sending
to **off**. It retains the 400 Mbps target, 90 Hz preference, 10-bit request,
CBR without padding and quarter-resolution multipass. Other encoder overrides
are unset, and loading the preset retains the same editable controls and Apply flow.

The latest read-only current-scene observation measured **261.18 Mbps of encoded
video at 89.72 fps over 8.80 seconds**, with zero encoder or output-lock errors.
The earlier restored reference measured 240.01 Mbps at 89.82 fps over 20.46 seconds.
Neither is a sustained game or physical-display smoothness test. Bitrate can still
vary because of content and SteamVR's adaptive budget. The lower width trades
spatial detail for stronger compression quality; this is not evidence of a
universal improvement over the earlier preset or steady 350–400 Mbps.

The experimental native H.264 receiver, radio changes and temporary testing
controls are not part of this preset.

## High quality HEVC settings

| Setting | Value | Where to edit |
| --- | --- | --- |
| Codec / bitrate target / refresh preference | HEVC / 400 Mbps / 90 Hz | Stream settings |
| Automatic bitrate / width | Off / off | Stream settings |
| Stream width | 2048 pixels | Stream settings → Picture quality |
| Custom gaze quality | On; center −2, outside gaze −6 | Stream settings → Picture quality |
| Advanced Supersample Filtering | Off | Stream settings → Picture quality |
| Additional video connections / AMD encoder | Off / off | Stream settings → More stream options |
| Rate control / encoding passes | CBR without padding / quarter-resolution multipass | Advanced → Encoding method |
| Reapply encoder and gaze settings | On | Advanced → Encoding method |
| NVENC preset / buffer / AQ overrides | Preserve Valve values; P2 was observed in testing | Advanced → Encoder quality and buffering |
| 10-bit request / asynchronous sending | On / on | Advanced → Driver and network settings |
| Sharpening / strength / QoS | On / 0.2 / on | Advanced → Driver and network settings |
| Recommended bandwidth | 400 Mbps | Additional settings |
| Debug region coloring / advanced graphs | Off / on | Additional settings |
| Driver bitrate ceiling | Compatible 500 Mbps override | Patches & Recovery |

Advanced rows have an **Override** checkbox. Clear it to restore Valve's value.
Other available controls include CQ, maximum QP, peak bitrate, VBV, chroma/AQ,
resource pooling, asynchronous encoder start, retries/FEC, backoff and width bounds.
Temporary keep-awake behavior is under **Diagnostics → Unattended testing** and
is not part of the preset.

When a complete preset is loaded, Apply on either Stream settings or Advanced
applies that complete edited setup. Refresh requests SteamVR's display preference;
refresh, sender and encoder initialization changes can require a reconnect.
Installing the bitrate override restarts SteamVR when required and may close a game.
Unsupported driver layouts are refused.

The gaze offsets change compression quality, not the physical gaze-region size.
Multipass changes encoder analysis. Native HEVC still uses one decoder; the
multistream research prototype is separate from the app's game-streaming path.

## Multipass and rate allocation experiments

In **Advanced → Encoding method**, select Valve default, single pass,
quarter-resolution multipass or **full-resolution multipass (experimental)**.
Both multipass modes encode the final image at full resolution. The choice changes
the resolution used by the first analysis pass; it does not change stream width or
decoder count. NVENC exposes these two multipass modes, not an arbitrary pass count.
Full-resolution analysis adds work and is not guaranteed to improve on quarter
resolution for moving VR content. Compare actual video rate and encoder completion
time in Diagnostics, alongside how the headset feels. The built-in High quality HEVC
preset retains the measured quarter-resolution setup.

**Advanced → Encoder quality and buffering** also offers:

- **Consistent bitrate between GOPs**: requests reduced bitrate variation between
  groups of pictures. It does not alter keyframe frequency or enforce a minimum rate.
- **Keyframe bit ratio (I / P)**: an experimental 1–4 ratio, documented for CBR and
  a single-frame VBV. A ratio of 1 requests similar I/P budgets; larger values allow
  larger keyframes. Other rate-control/buffer configurations may ignore this field.
  A single-frame VBV is the encoder's accepted Mbps divided by fps, in Mbit. For
  example, 400 Mbps at 90 fps is about 4.44 Mbit. SteamVR can change the encoder
  budget, so a fixed VBV override does not automatically stay one frame long.

These two overrides need a new encoder session. Enable Override, apply, then reconnect
with Reapply enabled. When updating the native helper, restart SteamVR first.
Clear Override and reconnect to restore the driver's values. Saved personal presets
include the new mode and controls. Requested, pending, rejected and last accepted
values remain distinct in Diagnostics; successful submission is not a guarantee of
picture quality or higher Mbps. Existing CQ, QP, VBV, AQ and P1–P7 controls remain
independently adjustable.

See the [NVIDIA encoder programming guide](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/)
for multipass tradeoffs, and the vendored NVENC declarations for
`strictGOPTarget` and `lowDelayKeyFrameScale`.

## What was measured

On the tested NVIDIA/Steam Frame setup, the accepted HEVC image was 2048×8192 at
10-bit and 90 Hz. A 270-second Galactic Racer observation after startup averaged
274.71 Mbps of coded video and 89.70 encoded fps. Headset interface traffic averaged
369.94 Mbps including overhead. There were zero encoder errors and zero reported
Frame dropped frames, but 21 Frame presentation timeouts and 49 PC application
drops. Racing versus menus was not independently classified.

These are observations, not a guarantee of sustained 400 Mbps video or perfect
frame pacing. Content, rendering load and link conditions affect the actual rate.
Equal-workload HEVC decoder experiments found no useful gain from two decoders;
four were slower. The preset keeps the stock single-decoder path.

Check **Connection** for the dongle's actual negotiated USB speed. USB 2 can limit
high-bitrate streaming; USB 3 signaling, Wi-Fi speed and actual coded bitrate are
different measurements. Unknown or stale USB results are shown explicitly.
