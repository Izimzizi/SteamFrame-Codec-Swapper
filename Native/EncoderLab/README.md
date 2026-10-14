# Standalone HEVC rate and timing experiment

This Windows lab measures the PC encoder without SteamVR, a headset, or network
traffic. It uses the public NVENC 11.1 API directly and creates its own D3D11 device
and encoder session. It does not install hooks, edit settings, or flash firmware.
It is separate from the app and is not enabled by a streaming preset.

Build with Visual Studio C++ tools and a Windows SDK installed:

```powershell
./Native/EncoderLab/build.ps1
```

Output is in the ignored `artifacts/EncoderLab/` directory. Example experiments:

```powershell
# Does a simple image need the requested bits? Compare cbr with padded.
./artifacts/EncoderLab/hevc_probe.exe --mode cbr --scene flat --mbps 400 --csv artifacts/EncoderLab/flat.csv
./artifacts/EncoderLab/hevc_probe.exe --mode padded --scene flat --mbps 400 --csv artifacts/EncoderLab/padded.csv

# A difficult synthetic input, paced at the default 90 fps.
./artifacts/EncoderLab/hevc_probe.exe --mode cbr --scene alternating --mbps 450 --pace --frames 900 --csv artifacts/EncoderLab/detail.csv

# Compare single, quarter and full analysis at the same encoder request.
./artifacts/EncoderLab/hevc_probe.exe --mode cbr --scene alternating --multipass quarter --mbps 400 --csv artifacts/EncoderLab/quarter.csv

# Rate changes with and without a reset. A reset also forces an IDR.
./artifacts/EncoderLab/hevc_probe.exe --mode padded --step-mbps 300 --step-at 150 --csv artifacts/EncoderLab/step.csv
./artifacts/EncoderLab/hevc_probe.exe --mode padded --step-mbps 300 --step-at 150 --reset-at-step --csv artifacts/EncoderLab/reset.csv
```

Defaults are 2048×8192 P010, HEVC Main10, P2 ultra-low-latency tuning, 90 fps,
300 frames, 400 Mbps, CBR without filler, single pass, a flat neutral image,
one-frame VBV, no automatic periodic IDRs and no B frames. `--mode` accepts
`vbr`, `cbr`, or `padded`; only `padded` enables filler. `--scene alternating`
alternates two deterministic independent luma-noise textures with neutral chroma.
It is a stress input, not representative gameplay or a visual-quality comparison.
All modes leave picture-timing SEI off. Frame rate, dimensions, frame count and
target have bounded numeric arguments (`--fps`, `--width`, `--height`, `--frames`,
`--mbps`). `--output PATH` optionally saves only the first 60 frames as Annex B HEVC
for independent decoder validation. Use a new path to preserve earlier results.

Each run creates a fresh encoder; it does not attempt an unsupported dynamic
VBR/CBR mode change. A nonzero exit indicates an argument, API, timestamp, timer or
file error. No network or GUI is opened. The test runs a finite frame count.

## Reading the output

- `media_mbps` is encoded bytes divided by **nominal media duration**. Unpaced
  runs measure processing capacity; they are not evidence of delivery at 90 fps.
- `nonfiller_media_mbps` subtracts Annex B filler NALs (type 38), including their
  start codes. It still includes codec headers and is not a quality score.
- `wall_fps` includes host-side bookkeeping, waiting, filler classification and
  optional output. This serial, synchronous lab does not model SteamVR's
  overlapping GPU work, foveation maps or headset decoder.
- `encode_complete_*_ms` covers mapping, submission and the blocking wait for
  encoded output. It is not isolated GPU time. The CSV also records completion
  timestamps so cadence tails can be calculated separately from average fps.
- `accepted_*` describes parameters passed to a successful NVENC call, not an
  independent hardware configuration readback. Actual bitstream bytes are counted
  independently; a successful call does not guarantee exact rate adherence.
- `--pace` uses a high-resolution waitable timer and a final spin of at most
  approximately 150 microseconds in normal scheduling. It is a frame-submission
  experiment, not a packet pacer. Overdue frames are processed rather than dropped.

The original ordinary `sleep_until` experiment showed large completion gaps even
with an average near 90 fps. Current code uses the [Windows high-resolution timer](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw)
with relative [100-nanosecond due-time units](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-setwaitabletimerex).
It does not change system-wide timer resolution or process priority.

Measured findings and their limitations are in
[the encoder research notes](../../docs/encoder-and-codec-research.md).

## Live-path diagnostics

Two additional, separately built executables were used for the connected trials:

```powershell
./Native/EncoderLab/build_scene.ps1
./Native/EncoderLab/build_udp.ps1
```

`steamvr_scene.exe SECONDS DETAIL_PIXELS` submits a translating procedural texture
through the installed SteamVR compositor. Duration is 1–900 seconds; texture tile
size is 1–32 pixels. It temporarily keeps the PC compositor awake and restores
the previous power settings on normal completion, SteamVR quit, or a caught error.
Forced process termination can bypass that cleanup; it is a research workload,
not a persistent service or an app control.
It reports invalid poses and submit errors. It is a synthetic stress scene,
not stereo gameplay, and should not be used to assess comfort or visual quality.
It uses the default installed SteamVR path and does not change encoder settings.

`udp_probe.exe IPV4 MBPS PACKET_BYTES SECONDS BATCH_MICROSECONDS` sends sequence-numbered
synthetic datagrams to the explicitly supplied IPv4 address, port `39764`.
There is no default destination. Invalid arguments are rejected before socket
creation, and socket-bound or pacing-timer failures terminate the experiment.
Bounds are 10–650 Mbps, 256–1400 bytes, 1–15 nominal seconds and 100–12000 µs.
Use an address you control with a receiver you started there, and pause the video stream for an
isolated capacity comparison. There is no receiver installation or automatic
stream control inside the executable. The first eight bytes contain a little-endian
64-bit sequence number, starting at zero; remaining bytes are synthetic.

The test reports actual sender elapsed time, send-call stalls and process CPU time.
An overloaded socket can extend the nominal duration; its send timeout is two
seconds. A receiver must count unique sequence numbers, check receive-buffer
errors and divide bytes by **actual elapsed time**, not nominal test duration.
Sender throughput alone cannot establish received throughput. No part of this
network test is evidence of video quality or decoder/display performance.

`python Native/EncoderLab/test_udp.py` checks argument rejection without traffic
and runs a one-second, 10 Mbps loopback receiver test. Build the UDP probe first.
This test uses only `127.0.0.1`; it does not contact a headset or change settings.

The local paired receiver/orchestration and raw results are preserved under the
ignored `artifacts/RadioTrace/` directory. These experiments are not app presets
and do not install packet pacing into VRLink.
