# Experimental codec modes

The codec selector in **Stream settings** switches the PC encoder and
Frame decoder together. Selecting a dropdown item alone does not change the stream.
Use **Apply and reconnect** to apply the selected mode. Switching restarts SteamVR and
reconnects the Frame; a running game may close.

For the current native HEVC tuning, individual options and measured video rates,
see the [high-quality HEVC guide](hevc-high-quality.md).

## Supported setup

The codec-enabled portable release includes the companion's isolated Python
runtime, the PC encoder adapter and the experimental AV1 decoder. It does not
require a PC Python installation or a manually patched headset.

The current experiment requires:

- Windows 11 x64 with SteamVR and a NVIDIA GPU whose NVENC supports the selected
  codec. AV1 was tested on an RTX 5090; older GPUs without AV1 encoding cannot use it.
- Steam Frame kernel **`6.18.0-ge66bc2ca6f8c`** and the stock ARM64 VRLink client with
  SHA256 **`31addef0c9553d3770b5570487699e96d70271d4073cd6c03b080fdee6ae2153`**.
  Module, client and firmware fingerprints are checked before changing them.
- The dedicated Valve dongle, with the Frame reachable at **`10.35.78.1`**.
  Keep the Frame's Wi-Fi enabled for discovery and authentication.
- Developer Mode and a Frame user password set under **Developer → Set User Password**.
- Trusted SteamOS Devkit SSH pairing and Windows OpenSSH. Configure the private-key
  and known-hosts paths under **Connection** as described in the
  [connection guide](connection-setup.md).

An unknown kernel, client, firmware or unsupported encoder is rejected. This is
an exact-build prototype, not support for every future driver or headset update.
The ordinary HEVC controls and monitor remain separate from these experiments.

## Start a session

1. Extract the complete codec-enabled ZIP. Keep `CodecLab` beside the app executable.
2. Configure and save the trusted wireless SSH connection. Use `10.35.78.1` as the host.
3. Keep the Frame awake and connected to this PC through the dongle.
4. Open **Stream settings**, select **AV1**, **100 Mbps** and **90 Hz**
   for an initial viewing test.
5. Enter the Frame user password in the private password field and click **Apply and reconnect**.
6. Wait for the session status to become ready. Reconnect the Frame if needed, then
   reopen the game and check the dashboard's active codec and fresh output.

The password goes only through the owned companion process's standard input and
the authenticated SSH session. It is not saved in preferences, files or command
arguments. Switching modes within the same authenticated session needs no further
password entry.

## Modes and measurement

| Mode | Encoder/decoder | Current scope |
| --- | --- | --- |
| HEVC | Stock SteamVR encoder and Frame decoder | Restores the ordinary client/decoder path. |
| AV1 | NVIDIA NVENC and Frame Qualcomm hardware decoder through the experimental Iris module | 8-bit Main, packed image up to 1536 × 6144, 90 or 120 Hz. |
| H.264 | NVIDIA NVENC and the Frame's stock Qualcomm hardware decoder | Packed image 1024 × 4096, 90 or 120 Hz; the smaller image is an explicit current limitation. |

H.264 accepts 25–500 Mbps requests; AV1 retains its 25–200 Mbps range.
The field is the encoder's requested ceiling. SteamVR's own rate control can lower
the encoder below it after bad-link events, as it does for HEVC, instead of the
session pinning a rate the link cannot carry. Actual compressed output depends on
content, and no filler is added to force a minimum transmitted rate. Requests above
232 Mbps need the [bitrate patch](driver-patcher.md); Apply refuses them on a
verified original driver, because that driver caps SteamVR's target at 232 Mbps.
A 200 Mbps request did not maintain the desired new-frame rate in the earlier AV1
trial. Display refresh, encoded FPS and transmitted Mbps measure different things.

The live dashboard verifies AV1/H.264 from the loaded adapter's codec identity,
successful initialization and advancing encoded-output counters. The HEVC monitor
uses successful NVENC codec GUID readback. A requested mode or old saved session
is insufficient evidence: while reconnecting or without fresh output, the app
shows waiting, stopped or unknown rather than asserting a live codec.

Custom foveation and advanced HEVC hooks cannot be installed on top of the paired
codec adapter. Valve's native foveation remains part of the stream. The provisional
quality-map conversion is not a calibrated comparison of HEVC and AV1 image quality.

## Restoration and recovery

Choose **HEVC** in Stream settings and apply to remove the temporary client mount, unload
the AV1 module if active, restore the stock decoder and restart ordinary HEVC.

The session also ends when SteamVR exits. The Frame and your HEVC stream settings are
restored, and SteamVR is left closed unless it was already started again. After 90
minutes the session no longer stops a running game: stock HEVC is restored the next time
SteamVR exits, or automatically shortly before the Frame administrator session's two-hour
limit (about 115 minutes after authentication), which restarts SteamVR. The session status
shows when the limit has been reached. Closing the app window does not itself end the
independent companion session.

The helper changes only temporary runtime state. It does not replace the installed
kernel, firmware, original client file or boot module files. Rebooting the Frame
uses the stock installed components. **Back to stock** additionally restores a verified
bitrate patch and removes the app's saved tuning overrides, saving the previous setup first.

A session writes its own bitrate, automatic bandwidth, link-joining and stream-width
values to SteamVR. Returning to HEVC puts back the values you had before the session,
including removing ones that were unset. If a session ends unexpectedly, the next session
still restores those original values, unless you changed them in the meantime. The display
refresh rate selected for the session remains configured on the PC and the Frame.

If startup fails, read the codec status before retrying. Stale or unverified
companion ownership blocks a second session. Use the restore action while the
service is reachable; after a lost connection, reconnect the Frame and confirm
stock HEVC before opening another codec session.

## Clean-package validation (October 12, 2026)

The portable ZIP was extracted into an isolated directory and tested using its bundled
runtime. Each experimental codec started from the verified stock Frame client and
`qcom-iris` decoder, with the PC driver restored to its original hash. H.264 and AV1
both produced visibly decoded moving test patterns over the dedicated dongle.
Closing each session restored the exact original Frame client and stock decoder.
A separate pristine PC driver copy, with no patch receipt or backup, passed the
500 Mbps patch and byte-for-byte restoration round trip.

At a 100 Mbps target and 90 Hz, the 41-second moving-pattern trials measured:

| Mode | New encoded frames/s | Encoded Mbps | Dongle Mbps | Reported dropped frames | Presentation timeouts |
| --- | ---: | ---: | ---: | ---: | ---: |
| H.264 | 89.99 | 72.95 | 91.92 | 0 | 1 |
| AV1 | 82.47 | 91.66 | 117.81 | 0 | 1 |

Display presents can include reprojection; zero reported drops does not establish
that every displayed frame was newly encoded. The AV1 stress trial did not sustain
90 new encoded frames per second. These checks establish functioning patch,
stream and recovery paths on the tested kernel/client, not compatibility with
unseen future builds or a sustained-performance guarantee.

## H.264 bitrate sweep (October 12, 2026)

The 200 Mbps software limit was extended to 500 Mbps for H.264 in the app,
preset validator, supervisor and native adapter. AV1's 200 Mbps limit is retained.
An older running supervisor is detected before sending an unsupported high-rate
request; restore HEVC and start a new session to use the updated companion.

Same moving diagnostic pattern, NVIDIA hardware encoding, stock Frame hardware
decoding, dedicated dongle, 1024 × 4096 packed image and 90 Hz:

| Requested Mbps | Actual encoded Mbps | Encoded fps | Reported dropped frames | Presentation timeouts |
| ---: | ---: | ---: | ---: | ---: |
| 200 | 148.5 | 89.88 | 0 | 0 |
| 225 | 166.0 | 89.73 | 0 | 0 |
| 250 | 184.6 | 89.59 | 0 | 3 |
| 300 | 211.4 | 85.54 | 0 | 51 |
| 500 | 56.5 | 14.95 | 1 | 780 |

The first four observations lasted about 41–42 seconds; the failing 500 Mbps
observation lasted 20.6 seconds and recorded 50 PC stream recovery requests.
Receiver retry, missing-buffer and decoder queue-failure counters were zero in
the observed windows. Zero reported dropped frames alone does not establish smooth
streaming: presentation timeouts and fresh encoded cadence are separate evidence.

225 Mbps was the highest clean **short** trial on this setup, producing about
166 Mbps of video and 210 Mbps of dongle traffic. The longer confirmation test
was not run because the user needed the PC. This is a starting point for a viewing
test, not a guaranteed maximum for other content, hardware or sessions. High targets
can severely stall this stream; the 500 Mbps control range does not establish
successful 500 Mbps throughput. The H.264 packed image remains smaller than AV1's.

## H.264 multistream prototype (October 13, 2026)

The standalone prototype in `Frame/Multistream` and `Native/Multistream` implements
the architecture verified in a locally inspected Virtual Desktop client: independent
hardware decoder instances, shared frame timestamps, pairing before rendering, and
GPU textures for the decoded regions. That client has two- and four-decoder paths;
its Windows VR playback path waits for matching timestamps. Its `NetVideoClient`
uses a TCP stream and framed video/slice records. Its Android decoder setup requests
low latency and Qualcomm-specific extensions. These findings establish architecture,
not the complete proprietary encoder policy or proof that splitting alone enables
400 Mbps. Decompiled third-party code/binaries are not included in the prototype.

The prototype provides independent real-time NVENC sessions, bounded TCP or
optional paced UDP video transport, two/four stateful V4L2 decoders, timestamp
pairing, direct DMA-buffer imports through EGL, GPU NV12 conversion/composition,
and OpenVR per-eye game projection layers or a synthetic side-by-side overlay.
It uses newly written lab framing and the native SteamOS
interfaces. CAVLC, three reference frames, region geometry and encoder rate control
are explicit prototype choices, not asserted Virtual Desktop settings. The public
[V4L2 export API](https://www.kernel.org/doc/html/v4.9/media/uapi/v4l/vidioc-expbuf.html)
and [EGL DMA-buffer import extension](https://github.com/KhronosGroup/EGL-Registry/blob/main/extensions/EXT/EGL_EXT_image_dma_buf_import.txt)
provide the native buffer-sharing mechanism.

The tests exposed two independent problems in the original single-stream mode:

- At high H.264 levels, the stock client requested 16 capture buffers but the driver
  returned 18. IDA confirmed the client allocated only 16 backing surfaces; the
  receiver log repeatedly rejected index 16 with `EINVAL`. The adapter now signals
  the actual three-reference decoded-picture buffer limit in the bitstream VUI.
- In isolated local hardware tests, a single CAVLC stream decoded about 400 Mbps
  at 90 fps with no missing frames and a roughly 7 ms p99 decode time. CABAC was
  slower in the same capacity experiments. The adapter now selects CAVLC above a
  200 Mbps H.264 budget. This is measured on this device, not a universal entropy-mode rule.

The initial live prototype tests were also limited by the **PC dongle's USB bus**.
An actual USB device-tree report showed High-Speed/USB 2, despite the network
driver's saved setting saying USB 3. After reconnecting the dongle, another report
confirmed SuperSpeed/USB 3 at 5 Gbit/s. With the same two-stream workload, measured
delivery increased from approximately 237 Mbps to about 403 Mbps. Wi-Fi PHY rate
alone did not reveal this bottleneck.

After the USB correction, the DMA-buffer prototype measured:

| Independent streams | Duration | Delivered coded video | Matched/composed sets | Decode errors / queue drops | p99 composition | p99 interval between submissions |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 10 s | 403.1 Mbps | 900 / 900 | 0 / 0 | 6.1 ms | 20.7 ms |
| 4 | 30 s | 402.1 Mbps | 2,700 / 2,700 | 0 / 0 | 5.9 ms | 23.9 ms |

Both used a 465 Mbps aggregate encoder budget, CAVLC, a deterministic high-entropy
GPU scene, a 4096×1024 aggregate image, and 90 new encoded sets/s. No filler was
enabled. All first decoded regions matched FFmpeg exactly. Actual coded output was
about 402 Mbps; the budget was not counted as delivered data. Each completed set
was accepted by the headset's OpenVR overlay API. The final GPU composition was
captured and checked for region order and color conversion. Results are in local
`artifacts/Multistream/two-465-dmabuf/result.json` and
`artifacts/Multistream/four-465-dmabuf-30s/result.json`.
After adding expiration of incomplete frame sets, a final 10-second four-stream
run again passed at 402.7 Mbps, with all 900 sets composed, no missing/late/duplicate
sets or decoder errors, and exact first-frame pixel matches. Its p99 submission
interval was 20.4 ms; see `artifacts/Multistream/four-465-final/result.json`.

**Throughput passed; the stricter presentation-interval criterion did not.** These
short runs establish a working 400+ Mbps multistream encode/transport/decode/GPU
prototype, not Virtual Desktop-equivalent smoothness, panel presentation counts,
latency, image quality or thermal stability. These were the earlier synthetic TCP
runs. The prototype now captures a SteamVR game's stereo mirror textures and carries
pose, projection and gaze validity metadata. It does not replace VRLink's receiver;
the app's regular H.264 mode remains single stream.
Build and run instructions are in the [Frame tools README](../Frame/AV1/README.md#h264-multistream-prototype).

### Game integration and pacing follow-up

Actual SteamVR Home capture uses four 1024×2048 regions, matching frame identities
before composition. A low-bandwidth native Valve connection carries tracking,
audio and controllers. `game_session.py` manages the temporary connection and
restoration; visible projection is opt-in. This is still a standalone prototype.

The reported inverse-moving duplicate image exposed a projection transform bug:
the Frame projection API consumes the world-to-eye view matrix. The receiver now
inverts the captured eye pose, verifies rigid transforms, and cycles three output
textures so rendering cannot overwrite the currently shown surface. A rotated,
translated camera regression test verifies the inversion. Headset confirmation
is still required before calling the visual issue resolved.

Decoded surface retention is asynchronous, and a three-frame playout buffer
separates arrival jitter from submission timing. Bootstrap frame zero initializes
the hardware decoders and is intentionally omitted from display. Game deadlines
use compositor frame identities. Queue drops, missed partners, clock resets and
composition/submission intervals remain explicit failure metrics.

One isolated 10-second UDP synthetic run passed the stricter criterion:
402.431 Mbps of actual video, 900 matched groups, 899 displayed after bootstrap,
89.9998 composed fps, 11.889 ms p99 submission interval, no queue drops/clock
resets/decoder errors, and exact first-frame FFmpeg matches. This used the earlier
eight-data-plus-one-parity transport and 4096×1024 geometry. Evidence:
`artifacts/Multistream/udp-segmented-465-isolated/result.json`.

**Smooth 400+ Mbps Home streaming has not passed.** A 421.059 Mbps TCP game run
decoded all 900 groups but dropped 103 presentation sets and reached a 55.8 ms p99
interval (`home-550-sixqueue`). A later 30-second UDP test stopped after about
18 seconds with a 121 ms send stall (`home-udp-535-30s`). Instrumented follow-ups
measured 125–132 ms inside a Windows UDP send call. Registering the capture/send
threads with MMCSS did not eliminate these stalls. Current experiments use
sixteen-data-plus-one-parity recovery and a 10.5 ms packet schedule, but have not
yet established sustained game-stream smoothness or panel scanout timing.

The prototype's gaze quality plateau covers 70% of each eye's normalized width,
with a soft transition. The tested public SteamVR gaze API returned invalid gaze,
so these runs used uniform quality throughout both eyes. The native Valve stream's
packed foveation geometry has not been modified.

After restoring the app's regular H.264 mode at a 400 Mbps budget and 90 Hz, a
61.6-second moving-noise scene measured 89.98 encoded fps, 274.8 Mbps of actual
encoded video, and zero encoder errors. The dongle received 365.0 Mbps including
transport overhead, with no interface drops/errors. The compositor reported one
dropped frame and 266 presentation timeouts; this is not a clean smoothness pass.
The app's budget setting therefore must not be described as verified 400 Mbps of
game video. This separate run is saved in
`artifacts/AV1Lab/steamvr-soak-h264400-usb3-noise-20261013.json`.

## Source and licenses

`CodecLab/Assets/decoder-source.zip` contains the corresponding source for the
bundled GPL-2.0-only Iris decoder module. The codec package includes its source
provenance, port changes, license and build instructions. The PC adapter and
supervisor source are in `Native/AV1Lab` and `Frame/AV1` in this repository.
The bundled CPython runtime has its own license under `CodecLab/runtime`.

Valve's original VRLink binary and Qualcomm firmware are not distributed. The
client patch is generated from the verified original file on the user's Frame.
