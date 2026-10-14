# HEVC tuning and decoder comparison

These tools measure the existing Valve HEVC stream and run a separate hardware
decoder capacity experiment. They do not replace VRLink or claim Virtual Desktop
compatibility. Generated clips and measurements go under ignored `artifacts/HEVC/`.

## Selected profile, October 13, 2026

The user reported that the image looked great in SteamVR Home, then selected
Galactic Racer for gameplay validation:

- HEVC Main10, 90 Hz, 2048-pixel stream width (accepted packed frame 2048×8192).
- 400 Mbps target, automatic bandwidth/width disabled, one dedicated dongle link.
- CBR without filler or picture-timing padding, P2, quarter-resolution multipass.
- Asynchronous sending enabled.
- Gaze QP offset −2, peripheral offset −6, with Valve's protected entries preserved.
  These request better peripheral quantization; they **do not enlarge the physical
  gaze region** or change host/receiver packing geometry.

The app includes **High quality HEVC (built-in)** in **Stream settings**. Selecting
it loads the setup into editable controls; applying is a separate action. It
requests 10-bit explicitly and otherwise preserves Valve's NVENC preset (observed
P2) and buffer policy. It includes the compatible 500 Mbps ceiling patch when needed.
The built-in is available on a fresh install and remains separate from saved presets.

Earlier locally saved presets: `HEVC 400 Mbps - 90 Hz - peripheral detail` and
`HEVC 300 Mbps - 90 Hz - smooth baseline`. The fallback uses Valve VBR/single-pass,
no custom quality map and synchronous sending. Pairing and existing presets remain.
The candidate is not a claim of uninterrupted 400 Mbps coded-video delivery.

Every tuning request in this setup has an editor:

| Setting | App location |
| --- | --- |
| Codec, target bitrate, automatic bitrate, 90/120 Hz preference | Stream settings |
| Width, automatic width, gaze/peripheral offsets, filtering | Stream settings → Picture quality |
| Encoder choice and additional wireless links | Stream settings → More stream options |
| CBR/VBR, single/quarter multipass, reapply on restart | Advanced → Encoding method |
| NVENC P1–P7, CQ, QP, peak/VBV, chroma and AQ overrides | Advanced → Encoder quality and buffering |
| 10-bit, async send/start, pooling, sharpening, QoS, retries/FEC, backoff and width bounds | Advanced → Driver and network settings |
| Recommended bandwidth, diagnostic graph/color settings | Additional settings |
| 500 Mbps driver ceiling override | Patches & Recovery |
| Temporary keep-awake behavior | Diagnostics → Unattended testing |

Use Override on advanced rows to set a value; clear it to restore Valve's default.
Save preset stores the edited setup as a personal preset, including advanced
values. When a complete preset is loaded, Apply on either Stream settings or
Advanced applies that complete edited setup. Refresh is a SteamVR preference;
initialization changes may need a reconnect. The normal HEVC stream still has one
decoder. Experimental decoder-count trials remain in `decoder_compare.py`;
multipass is an encoder setting, not multiple decoder sessions.

## Native SteamVR Home measurements

All runs used full-resolution 10-bit HEVC:

| Target / policy | Seconds | Actual coded Mbps | Encoded fps | Frame presentation timeouts |
| --- | ---: | ---: | ---: | ---: |
| 300, stock VBR/single-pass | 120 | 184.68 | 89.90 | 0 |
| 300, CBR/single-pass | 60 | 201.19 | 89.96 | 19 |
| 400, CBR/single-pass | 60 | 230.48 | 89.86 | 48 |
| 500, CBR/single-pass | 60 | 256.89 | 89.80 | 83 |
| 500, CBR/quarter multipass | 60 | 304.75 | 89.88 | 45 |
| 500, CBR/quarter, asynchronous send | 60 | 275.88 | 89.92 | 7 |
| 500, above plus peripheral quality | 60 | 269.89 | 89.75 | 14 |
| 400, above plus peripheral quality | 120 | 271.11 | 89.70 | 10 |

All listed runs recorded zero encode/bitstream-lock errors and zero Frame
`dropped_frames`. The final run also recorded 27 PC compositor drops across about
10,800 presentations: average fps and the subjective report do not prove perfect
pacing. The 300 CBR run's 19 timeouts were in its first 10-second interval.
Head motion, content and startup state were not controlled; this is a trial record,
not a perceptual quality benchmark or causal proof for each setting. Autonomous
tests began with the headset off; the user later put it on. An app build overlapped
the end of the first asynchronous-send trial; the later 120-second run followed
all builds and unit tests.

The final run's headset interface averaged 367.04 Mbps including overhead, versus
271.11 Mbps of actual video. Neither interface traffic nor the target is coded
throughput. Valve can reduce its encoder budget during recovery even with automatic
bandwidth disabled. Quarter multipass added roughly 2 ms to initial mean completion
callback latency, which includes queue/lock wait rather than GPU execution alone.
Frame reprojection occurs normally on nearly every VRLink frame and is not a game
missed-frame percentage.

`home-p4-300-width2048.json` is marked invalid as a P4 trial: P2 remained accepted
and P4 stayed pending. `home-cbr300-stock2048.json` is incomplete and interrupted
by a helper rebuild/headset idle behavior. Use the `fixed` results above.

## Galactic Racer observation

A five-minute observation ran while Steam app 4078430 was active. Excluding the
first 30 seconds of startup, 270 seconds averaged **274.71 Mbps coded video**,
**89.70 encoded fps**, and **369.94 Mbps headset interface traffic**. There were
zero encode/lock errors and zero Frame `dropped_frames`, but 21 Frame presentation
timeouts and 49 PC application drops. Racing versus menus was not independently
classified. This does not establish perfect frame pacing or 400 Mbps actual video.

A separate overlapping PC compositor observation saw occasional application GPU
times up to 13.38 ms against an 11.11 ms 90 Hz budget. Its drops use a different
sample window and must not be added to the above totals.

## One versus multiple hardware decoders

`decoder_compare.py` calibrated real Main10 output near 300 Mbps, rejected filler
NALs and verified software decoding. It replayed 90-frame GOPs from Frame-local
storage for ten seconds at equal aggregate 2048×8192 image area:

| Decoder sessions | Actual aggregate source Mbps | Whole-frame equivalents/s |
| --- | ---: | ---: |
| 1 × 2048×8192 | 305.37 | 84.33 |
| 2 × 2048×4096 | 302.94 | about 84.35 |
| 4 × 2048×2048 | 300.09 | about 78.8 |

Every submitted frame decoded and drained without decoder, identity or empty-buffer
errors, but none met paced 90 Hz. Two sessions gave no meaningful gain and four
were slower, supporting one decoder for this HEVC path. This is difficult synthetic
content, not a ceiling for every HEVC workload. Wireless transport and display are
excluded. Main10 uses compressed Qualcomm QC10C capture; independent hardware pixel
verification is **not** claimed. Queue-inclusive p99 completion was about 105 ms
under this overload.

## Commands

```powershell
./Native/build.ps1
dotnet build Native/HevcTune/HevcTune.csproj -c Release -o artifacts/HEVC/tune
artifacts/HEVC/tune/HevcTune.exe --start
python Frame/HEVC/settings.py --target 400 --width 2048
python Frame/HEVC/measure.py --probe artifacts/HEVC/tune/HevcTune.exe --seconds 120 --output artifacts/HEVC/observation.json
artifacts/HEVC/tune/HevcTune.exe --timing 120 artifacts/HEVC/compositor.json
```

No-argument `HevcTune.exe` is read-only and reuses an installed hash-matching guarded
monitor. `--start` explicitly installs monitoring. `--apply REQUEST.json` changes
only supplied fields and verifies the accepted policy. `--arm REQUEST.json` starts
the monitor and prepares requests before Frame connection, refusing an active
encoder. Verify accepted output after connecting. Do not rebuild/deploy a different
helper while a measurement using it is running.

Selected encoder request:

```json
{"Advanced": {}, "Policy": {"RateControl": 2, "MultiPass": 2},
 "QualityEnabled": true, "CenterOffset": -2, "PeripheralOffset": -6}
```

`settings.py --async-send` / `--no-async-send` change the sender request through
public OpenVR settings; reconnect before comparing modes. Initialization-only
encoder changes need a truly new encoder, which Frame-only reconnects may not create.
The settings app already exposes these controls and can reapply them.

`settings.py --ten-bit` / `--no-ten-bit` similarly changes only the requested
driver setting. Verify the monitor's actual `BitDepth` after a fresh encoder
initialization; successful settings readback is not proof of an active codec change.

`measure.py` preserves partial data on failure and records actual video, callback
timing, whole-frame QP, PC application counters, Frame counters and interface bytes.
`--timing` polls the existing public compositor observer once per second for game
GPU/frame interval distributions without changing game images, focus or settings.

```powershell
python Frame/HEVC/decoder_compare.py --results artifacts/HEVC/decoder-300-2048 --mbps 300 --prepare-only
python Frame/HEVC/decoder_compare.py --results artifacts/HEVC/decoder-300-2048 --mbps 300 --run-prepared
```

The isolated comparison uses existing trusted Frame SSH pairing and local FFmpeg.
Set `FRAME_SSH_KEY` and `FRAME_SSH_KNOWN_HOSTS` to override the current user's
default pairing paths, or `STEAMVR_RUNTIME` for a nondefault PC SteamVR install.
`decoder_compare.py --ffmpeg PATH --ffprobe PATH` selects local codec tools.
It pauses the native Frame stream for bounded decoder tests and restarts it in
`finally`. Functional decode, deadline acceptance and untested pixel/display
correctness are separate result fields.

## Control fixes and validation

NVENC rate-control values are already bit flags (VBR=1, CBR=2). The monitor had
incorrectly treated them as bit positions. The regression fixture now uses the
real supported mask of 3. The monitor also tracks each encoder's last accepted
mode: repeated Valve VBR-shaped reconfigure inputs no longer reject an existing
CBR session. Hardware without dynamic mode changes retains its accepted mode until
a new initialization, and failed calls cannot replace that accepted state.

Six native tests and 61 managed core tests passed. The WinUI app and measurement
helpers built; the updated app/native helper were deployed locally.
