# HEVC presets

Select a built-in under **Stream settings**: **Tuned HEVC** uses a smaller stream,
**High quality HEVC** uses 2048-pixel width, and **Lower-latency HEVC** retains that
width with a smaller data budget and single-pass encoding. This fills the editor;
use Apply after reviewing the values. Loading the preset does not change the stream.
Every setting remains editable, and **Save preset…** saves your own version without
replacing the built-in. Existing personal presets remain available.

## Bundled choices

The five patched presets below request 90 Hz and 10-bit HEVC. Tuned HEVC uses
1664-pixel stream width; the other four use 2048. They
disable automatic bitrate/width selection, retain Valve's NVENC preset, VBV and
AQ policy, and leave the GOP-consistency/keyframe-ratio overrides unset. Each
includes the compatible bitrate-ceiling override and Reapply after restart.

| Preset | Target / encoding passes | Quality and sending | Intended use |
| --- | --- | --- | --- |
| Tuned HEVC | 400 Mbps / CBR, quarter-resolution multipass | 1664 width; center −20 / outside gaze −12; synchronous sending | Current playable setup, with stronger compression quality at a lower stream width |
| Smooth HEVC | 300 Mbps / VBR, single pass | Valve gaze quality; synchronous sending | Fallback based on the smooth baseline, with less encoding work than multipass |
| High quality HEVC | 400 Mbps / CBR, quarter-resolution multipass | Center −2 / outside gaze −6; asynchronous sending | The measured high-quality setup |
| Lower-latency HEVC | 200 Mbps / CBR, single pass | Same width, 10-bit and gaze offsets as HQ; synchronous sending | Lower measured delay with more compression; visual equivalence is unverified |
| Full-resolution HEVC — Experimental | 400 Mbps / CBR, full-resolution multipass | Same as High quality HEVC | Compare the analysis pass alone; quality/bitrate gains remain unverified and frame timing can worsen |

Selecting a preset fills the editor; Apply activates it. All controls remain
editable. Smooth HEVC explicitly requests the VBR/single-pass behavior observed
in the baseline instead of depending on the driver's current defaults. The
measurements below identify which setup was observed. Full-resolution multipass
has not demonstrated a quality or throughput gain.

**Balanced wireless (stock driver)** is a separate 200 Mbps / 1536-pixel / 90 Hz
option using Valve encoder defaults. It is not the automatic Valve-default
reference used in the latency comparisons below.

## Latency comparison — October 15, 2026

The investigation captured 35 runs on one RTX 5090, Steam Frame and wireless
environment, using a moving test scene at 90 Hz. The metric is **PC submit to
headset decode completion**, not motion-to-photon or isolated decoder hardware
time. Each trial used a fresh streaming session and 15-second warmup. Screens
lasted 36 seconds; repeated confirmations and diagnostic controls lasted 60 seconds.

| Setup | Median | p95 | Image tradeoff |
| --- | ---: | ---: | --- |
| Fresh Valve defaults, automatic 1152×4608 stream in this test | 13.40 ms | 17.33 ms | Lower resolution than HQ; automatic width is not always 1152 |
| High quality HEVC, 2048×8192, 400 Mbps target | 29.65 ms | 36.88 ms | Quality reference |
| Lower-latency HEVC, same geometry, 200 Mbps target, single pass, sync send | 25.11 ms | 30.10 ms | More compression and no multipass analysis; equal visual quality not established |

The candidate was tested in HQ/candidate/candidate/HQ order. Its two median
results were 24.35 and 25.96 ms, versus 29.67 and 29.62 ms for HQ. The table pools
frame distributions within each repeated setup. The Valve reference pools two
subsequent diagnostic controls (13.01 and 13.95 ms); it is a lower-resolution
reference, not an equal-picture comparison. Actual candidate video averaged
about 153 Mbps versus HQ's 204 Mbps; targets are not guaranteed output rates.

The controlled comparisons identified these settings-level costs:

- **Stream geometry was the largest isolated effect.** At the same 150 Mbps
  target, P2 and quarter-resolution multipass, changing only width from 1152 to
  2048 increased median delay from 14.60 to 24.43 ms. Encoder completion and
  receiver turnaround both increased, even though the larger stream sent fewer
  actual bits. At a 200 Mbps target, the width comparison again added about 10 ms.
- **A lower bitrate target reduced delay while preserving geometry.** At 2048
  pixels and quarter-resolution multipass, the 150 Mbps diagnostic measured
  24.43 ms. This increases compression; scene and session variation affect the
  result. The new preset uses the less aggressive 200 Mbps candidate confirmed
  above.
- **Multipass added a smaller cost.** At 2048 pixels and 150 Mbps, single pass
  measured 23.23 ms versus 24.43 ms for quarter-resolution multipass. At the
  400 Mbps target, the single-pass screen was roughly 1 ms below HQ controls.
- **The driver/HEVC-level patch did not explain the gap.** At matched 2048-pixel
  geometry and a 200 Mbps target, stock level 6.1 measured 27.60 ms and patched
  level 6.2 measured 27.65 ms. This compares the whole patch, including cap
  behavior; it is not a level-only experiment.
- **Sending mode, VBR, encoder-start scheduling, gaze defaults and additional
  links did not produce a large improvement in their individual screens.** P1
  reduced encoder time without a comparable reduction in total delay, so it
  was not adopted in the new preset. These short screens do not establish that
  the controls have no effect on other setups.

With 1152 pixels, 150 Mbps and single pass, the HQ extras still enabled measured
12.67 ms, close to the fresh Valve controls. The 2048-pixel trials commonly had
two submitted frames awaiting completion feedback while the smaller trials had
one. This measures pipeline occupancy, not the hardware queue length. Steady
90 FPS therefore did not establish low latency. Stage medians must not be added
to reconstruct total latency; their distributions overlap and differ.

No blind visual comparison or perceptual image score was captured. The lower
width screens did not use Tuned HEVC's −20 / −12 offsets, so they do not measure
that exact preset. Existing presets retain their settings, and the new option
must be loaded and applied explicitly. Auto tune measures delivery hitches and
configuration preferences; its score does not measure this end-to-end delay.

## Tuned HEVC settings

This preset saves the restored HEVC configuration used before the latency comparison.
Compared with High quality HEVC below, it changes stream width to **1664**, center
quality offset to **−20**, outside-gaze offset to **−12**, and asynchronous sending
to **off**. It retains the 400 Mbps target, 90 Hz preference, 10-bit request,
CBR without padding and quarter-resolution multipass. Other encoder overrides
are unset, and loading the preset retains the same editable controls and Apply flow.

An earlier read-only current-scene observation measured **261.18 Mbps of encoded
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
