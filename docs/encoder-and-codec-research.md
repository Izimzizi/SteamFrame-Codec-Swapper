# Steam Frame encoder controls and codec research

Investigation: 2026-10-11. Read-only analysis of the PC driver, Frame client and Frame kernel decoder using IDA Pro 9.0 headless, public NVENC headers and live capability queries.

The practical next work is better control and measurement of the existing HEVC stream. AV1 is a larger project: the PC hardware can encode it, but the installed Frame hardware-decoder interface does not expose it, and the Frame streaming client explicitly selects HEVC. An AV1 dropdown or codec-GUID substitution would not produce a working stream.

## Evidence and provenance

Local investigation files are in `artifacts/CodecResearch/`. These include private local analysis copies and are not part of the app distribution.

| Component | Verified identity | Evidence |
| --- | --- | --- |
| Installed PC `driver_vrlink.dll` | SHA256 `34265101d56235e2f87e5b0e2f3fc2340ed1513a9b6d26f03c4fe12bafcbbc03` | `host-static-strings.json`, `host-provenance.json` |
| Frame `vrlink` client | SHA256 `31addef0c9553d3770b5570487699e96d70271d4073cd6c03b080fdee6ae2153` | `frame-inventory.json`; matches the binary used by the ARM64 IDA database |
| Frame `iris.ko` | SHA256 `7a5647a009d60068965a476a887c2c73a51bedcec22727d71f010e9165d64a86` | `iris-symbols.json`; copied from the running OS for analysis |
| Frame kernel | `6.18.0-ge66bc2ca6f8c` | `kernel-metadata.json` |
| Frame hardware | SM8650 / Snapdragon 8 Gen 3, Adreno 750 | `frame-inventory.json`, `frame-decoder-capabilities.json` |
| PC hardware | RTX 5090 | Local GPU query; the PC's name is not its GPU model |

There are 37 unique decompiled functions: 21 PC driver, 10 Frame client and 6 kernel-module functions. The PC database predates the installed bitrate patch. A read-only comparison against the installed PE verifies identical machine-code bytes for 19 of the 21 functions. The other two differ only at three byte positions associated with patched constants: `0x180A2278E`, `0x180A2278F` and `0x180A23A44`. Their cached decompilation must not be used as an exact description of the installed bitrate/level constants. Codec selection, API calls, resource management and control paths discussed below remain present; the exact differences are recorded in `host-provenance.json`.

Capability queries opened a separate V4L2 descriptor and enumerated metadata. They did not configure buffers, start a decoder, capture camera/video data, or modify the streaming session. Decoder control defaults from that separate descriptor are not the active stream's values.

## What the current pipeline actually does

### PC encoder

The encoder factory at `0x1809F5B80` selects NVENC for NVIDIA's GPU vendor ID and AMF for AMD's vendor ID when enabled. No Intel QSV or software encoder branch was found in this factory. Enabling AMF on this NVIDIA PC would not create a usable AMD encoder.

The NVENC implementation requests API 11.1 (`0x180A21B70`) and initializes HEVC with the P2 preset and ultra-low-latency tuning (`0x180A22570`). Its codec validation handles H.264 and HEVC, with no AV1 branch. The initialization and reset paths contain HEVC-specific configuration writes. The current implementation rejects 10-bit input for its H.264 path; this is a limitation of this path, not a claim about modern NVIDIA hardware.

The reset path at `0x180A234A0` selects VBR, disables multipass and spatial AQ, and enables a delta-QP map in the foveated mode. Valve modifies preset-derived configuration, so selecting another preset alone does not define the final policy.

`CodecSet` / `CodecSetOVR` are misleading names: their installed table contains **Classic, CBR, VBR, TEST**, which are rate-control modes, not video codecs. They are accessed through an internal link key/value store rather than ordinary public OpenVR settings (`0x180A24720`).

`StartEncodeData` at `0x180A23FF0` calls `NvEncReconfigureEncoder` before encoding each frame. It already has performance warnings for submission-to-start, reconfigure and encode delays. This is a useful place to measure stalls; it does not establish that reconfiguration causes the observed stuttering. Any eventual coalescing must compare the complete effective request and preserve reset, keyframe, resolution, bitrate and buffer changes.

### Frame decoder

The Frame client resolves the `video_decode` device and opens the Qualcomm V4L2 path (`0x33248`). `OutputSetup` at `0x32E38` explicitly supplies the **HEVC FourCC**, initially with 1536 × 6144 dimensions. Source-change handling determines the decoded layout later.

The client requests display-delay enable with delay **zero**. Therefore an additional decoder-delay reduction is not an obvious available switch. This is a configuration request, not proof that every requested control is accepted on every build.

The client requests 16 coded-input and 16 decoded-capture buffers. Those capacities do not mean 16 frames are queued or that reducing the counts would safely reduce latency. Capture setup (`0x32328`) handles 8/10-bit Qualcomm compressed surfaces, DMA-buffer allocation, stride and alignment. A replacement decoder must preserve the renderer's import and synchronization requirements.

The stuck detector (`0x31940`) asks for a new I-frame after **330 ms** without forward progress, rate-limits those requests to approximately 100 ms, and resets after **800 ms**. Its first log rounds the threshold to “300ms.” These are recovery controls, not compression-quality controls; making them more aggressive could create extra keyframe bursts.

## Additional controls worth implementing or investigating

“Candidate” means the field or path exists, not that its benefit has been established. Existing controls should retain requested/applied readbacks and reversible restoration. The most useful first additions are telemetry plus a narrowly bounded quality experiment.

| Priority | Candidate | Confirmed hook/setting | Purpose and limits |
| --- | --- | --- | --- |
| 1 | VBR quality target and maximum QP | Public API 11.1 `targetQuality`, `enableMaxQP`, `maxQP` | Investigate preserving detail when the stream undershoots its bandwidth target. They do not guarantee minimum useful bitrate or gaze-region quality; bandwidth/VBV limits and Valve's delta map still matter. Add actual-config readback first. |
| 1 | Reconfigure, encode and queue timing | `0x180A23FF0`, `0x180A24720`; public encoder callbacks | Add duration distributions and a rolling event report to distinguish encoder stalls, queue waits, network retries and decoder recovery. Needed before changing scheduling. |
| 2 | Separate peak bitrate and VBV budget | Public `maxBitRate`, `vbvBufferSize`, `vbvInitialDelay` | Current snapshot: average 200 Mbps, peak about 230 Mbps, VBV about 1.90 Mbit. Investigate burst control without multipass. A larger budget can permit bursts and additional latency; smaller can constrain quality. |
| 2 | Automatic-resolution floor/ceiling | `minStreamFormatWidth`, `maxStreamFormatWidth` at `0x1809EBBC0` | Foveated fallback bounds are 512–1344. These are unset locally; automatic width is currently off with fixed width 1536. A quality floor needs corresponding actual-dimension and performance feedback. |
| 2 | Encoder resource pool | `driver_vrlink.usePool` at `0x180A22570`, resource management at `0x180A22130` | Six base frame resources; enabling the optional pool selects a pool limit of six. Setting is currently unset and the code leaves that limit zero. Test allocation/queue jitter after reconnect; do not arbitrarily shrink the frame ring. |
| 3 | Sharpening shortcut and readback | `useSharpening`, `sharpeningStrength` at `0x1809EBBC0` | Strength is clamped to 0–1; current value 0.2. Already available in the general settings editor. This can change apparent crispness, but does not restore compression-lost detail. |
| 3 | Chroma quantization offsets | Public API 11.1 `cbQPIndexOffset`, `crQPIndexOffset` | Candidate for colored-edge/text experiments. Effective defaults and reconfigure acceptance still need capture; stronger chroma quality consumes the same shared budget. |
| 3 | Retry/FEC/sending profiles | `enableTimedRetry`, `forceBaselineVideoFEC`, `enableQoS`, `asyncSend`, `backoffRecoveryCoefficient` | Current readbacks: 1, false, true, false, 3.5 respectively. Add shortcuts only after tracing consumer semantics and associating changes with packet-loss/decode events. Turning retry/FEC down is not automatically an improvement. |
| Research | Foveation transition bias or region size | QP-map builder at `0x180A1F170`; packed gaze/region metadata | Constant bias is already supported. The builder uses geometry and protected entries; altering region size needs consistent host packing and receiver unpacking. Do not treat a radius slider as another QP offset. |
| Research | Spatial/temporal AQ | Public API fields; spatial AQ cleared during reset | Validate compatibility with Valve's external delta-QP map and measure the center independently. Do not silently replace gaze prioritization with generic AQ. |
| Research | Consistent preset selection / modern encoder features | Preset query, initialize and reconfigure paths | A preset experiment must use consistent configuration and GUIDs across these calls and account for Valve's overrides. P3 did not show a useful win in the earlier user trial. |

The public rate-control fields are documented in the vendored [`nvEncodeAPI11.h`](../Native/vendor/nvEncodeAPI11.h). NVIDIA also documents target-quality VBR as subject to a maximum bitrate; an ambitious target can remain unmet when that ceiling is too low. Thus a quality target is an experiment in resource allocation, not a hard minimum-quality guarantee. [NVIDIA NVENC guide](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/nvenc-video-encoder-api-prog-guide/index.html)

`qualitySharpeningThresholdMbit` is another discovered setting, with a fallback value of 100 in `0x180A1C070`. Its complete consumer/effect has not been recovered, so it should not yet be presented as a understood quality knob. `foveationMode` returns a foveated predicate for values 0 and 2 (`0x1809E8B20`); labels such as “auto” or “forced” have not been established. Both settings are unset locally. OpenVR error 5 means missing value/default, not a valid observed zero.

For the RTX 5090, a modern backend also makes split-frame encoding worth checking. Explicit controls require newer API structures, and actual engine participation has not been measured here. It is not an automatic quality upgrade: NVIDIA describes a throughput/quality tradeoff. [NVIDIA split-frame encoding](https://developer.nvidia.com/blog/video-encoding-at-8k60-with-split-frame-encoding-and-nvidia-ada-lovelace-architecture/)

The earlier controlled quarter-resolution multipass experiment increased actual encoded rate but also recorded substantially more PC drops than single pass. Keep it disabled in the selected setup while testing alternatives; the trial measurements are in `artifacts/FrameQualityLab/report.md`.

## What is required for another codec

### AV1 hardware capability versus installed software

Qualcomm advertises an AV1 hardware decoder for Snapdragon 8 Gen 3. That is evidence of chip capability, not proof that the installed Frame OS exposes the needed mode or supports 1536 × 6144 at 120 Hz for AV1. [Qualcomm product brief](https://www.qualcomm.com/content/dam/qcomm-martech/dm-assets/documents/Snapdragon-8-Gen-3-SM8650-Q-AB-Product-Brief.pdf)

The PC's Blackwell generation also has AV1 encoding support. Its supported encoding dimensions, formats and low-latency configuration still need to be queried and validated for this particular prototype. [NVIDIA Blackwell Video Codec SDK](https://developer.nvidia.com/blog/nvidia-video-codec-sdk-13-0-powered-by-nvidia-blackwell/)

The actual `/dev/video22` decoder advertises only **H264, HEVC and VP90**. Its installed kernel module confirms the gap:

- `codec_data_sm8550` maps H264 → mask 1, HEVC → 2, VP90 → 4.
- `vdec_enum_fmt` at `0x6B30` enumerates the platform codec table/bitmap.
- `get_hfi_codec` at `0x11568` maps only these three masks to firmware codec identifiers; other masks return zero.
- `iris_hfi_session_set_codec` at `0xEB28` sends that mapping to the firmware.

Changing the table or FourCC alone would not implement AV1 reference-buffer, control, format and firmware-property handling. The existing firmware's AV1 support and interface have **not** been established; firmware replacement cannot yet be asserted necessary. A working hardware-decoder backend, using a validated firmware interface, is the first prerequisite.

The installed Vulkan query exposed no Vulkan Video decode/queue extensions as an alternative. Software AV1 decoders (`dav1d`, `aom`) are installed, but their presence does not establish viable latency, power or throughput for this stream.

### Changes needed on both ends

| Layer | Required work for AV1 |
| --- | --- |
| PC encoder | Introduce a capability-checked API 12+ backend or a complete version-translating bridge. Current API 11.1 structures and HEVC-specific writes cannot simply be relabeled AV1. |
| Encoder configuration | AV1 codec/profile/configuration, supported input formats, resource handling, rate control, keyframe recovery and sequence-header OBUs. Preserve low-latency behavior. |
| Transport and negotiation | Establish how codec capability/type is agreed and carried. Preserve Steam's launch/authentication, timestamps, frame boundaries, dongle transport and gaze metadata. The complete codec negotiation/wire schema has not been recovered. |
| Frame OS | Expose and validate an AV1 hardware-decoder backend. For the current Iris path this requires codec/platform/HFI integration, supported formats/controls/buffers and a verified firmware interface. |
| Frame client | Replace hardcoded HEVC selection with negotiated codec selection, sequence/header handling, capability rejection and reliable fallback. |
| Renderer and foveation | Preserve color/bit depth, compressed-surface imports, fences and packed-region reconstruction. Translate and validate the codec-specific QP map and protected entries. |
| App and helper | Make compatibility checks, accepted codec readback, controls, patching and restoration codec-aware. The current helper supports the old H.264/HEVC ABI paths. |

NVIDIA introduced AV1 encoding in Video Codec SDK 12.0. Modern NVENC treats AV1 configuration and sequence headers separately from H.264/HEVC. [NVIDIA AV1 introduction](https://developer.nvidia.com/blog/improving-video-quality-and-performance-with-av1-and-nvidia-ada-lovelace-architecture/), [NVENC guide](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/nvenc-video-encoder-api-prog-guide/index.html)

The current HEVC map has 32 × 32 entries, giving 9,216 entries at 1536 × 6144. Modern NVENC's AV1 interface uses a different grid/quantizer model; a 64 × 64 grid at that size would contain 2,304 entries. Reusing the HEVC map size, delta semantics or sentinel treatment would be unsafe. This is separate from preserving the geometry of the packed stereo/foveation surfaces. [NVENC guide](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/nvenc-video-encoder-api-prog-guide/index.html)

### Codec alternatives

| Codec | Evidence on this setup | Assessment |
| --- | --- | --- |
| HEVC | Working NVENC path and working Frame hardware decoder | Best near-term path for further quality/stability work. |
| AV1 | Supported by PC hardware; chip capability advertised; absent from installed Frame decoder interface | Worth researching, but blocked on a working Frame hardware backend before full integration. |
| H.264 | PC implementation recognizes it; Frame decoder exposes it | More accessible compatibility experiment. Receiver selection, configuration and map changes still required; this legacy host path rejects 10-bit input. No demonstrated quality improvement. |
| VP9 | Frame hardware decoder exposes it | No VP9 encoder backend found in this NVIDIA driver path; not an obvious next option. |

### Recommended development sequence

1. Locate the matching Frame kernel/firmware interface sources or supported vendor backend, then prove a small AV1 stream decodes through hardware independently of SteamVR. Matching source for this downstream kernel was not located in this investigation.
2. Validate supported dimensions, bit depth, surface imports and sustained throughput. Progress to the actual packed stream size and 120 Hz; chip playback specifications alone are insufficient.
3. Prototype a modern PC encoder with a deterministic test image, codec-specific map generation and no B-frame/lookahead latency additions.
4. Recover and implement codec agreement, sequence headers, frame boundaries and recovery in both clients, retaining HEVC fallback.
5. Compare HEVC and AV1 over the dedicated dongle using identical moving content, center-region reconstruction quality, frame-size bursts, encode/decode timing, late/drop events, retries and thermal behavior.
6. Expose a user-facing selector only after end-to-end capability detection, actual codec readback and restoration work.

For the smaller HEVC controls, start with additional effective-config/timing readbacks, then test one CQ/QP or burst-budget change at a time. Record center quality using known source frames; the existing frame-average QP is not a measurement of gaze-center reconstruction quality.

## State preserved and limits

This investigation did not patch installed binaries, restart SteamVR, toggle Wi-Fi, enable USB video or change the active profile. The selected setup remains the dedicated dongle, 120-Hz headset mode, 200-Mbps target, fixed width 1536, center −20 / outside +20, P2, Valve VBR and single pass. Temporary keep-awake overrides remain restored.

The latest encoder snapshot reports HEVC, 1536 × 6144, approximately 230-Mbps peak allowance, no filler and successful modified QP maps with zero protected-entry mismatches. Its frame-rate parameter is 118/1; that parameter varies and is not a headset mode readback. Likewise, the public `displayFrequency` setting reads 90 despite the previously verified 120-Hz headset mode. Requested settings must not be presented as measured display or delivered frame rate.

No active game scene was available for a new quality/stutter comparison during this research. New candidates have not been validated for subjective quality, sustained stability or future SteamVR builds. Use public API version/capability checks for new controls and explicitly reject unknown layouts; static addresses in this memo are evidence for this build, not compatibility guarantees for later builds.
