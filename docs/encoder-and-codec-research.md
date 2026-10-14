# Steam Frame encoder controls and codec research

Initial investigation: 2026-10-11. Read-only analysis of the PC driver, Frame client and Frame kernel decoder using IDA Pro 9.0 headless, public NVENC headers and live capability queries. The dated sections below describe their respective investigation snapshots, not a continuously updated live configuration.

The [2026-10-13 wireless transport investigation](#wireless-transport-and-bitrate-investigation-2026-10-13) traces the bitrate formula, decode acknowledgements, UDP scheduling, multiple links, FEC and retry behavior. For subsequent AV1 implementation work, see [AV1 implementation status](av1-implementation-status.md); the initial codec assessment below predates that work.

This research publication includes original notes and the standalone encoder lab.
The local app warning, maximum-QP lifecycle and trace-diagnostic changes discussed
below remain unreleased and are not part of this research-only source commit.

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

## Wireless transport and bitrate investigation (2026-10-13)

### Findings that explain a request higher than the delivered bitrate

A requested 350 Mbps does not go directly to the encoder unchanged. The researched stock driver applies a 232 Mbps ceiling when its dedicated-dongle predicate is true, retains latency throttling in manual mode, and uses variable bitrate encoding. These are separate mechanisms. The user's roughly 150 Mbps observation need not correspond to any one literal constant.

The most consequential new finding is that the acknowledgement used by the host's queue throttle is generated by **decode completion on the headset**. Network reception, repair delays, decoder backlog and feedback delivery can all affect that throttle. The host is protecting progress through the receiving pipeline, not merely estimating Wi-Fi capacity.

The current installed patch raises both researched ceiling constants to 500 Mbps. It does not remove the queue/ping factors, force the encoder to produce its entire budget, or reserve sufficient wireless capacity for repair overhead.

### Evidence and build scope

The private working material is in gitignored `artifacts/BitrateTrace/`: copied IDA databases, headless extraction scripts, individual decompilations, assembly excerpts and byte comparisons. This document contains independently written findings; proprietary binaries and decompiler output are not distributed with the repository.

| Evidence | Identity / validation |
| --- | --- |
| Installed Windows driver | SHA256 `34265101d56235e2f87e5b0e2f3fc2340ed1513a9b6d26f03c4fe12bafcbbc03`; image base `0x180000000` |
| Original ARM64 client analysis input | SHA256 `31addef0c9553d3770b5570487699e96d70271d4073cd6c03b080fdee6ae2153`; client addresses below are ELF virtual addresses |
| Host validation | `provenance.json`: 123 functions / 107,307 code bytes compared with the installed PE. 121 functions match exactly; two known initialization/HEVC-level patches differ at three code bytes. The budget, transport and FEC functions discussed here match. Original ceiling data and installed ceiling data are compared separately. |
| Client validation | `client/provenance.json`: 82 functions / 41,712 code bytes match the identified local ELF; 81 functions have recovered pseudocode and one was inspected in assembly. This establishes the identity of the analyzed client, not that an experimental client currently running on the headset has identical code. |

Critical values were checked against instructions where inferred decompiler types lost float returns or arguments. Addresses identify this build only. This investigation did not restart a stream, change settings, apply patches, or run an active throughput test.

### 1. Requested bandwidth to effective encoder budget

`0x180A1C070` reads `automaticBandwidth` and the float `targetBandwidth`. The manual float return/store is visible at `0x180A1C280–0x180A1C299`. `recommendedBandwidthMbit` is present in settings, but was not identified as the manual encoder-budget source in this path.

For manual mode, `0x180A1CFE0` implements this relationship (units: Mbps):

**Effective budget = clamp(requested target × queue factor × ping factor, 1, driver ceiling).**

The ceiling is applied **after** multiplication. A 350 Mbps request with both factors at 1 reaches the stock dongle ceiling of 232 Mbps. A request of 350 with a queue factor of 0.5 and ping factor of 1 reaches 175 Mbps. These are illustrative calculations, not reconstructed measurements of the user's session.

| Quantity | Recovered behavior |
| --- | --- |
| Driver ceiling | Original constants: 232 Mbps at `0x180C40F0C`, 350 Mbps at `0x180C40F10`. Installed values: 500 and 500. PE raw offsets: `0xC3EF0C`, `0xC3EF10`. |
| Dongle predicate | Uber-link virtual method `0x180A38280` reads a flag maintained by adapter scanning at `0x180A3A3F0`. It becomes true when a recognized dedicated-dongle adapter is Up. It does not test USB 2 versus USB 3, and does not prove that the dongle is the sole active video path. |
| Queue quantity Q | `0x180A493A0`: asynchronous pending-frame count plus the unsigned 16-bit difference between the transmit cursor and the last acknowledged decoded frame. The transmit cursor is incremented during submission, so this is not simply an OS socket-queue length. |
| Ping factor | Let T = 1000 / headset Hz in milliseconds and N = clamp(5 − Q, 1, 4). The factor is clamp(N × T / `cPingAMS`, 0.05, 1), with factor 1 when the reported ping is already within N × T. |
| Queue factor | `0x180A1CBE0`: Q = 4 multiplies the factor by 0.99 per update; Q = 5 by 0.95; Q = 6 by 0.90. The general multiplier has a floor of 0.5; this branch floors the resulting factor at 0.08. |
| Holdoff | Q > 6 or a pending throttle request can enter a holdoff. There is a 300 ms forced-exit condition. Leaving holdoff applies the additional multiplier 50,000 / (elapsed microseconds + 50,000), with a factor floor of 0.004. |
| Recovery | For Q < 4 the factor recovers toward 1 using `backoffRecoveryCoefficient`: +coefficient × 0.003 each update, another ×0.01 for Q < 3, and another ×0.02 for Q < 2. Installed settings supply 3.5; the constructor fallback is 2.0. |

Manual mode bypasses the automatic link estimate and its historical hitch ceiling, but retains queue and ping throttling. Increasing the backoff recovery coefficient speeds recovery; it does not disable the reductions.

Automatic mode has additional logic:

- The network-test result parser at `0x1809EAEC0` selects a qualifying tested bandwidth using ping distributions: at least ten samples in a group, over 80% positive pings below 20 ms, and under 4% positive pings above 60 ms. The encoder has a 200 Mbps fallback when a valid initial estimate is unavailable.
- `0x180A1CFE0` derives a recent estimate from roughly 200 ms of byte and frame-timing history. It reduces the estimate under ping/holdoff pressure and blends it back toward the measured estimate with different recovery weights.
- An additional historical hitch estimate can cap the automatic result. Switching to manual does not bypass the shared final driver ceiling.

These thresholds encode a latency policy. They do not establish the radio's maximum physical throughput.

### 2. Encoder output remains different from that budget

`0x180A24720` converts the effective Mbps to NVENC average bitrate in bits per second, requests a peak approximately 1.15 times larger, and derives a default VBV size from the previous configuration's peak divided by its frame-rate numerator. Capability-related checks can bound these values. The submission period also adjusts the encoder frame-rate parameter; it is not a reliable headset refresh-mode readback.

The researched reset path selects VBR, single pass, no spatial AQ, and Valve's delta-QP map in foveated mode. The frame path sets zero-reorder-delay and calls reconfiguration before encoding. Our helper may override selected rate-control fields, so accepted NVENC configuration must be captured for a particular test rather than inferred from these stock defaults.

Actual coded bytes depend on image complexity, foveation, QP policy, resolution and VBV constraints. A larger allowed average/peak does not require VBR to emit that rate. Filler is not enabled by our policy. Extra network repair bytes likewise do not constitute extra picture detail.

The earlier 350 Mbps trial recorded actual video averaging about 174 Mbps, but did not simultaneously capture the effective budget and throttle factors. It cannot distinguish exactly how much of that shortfall came from the stock ceiling, latency feedback, or VBR undershoot. The trace identifies the mechanisms; it does not retroactively supply missing counters.

### 3. Multiple links, packet framing and UDP scheduling

The normal Uber transport can own up to 16 per-interface transfers. `allowMultipleLinks` controls whether additional transfers remain eligible once a primary link has been found. The first accepted traffic establishes a primary; with multiple links disabled, the link update removes non-primary transfers. This is independent of the number of codec/decoder sessions.

At `0x180A39F80`, the sender gives the **same finalized datagram** to each connected, non-suspended eligible transfer. This is redundant delivery, not striping different portions of the video across interfaces. Receive sequence tracking suppresses duplicates. Adding interfaces does not inherently increase useful encoded bandwidth and can increase aggregate adapter traffic.

The packet path is:

1. FEC divides the encoded frame into 1,024-byte payload chunks. Each inner video packet has an eight-byte header carrying type/subtype, frame ID, payload size and packet ID.
2. `0x180A3BFD0` appends inner packets to an outer datagram. The initial packing limit is 1,376 bytes, so two full video chunks do not fit together. Small control packets can share datagrams.
3. `0x180A38530` adds sequence/framing according to the selected transport mode. The modes include clear framing, a lightweight checksum/XOR mode named `mid`, and an AES mode. `reqEncMode` selects **transport encryption**, not H.264/HEVC or NVENC quality.
4. `0x180A34AD0` copies the datagram and enqueue timestamp into each transfer's 24 MiB ring, then signals its worker.
5. `0x180A33860` handles both sending and reception despite its `RXThread` name. `0x180A336E0` drains queued datagrams through individual nonblocking `sendto` calls until empty or blocked. The Winsock event mask covers read, write and close events.

There is a separate optional bandwidth limiter in `0x180A39F80`, initialized to zero/disabled by the base constructor. It accounts for datagram size plus a 100-byte allowance and delays for token debt. Its wait wrapper calls `Sleep` in whole milliseconds. The offline follow-up below establishes its internal `gLimitMBPS` control mapping; this is not the ordinary bitrate slider or fine-grained packet pacing.

The Windows socket requests a 1 MiB send buffer and a 128 KiB receive buffer. The ARM64 per-interface path requests the reverse: 128 KiB send and 1 MiB receive (`0x44840`). Those are requested socket sizes; operating-system limits and accepted sizes were not queried in this pass. The Frame worker uses `poll`, drains sends, and receives individual datagrams with `recvfrom` (`0x45D28`, `0x45708`).

Host queue management includes:

- Skipping datagrams older than a moving purge timestamp, including purges driven by decoded-frame acknowledgements.
- Maintenance that discards sufficiently old queued packets, instead of preserving an indefinitely growing delay.
- Keeping the current packet queued on `WSAEWOULDBLOCK`. Other repeated send failures can suspend the transfer; maintenance retries after about a second.
- An enqueue path that can return success without copying the packet when ring space is insufficient. This is a code-path finding, not evidence that it occurred in the user's test.

QoS is a request to the OS/provider, with explicit fallbacks when unavailable. The source of truth is successful setup/readback and measured loss/latency; a saved `enableQoS` value does not prove that priority was accepted or that the radio honors it.

### 4. MTU probing and receive ordering

The Uber maintenance path (`0x180A3B7D0`) probes datagram sizes 12, 1,376, 2,212 and 3,152 bytes on eligible links. Larger packing is gated by a separate MTU-growth flag and protocol support; the constructor leaves that flag off. The existence of successful probes is not proof that jumbo datagrams are being used for video.

The receive path has a 2,048-entry sequence window and can retain control traffic behind missing datagrams for up to approximately 100 ms. **Video type 5, acknowledgement type 4, and pose/bookend type 3/subtype 1 have an immediate path through that window.** This is visible in host `0x180A377A0` / `0x180A39850` and client `0x47EE8`. Therefore it would be wrong to describe the 100 ms window timeout as a mandatory video buffering delay.

Video has its own frame assembly and in-order decoder submission rules after this fast packet path. Bypassing datagram reordering does not bypass missing video data or codec dependencies.

### 5. Repair overhead and retry policy

The host constructs the RAID6 FEC implementation (`0x180A305A0`, `0x180A4B0A0`). In the examined layout it groups eight data chunks with two available parity chunks, interleaves groups across 64-data-chunk batches, and repeats frame metadata between batches (`0x180A4B680`, `0x180A4BF90`).

The sender transmits one parity chunk per eight data chunks during the reduced-redundancy condition. If reported `cRXpass` drops below 99.99%, it transmits the second parity chunk for the following ten seconds. Thus full groups carry approximately **12.5% or 25% repair payload overhead**, before packet headers, repeated metadata and requested retransmissions. Partial groups can have a larger proportional overhead.

For scale, 400 Mbps of coded video requires 450–500 Mbps of data-plus-parity payload under these two conditions. That corresponds to roughly 55,000–61,000 full data/parity chunks per second, before aggregation and other traffic. It is a calculated protocol workload, not a measured throughput promise. Wi-Fi PHY rate, video Mbps, SVL byte counters and adapter Mbps are different measurements.

The host's `sBWFECLim` lookup defaults to 720 in this function. The traced use adjusts per-packet send-time bookkeeping used by retries; the function does not sleep at that value. It is not established as a 720 Mbps wire-rate cap or as a useful bandwidth-slider override.

The receiver has two distinct repair triggers:

- **Group/frame progress:** frame metadata and newly observed frames can request missing chunks from preceding groups or frames (`0x52A78`, `0x53D18`, `0x53EF8`). Missing whole frames can be initialized and re-requested.
- **Timed retry:** option 26, supplied by `enableTimedRetry`, enables periodic checks of incomplete receive slots (`0x52890`). The shared retry builder (`0x52BD0`) has a 4 ms request-interval guard and considers time since recent progress. The timed branch limits a request to 20 packet IDs. Oversized missing-packet lists can flag a stream reset.

The host retains 16 transmit-frame slots. Requested metadata/data/parity IDs are looked up in the matching frame; `0x180A49420` imposes a 3 ms per-packet retransmission guard. A request for a frame whose slot has already been replaced cannot be satisfied by that slot.

Turning off timed retry does **not** disable every repair request or remove parity. Similarly, `forceBaselineVideoFEC` is transmitted as an inverted option 31, but its complete receiver-side effect was not recovered. It must not be presented as a proven parity-off switch or a predictable 12.5%/25% selector.

### 6. Decode acknowledgements close the bitrate feedback loop

This chain is directly traceable on both ends:

| Stage | Evidence |
| --- | --- |
| Complete coded frame | Client `0x541E0` / `0x53958` accept an assembled frame, record reception, and submit completed frames to the decoder in order. |
| Decode completion | V4L2 media thread `0x32878` invokes renderer `DecodeDoneHandler` at `0x34EB0`. It maps the frame's presentation ID back to the transport frame and calls `0x43540`. |
| Feedback construction | `0x43540` records the decoded frame ID/time. `0x41960` places that ID at offset 2 of the type 4/subtype 15 acknowledgement. Other fields carry receive and further pipeline timestamps. |
| Feedback send | The client's `SendPoses` path at `0x269B0` sends this feedback alongside pose traffic. A decoded frame can therefore wait for the feedback cadence and return transport before the host observes it. |
| Host acceptance | `0x180A4A510` reads that offset-2 frame ID, updates the acknowledgement cursor, purges older queued sends, and invokes the server callback. |
| Budget response | `0x180A493A0` uses the cursor for Q; `0x180A1CBE0` and `0x180A1CFE0` apply the queue/ping reductions. |

This acknowledgement proves decode progress, not physical display scanout. A delay in decoding or feedback can look like pipeline congestion to the host even if the receiver's network interface has spare capacity. Conversely, a lower bitrate is not by itself proof that the decoder is the bottleneck.

`cPingAMS` is produced from the protocol's two directional timing estimates (`0x29EA0`, `0x5AC60`, metadata processing at `0x41720`). It is not a Windows ICMP-ping result. Clock-offset estimation and packet timing are part of its interpretation.

The client maintains 16 receive-frame slots plus completed/submitted bookkeeping. `0x53958` rejects a newly accepted frame when its unsigned 16-bit distance from the last submitted frame exceeds five, and also checks in-order submission. A missing frame can therefore hold up later decode submission and eventually trigger recovery. The larger rings are capacities, not permission to accumulate that many frames of useful latency.

The previously traced decoder recovery still applies: about 330 ms without progress prompts a new I-frame request, with roughly 100 ms request spacing; about 800 ms prompts a reset. These recovery actions can create conspicuous interruptions. This static investigation does not establish which one caused an earlier subjective flicker.

### 7. What the settings do, and what remains unknown

| Setting / control | Established interpretation |
| --- | --- |
| `targetBandwidth` | Manual encoder-budget request, still subject to queue/ping factors and final ceiling. |
| `automaticBandwidth` | Adds adaptive estimation and historical hitch limiting; disabling it does not disable all adaptation. |
| Installed 500 Mbps override | Raises the two fixed software ceilings in this build. Does not change USB enumeration, FEC, feedback timing or VBR utilization. |
| `backoffRecoveryCoefficient` | Controls the queue factor's upward recovery when outstanding work falls. |
| `allowMultipleLinks` | Allows redundant eligible transport paths. Does not enable multiple decoders or bandwidth striping. |
| `asyncSend` | Moves FEC/frame sending onto a separate worker and pending-frame queue. The normal per-interface UDP worker already exists without this setting. |
| `enableTimedRetry` | Gates timed incomplete-frame repair checks; other repair triggers remain. |
| `forceBaselineVideoFEC` | Host setting-to-option mapping confirmed; complete client effect remains unresolved. |
| `enableQoS` | OS/provider request, with fallbacks. Actual acceptance and radio scheduling benefit require measurement. |
| `maxVideoQueueLatencyUs` | Host publishes option 36. A corresponding consumer was not established in the analyzed client; do not assume a working queue-latency clamp from the name alone. |
| `reqEncMode` / `enableEncryption` | Transport framing/encryption selection. Separate from video codec and encoder rate control. |

The USB 2 → USB 3 change came from re-enumeration after the user unplugged and reconnected the dongle on the same USB 3.1 hub. This trace finds no USB-version-dependent choice between the two fixed encoder ceilings. Faster USB can improve the real link and consequently reduce feedback pressure, but it does not itself rewrite those constants.

Multiple decoders would require coordinated frame/eye partitioning, metadata, reference-frame ordering and renderer synchronization. The existing multiple-link switch provides none of that. Splitting a stream would not automatically remove the host's shared budget calculation or fix UDP scheduling.

The remaining boundaries are concrete: radio firmware scheduling and aggregation, channel/rate selection, USB bus scheduling, accepted OS socket/QoS limits, and hardware-decoder processing time are not fully determined by these application binaries. Option 31/36 consumers remain unresolved in this client. The debug MTU/limiter mappings are described in the offline follow-up below. No claim is made that every component of the wireless stack has been decompiled.

### Measurements this trace enables

The next useful app instrumentation is a synchronized explanation of **requested target → effective budget → accepted NVENC configuration → coded bytes → adapter traffic**, with these existing internal diagnostics:

| Diagnostic | Meaning / caution |
| --- | --- |
| `dTargetBW` | Effective pre-helper encoder budget; compare with NVENC's accepted average/peak/VBV. |
| `dEstLinkBW` | Adaptive estimate, or the manual request when manual mode overwrites it. |
| `dTXQ` | Pending/cursor distance incorporating decoded-frame acknowledgements; not a socket byte count. |
| `dQThrottle`, `dPresThr` | Queue/presentation factor; these refer to the same factor in the traced budget path. |
| `dPingThr`, `cPingAMS` | Ping factor and the protocol timing metric that drives it. |
| `dHitchMaxBW` | Additional historical cap in automatic mode. |
| `dCurBW` | Recent throughput-derived estimate; not a substitute for counting actual NVENC output bytes. |
| `dFBwThr` | Separate observed-frame-bandwidth diagnostic; not a third multiplier in the recovered final formula. |
| Frame receive/decode/submit times, retries and receiver resets | Separate transmission/repair delay from decoder delay and feedback delay. |

A low effective budget with a falling queue factor points to delayed pipeline progress. A high accepted budget with low coded bytes instead calls for examining scene complexity, QP/foveation and encoder constraints. A rising adapter/video ratio calls for repair and multi-link accounting. These distinctions are more useful than increasing the target until the network graph rises.

The most promising transport experiments are measured burst pacing and reduced packet overhead, after these counters are available. Increasing socket queues blindly could preserve more stale work; disabling repair could create missing-frame stalls; bypassing decode feedback could overwhelm the headset. None was applied during this read-only investigation.

## 2026-10-13: live CBR mismatch and dongle radio scheduling

### Router measurement: CBR was requested but was not active

A 31.27-second capture read the running app's existing atomic status snapshots, without
installing hooks, reconfiguring the encoder, or changing the connection. The PC used its
2.5 Gbps Ethernet adapter to the router; the dedicated dongle was absent during this
capture. Private evidence is `artifacts/RadioTrace/router-baseline*.{json,jsonl,txt}`.

| Quantity | Observed result |
| --- | --- |
| Requested policy | CBR, full-resolution multipass (packed policy 14) |
| Accepted configuration, every sample | HEVC 10-bit, VBR, single pass, P2; no filler |
| Coded video, successive approximately one-second intervals | Mean 177.6 Mbps; range 137.3–188.7 Mbps |
| Frame rate over those intervals | Mean 89.7 fps; range 86.6–90.8 fps |
| Accepted average bitrate | 285.4–400.0 Mbps, despite a 400 Mbps manual request |
| Accepted maximum bitrate | 450 Mbps, from the user's separate peak override |
| Encoder/bitstream-lock failures during capture | Zero |
| Policy guard deferrals during capture | 2,806, one for each reconfiguration |
| Reconfigure / encode API calls over 2 ms during capture | Zero / zero |

These are coded bytes and accepted API parameters, not radio goodput or subjective
smoothness. The completion callback's approximately 7.7 ms average includes waiting
for output; it is not an isolated hardware encode-time measurement. Surrounding logs
contained holdoffs, protocol ping near 3.2–3.4 ms, and occasional longer encoder/send
calls outside this interval.

The helper's `validatedPolicy` preserves the accepted mode when the GPU does not
advertise dynamic rate-control changes. A requested VBR-to-CBR transition then waits
for initialization of a new session. The old snapshot's aggregate rejection counter
did not distinguish that deferral from other guard refusals. The live mismatch is
proven; the exact capability reason is consistent with this guard but was not exposed
by the already-loaded helper. The local, unreleased helper change reports session-required,
unsupported-mode and initialization-failure states separately, alongside the dynamic
rate-control capability and supported mode mask. The accompanying local app change displays a persistent warning
for an active mismatch and includes the result in Apply feedback. A queued request is
not described as accepted CBR.

There are three distinct reasons a CBR selection cannot guarantee constant network
throughput:

1. It must first become the encoder's accepted mode. A bitrate refresh can leave an
   existing VBR session intact.
2. `averageBitRate` still follows Valve's per-frame queue/ping reductions, even in CBR.
3. This app explicitly disables filler insertion and picture-timing SEI. NVIDIA documents
   filler insertion for strict adherence to the byte rate. Padding adds no picture
   detail. Even padded CBR does not pace UDP packets evenly within each frame or keep
   repair traffic constant. See the [NVIDIA encoder guide](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html).

### Realtek driver and embedded firmware

The installed dongle driver is `rtwlanuval.sys`, version `5.32.908.2026`, SHA256
`378ffaee3b782b9a7f5382c294a846e7c4b3b952717991fd1fd93795c5a85612`.
The Valve USB ID selects the RTL8852CU installation section. Its display name says
8832CU; internal chipset routines and firmware selection identify the 8852C family.
All following driver addresses use image base `0x140000000`. Copies and IDA material
remain private in gitignored `artifacts/RadioTrace/`.

The common registry reader at `0x14001F728` processes 325 descriptors from
`0x1408C6C10`; a second table has seven USB-specific entries. Descriptors include a
destination offset, storage width, fallback and allowed range. This makes it possible
to follow a setting into its consumer instead of guessing from its name.

| Setting / path | Verified consumer and implication |
| --- | --- |
| `CoreAMSDU` | Registry field +4338 gates software A-MSDU processing at `0x14004D57C`. Installed registry value is 0. That does **not** establish that hardware A-MPDU aggregation is disabled. |
| A-MSDU maximum length | `0x140107E60` combines peer capability with `tx_amsdu_len` (+4371), then applies a nonzero `custom_tx_amsdu_len` (+4380) as an additional minimum. Installed values are selector 0 and custom length 3,100 bytes. Larger peer/selector classes include 7,991 and 11,454 bytes, but a custom bound still wins. |
| A-MSDU count and timeout | `0x1401070EC` batches by `tx_amsdu_num` (+4372), with peer-dependent limits; fallback count is 10. `tx_amsdu_timeout` (+4376) falls back to 1. `0x140002564` converts it to a relative NDIS timer with `-10000 × value` 100-ns units: milliseconds. More batching can save airtime while adding delay. |
| USB A-MSDU flush | `0x140006E34` checks `usb_timer_flush_amsdu` and traffic/time conditions, then flushes queues or arms that same timer. An INF `SendPacketByTimer` entry alone is not proof that this separate flag controls the path. |
| `tx_data_cnt` | Copied into a 16-bit retry-policy field by `0x1400B2754`. `0x14004BAC0` uses the low byte for ordinary data retry limits, with fallback 16 when zero; `0x140049D5C` uses the high byte for a special-packet retry limit, also defaulting to 16. The installed value 7 therefore does not mean seven packets per aggregate. |
| Runtime retry controls | `0x14012A1FC` / `0x14012A2D4` can replace the normal/special retry byte through custom VR requests. Registry values need not equal current runtime values. Increasing retries can improve delivery while worsening late-frame tails. |
| Thermal duty policy | `min_tx_duty` and `thermal_threshold` are copied from registry fields +4659/+4660 to the radio configuration. Installed values are 40 and 53. The threshold is a driver thermal quantity; degrees Celsius were not established. |

Thermal protection is an actual duty-control path, not merely an unused registry name.
The 8852C thermal comparator (`0x14038AD08`) includes per-path correction and a two-unit
hysteresis region. `0x1401C3EC8` changes a protection level from 0 through 5.
`0x140164EF8` computes a step of `(100 − minimumDuty) / 5`, with a minimum step of one.
For a configured minimum of 40, the calculated transmit-duty stages are
**88%, 76%, 64%, 52%, 40%**, with recovery back through the levels. The MAC path
`0x1401C4118` splits the period into on/off portions; `0x1402BA1E0` sends two 16-bit
durations and a control word to firmware. This can limit throughput when active.
It is not proof that the user's dongle is hot or currently throttling, nor is it a
fixed Mbps ceiling. Thermal protection was not disabled or changed.

The embedded-firmware selector at `0x140485740` (`mac_query_fw_buff`) identifies four
8852C buffers: normal/WoW variants for two silicon-cut branches. All four advertise
version **0.29.123.4**, commit **8fd5840c**, with 2025-08-04 build timestamps. The
normal images are 444,816 and 418,000 bytes. The exact sizes come from the selector's
size constants, not from estimating the container length. The normal-image SHA256s
are `01e11316c0727abd9d689004f82e41ad627b8b9d84b4cb901b6afb13fed86b73`
and `1a377d6f0ebd6be49aa1f8c00fce460c3caab9442b70aab9de7fbc78b1045602`.

The [Linux rtw89 firmware header definitions](https://github.com/torvalds/linux/blob/master/drivers/net/wireless/realtek/rtw89/fw.h)
provided a container-layout cross-check. The two normal images were independently
wrapped for static RISC-V analysis with their code/data load addresses, without
executing or flashing them. In the second image, IDA recovered 1,100 function
boundaries. RISC-V assembly at `0x20123FC8` creates `Tx_Duty_timer`; its callback at
`0x20123ED8` alternates states 1 and 2, selects the two duration fields, invokes
`0x20123E8E` with alternating arguments and rearms the timer. The host H2C header
builder (`0x1402E9B7C`) encodes category 1, class 9, function 24. The firmware's
function-24 dispatch at `0x20124C56` passes the eight-byte payload to this duty handler.

The final queue gate was also recovered: `0x20123E8E` supplies mask `0xFF` and
alternating values `0x00` / `0xFF` to `0x2010DC34`. That routine updates a shadow word
and writes the selected register from the table at `0x2021C3C8`: `0xC348` or `0xE348`.
The [Realtek rtw89 register definitions](https://github.com/torvalds/linux/blob/master/drivers/net/wireless/realtek/rtw89/reg.h)
identify these as `CTN_TXEN` for the two MAC banks, with bits 0–7 enabling the eight
data queues. Management/beacon queue bits lie above that mask. Thus this path really
alternates data-queue transmission eligibility; it does not merely change an
advertised bitrate. The second bank is updated only when the checked hardware flag
is set. The timer's time unit remains unresolved: the callback calls through a
function-pointer table outside the extracted loadable sections. No RISC-V pseudocode
was recovered in this pass; these firmware findings come from assembly.

These are embedded candidate identities, not a readback of the dongle's running cut
and firmware. External firmware-loading paths also exist. No claim is made that all
firmware functions or every scheduling limit have been resolved.

### What to change and what still needs measurement

The immediate correction is to activate and verify the requested encoder mode before
comparing CBR with VBR. The local app warning and native reason fields were implemented and
built during the investigation but remain unreleased; they do not themselves increase throughput. After that, the most useful
experiments are bounded packet pacing, aggregation/timeout comparisons, and retry
policy comparisons, while recording tail latency, missing frames and decoder progress.
Thermal state should be measured; a cooling/placement remedy is preferable to removing
its protection. A separate padded-CBR transport test could establish delivery headroom,
but cannot establish improved picture quality. A fixed encoder-budget experiment would
also need explicit latency/rollback guards because it changes congestion behavior.

After the user reconnected the dongle, Windows reported a 2.4 Gbps PHY link. A separate
USB-hub IOCTL read at 18:23 confirmed **USB 3 SuperSpeed, 5 Gbps signaling**; neither
number is a measured video rate. Paired SSH timed out. Starting SteamVR again left its driver listening on the dongle address,
but it still reported no connected headset. This pass
therefore did **not** measure dongle retry/thermal state, establish 400+ Mbps of useful
video, or validate smooth high-rate gameplay. Neither registry values, firmware, router
settings nor the active encoder policy were changed by these measurements. The next
capture needs an active Frame stream and synchronized host/receiver counters.

## 2026-10-13: offline sender controls and HEVC experiments

The user requested continued offline work. No further headset connection attempts
or live encoder-policy, radio-registry or firmware changes were made in this pass.

### Limiter and MTU controls are now mapped

The previously unresolved sender limiter is populated by `0x180A321A0`, which reads
the root's **internal numeric map** through `0x1809E9770` using `gLimitMBPS`, then
stores the result at the data-link object's float offset +8. Maintenance calls this
refresh. The debug callback at `0x180A3F800` writes that map through `0x180A51C80`;
its assembly computes a control-position ratio times the float constant **500** at
`0x180C4A698`. This is a debug-control scale, not evidence of a normal-stream
500 Mbps clamp or a hidden 150 Mbps ceiling. No `driver_vrlink.gLimitMBPS` OpenVR
setting mapping was found; adding that name to the app's settings list would not
establish that the control works.

The limiter's accounting is approximately:

```text
debtBytes += datagramBytes + 100 - limitMbps × elapsedMicroseconds / 8
if debtBytes > 0:
    waitMicroseconds = debtBytes × 8 / limitMbps
    Sleep(integer(waitMicroseconds) / 1000)
else:
    debtBytes = max(debtBytes, -20000)
```

`Sleep(0)` can yield without a requested delay; positive sub-millisecond debt is
therefore not metered with a corresponding timed wait. The 20,000-byte credit floor
also permits a burst after inactivity. This is a coarse bandwidth limiter, not
uniform packet spacing. A further scheduling concern is visible in `0x180A3BFD0`:
it holds the packet-assembly mutex while calling `0x180A39F80`, including its wait.
Other traffic sharing that packer can be delayed while video consumes the limit.
This is a static risk finding, not a measured cause of the user's earlier stutters.

The limiter charges repair datagrams and a per-datagram allowance, not just video
payload. Setting it to 400 would consequently leave less than 400 Mbps for video.
At 400 Mbps, 1,024-byte chunks plus 12.5–25% parity imply approximately 55,000–61,000
data/parity datagrams per second with the ordinary packing limit. Exact packet
counts also depend on partial groups, metadata and retries. A future pacer should
use the **transport** budget, bound queued age, and keep its waits outside the
shared packet-assembly lock.

The debug MTU callback is `0x180A3FD10`: after runtime type checks for `SVLServer`
and `SVLDataLinkUber`, action 1 toggles the byte at link offset +672. The constructor
clears it. This is the flag behind the `dEnableMTU` / “Toggle MTU Growth” debug
control, rather than an established ordinary OpenVR setting. Maintenance can then
increase the packing threshold based on observed probes, up to 3,152 bytes.

The PC dongle interface reported an IPv4 MTU of **1,500** during the offline pass.
A successfully received 2,212/3,152-byte UDP probe does not by itself prove an
unfragmented path: IP can reassemble fragments before delivering a datagram. Thus
turning on this switch is not yet justified as an airtime improvement. A lost IP
fragment could discard a datagram containing several FEC chunks. Neither debug
control was enabled.

Host provenance was rechecked across 136 recovered functions / 111,794 bytes against
the same installed SHA256 recorded above. The only differences remained the known
encoder-policy patches in `0x180A22570` and `0x180A234A0`; the newly traced limiter
and MTU functions matched.

### A reproducible encoder-only lab

[`Native/EncoderLab`](../Native/EncoderLab/README.md) now contains a standalone
public-NVENC-API experiment with selectable rate control, filler, multipass, nominal
cadence, a mid-run bitrate step, optional reset, and CSV measurements. It does not
inject into SteamVR or send packets. The input is 2048×8192 P010, HEVC Main10, P2
ultra-low-latency, no B frames, a one-frame VBV and no periodic IDRs. The two scenes
are flat neutral pixels and two alternating deterministic luma-noise textures.
Neither is a gameplay-quality benchmark. Runs used the RTX 5090 / driver 617.42.

The GPU query returned supported rate-control mask **3** (VBR and CBR), dynamic
bitrate support **1**, and dynamic rate-control-mode support **0**. This independently
confirms the need for a new session when changing VBR/CBR on this machine.

Unpaced runs process 300 frames serially; the table's rates use the 90 fps **media
duration**, excluding the first 30 frames. Timing is map/submit/blocking-completion
latency, not pure GPU duration or SteamVR's pipelined throughput.

| Input / configuration | Target | Measured coded Mbps | Non-filler Mbps | Completion p99, whole run |
| --- | ---: | ---: | ---: | ---: |
| Flat, VBR single | 400 | 0.317 | 0.317 | 8.56 ms |
| Flat, CBR single, no filler | 400 | 0.317 | 0.317 | 8.49 ms |
| Noise, CBR single, no filler | 400 | 338.18 | 338.18 | 7.66 ms |
| Noise, CBR single, filler | 400 | 400.00 | 338.18 | 8.65 ms |
| Noise, CBR single, no filler | 450 | 406.85 | 406.85 | 7.58 ms |
| Noise, CBR quarter, no filler | 400 | 459.21 | 459.21 | 8.93 ms |
| Noise, CBR full, filler | 400 | 400.00 | 374.21 | 14.26 ms |

Filler is identified from HEVC Annex B NAL type 38. “Non-filler” still includes
headers and is not a picture-quality score. The quarter-resolution overshoot was
reproduced in a second run with filler and again without filler: the non-filler
rate was identical. Accepted minimum/maximum QP constraints and spatial/temporal AQ
were all disabled. This is a synthetic, fine-detail rate-control failure case; it
does not establish that quarter multipass always overshoots. It does establish
that a CBR label and successful initialization cannot be treated as an exact
per-frame bandwidth guarantee.

Full-resolution multipass took approximately 12.19 ms median and 14.26 ms p99 in
this serial test, with all 270 post-warmup frames exceeding the 11.11 ms budget for
90 Hz. Its wall processing rate was about 78.5 fps. That is a reason to measure
timing before applying full multipass at this resolution, not a universal ceiling
for all HEVC encoding paths.

### Constant filler has state across reconfiguration

Starting padded CBR at 400 Mbps on a flat image and reconfiguring average, maximum,
VBV and initial delay for 300 Mbps returned success but continued producing
**400 Mbps**. Repeating with `resetEncoder=1` and `forceIDR=1` produced the intended
400→300 Mbps step. Unpadded CBR on noise did react without a reset: the measured
non-filler rate changed from about 339.45 to 260.38 Mbps in the steady sections.

This is observed behavior of this driver/API/configuration, not a universal NVENC
rule. It makes a naive filler toggle unsuitable for Valve's per-frame changing
budget: resetting/forcing an IDR for each budget update would introduce substantial
work and transient traffic. A padded transport test needs its own explicit rate
and lifecycle, and cannot be described as increased image detail.

### Frame pacing in the isolated producer

A 900-frame, 450 Mbps CBR/no-filler noise run produced **405.50 Mbps** over its
nominal media duration (404.81 Mbps after the first 30 frames). The first paced
version used ordinary `sleep_until`: wall fps was 89.89 but completion-gap p99 was
25.35 ms. This was a problem in the **lab producer**, not a measurement of SteamVR.

The lab now uses an absolute schedule implemented with a high-resolution Windows
waitable timer and a short final spin. The follow-up produced the same encoded byte
count, about **90.03 wall fps**, **11.65 ms completion-gap p99**, **11.83 ms maximum
gap**, and **7.12 ms encode-completion p99**. No post-warmup encode-completion sample
exceeded 11.11 ms. It demonstrates why both cadence tails and average fps need to
be measured. This timer has not been installed into Valve's sender, and it is not
a packet-spacing implementation.

The first 60 frames of padded-flat and unpadded-noise output both independently
decoded with FFmpeg without errors. Private raw results are in
`artifacts/EncoderLab/{matrix,edge-cases,highres-summary}.json` and the per-frame CSVs.
All generated streams and executable artifacts remain ignored. The actionable
direction is to verify CBR at session creation, favor single-pass timing for the
first 400+ Mbps transport comparison, and investigate bounded packet batching with
precise scheduling. Wireless capacity, radio thermal state, decoder headroom and
smooth gameplay remain unmeasured in this offline experiment.

## Connected measurements, October 13, 2026, evening

The Frame reconnected through the dedicated 6 GHz, 160 MHz link. The hub IOCTL
confirmed USB 3 SuperSpeed both before and after these experiments. Windows'
2.4 Gbps adapter indication was not treated as measured capacity. SSH used the
existing paired key and verified host; no new pairing or credentials were stored.

An initial Home/idle observation produced only 0.374 Mbps of video at 90 fps,
despite a 400 Mbps accepted average. It is not useful throughput evidence. The
new bounded `Native/EncoderLab/steamvr_scene.cpp` instead submitted translating
procedural detail through the ordinary OpenVR compositor and Valve encoder,
transport, decoder and display path. All sampled poses were valid and its eye
submissions succeeded. This is difficult synthetic input, not gameplay or a
subjective smoothness assessment. Temporary keep-awake settings were restored.

The fresh monitor initially observed **VBR/single-pass**, not CBR. Frame-only
disconnect/reconnect retained the NVENC session and left the CBR request pending.
Stopping SteamVR, arming the helper before reconnection, then creating the encoder
produced independently observed CBR readback with no pending policy or filler.

Thirty-second moving-detail comparisons at the same 400 Mbps slider:

| Accepted configuration | Actual video Mbps | Encoded fps | Receiver retry events | Presentation timeouts |
| --- | ---: | ---: | ---: | ---: |
| Main10, VBR/single | 119.43 | 88.10 | 33 | 9 |
| Main10, CBR/single | 116.67 | 88.32 | 46 | 14 |
| Main10, CBR/single, router link temporarily disconnected | 130.49 | 87.00 | 21 | 21 |
| Main, 8-bit CBR/single, verified after full restart | 129.19 | 88.17 | 24 | 17 |

All four reported zero encode/lock errors and zero receiver interface errors or
socket-buffer overflow. Receiver retry events are requests, not unique lost
packets. The router connection was restored automatically. The first attempted
8-bit measurement retained a 10-bit session and was interrupted; its file is
explicitly marked invalid as an 8-bit trial. The replacement file is
`moving-cbr400-8bit-fresh.json`.

Manual target and CBR did not freeze the encoder budget. The first CBR run sampled
accepted averages from **99.2 to 249.6 Mbps**, averaging 191.95 Mbps. Valve's
decode-acknowledgement/queue controller was still acting. Simply bypassing that
feedback would not remove the observed packet-delivery delays.

Recovery traces also showed decoder pressure. A 4,031-frame trace at 19:05:31
had encode median/p99 7.57/9.04 ms, transport median/p99 6.53/51.92 ms, and
remote decode median/p99 12.39/22.47 ms. These are overlapping recovery-selected
histories, not a random steady-state sample; do not add their frame counts to the
above windows. Nevertheless, decoder intervals above the 11.11 ms 90 Hz period
and long transport tails are visible separately. A decoder counter saying zero
`dropped_frames` does not negate presentation timeouts or delayed frames.

### Isolated transport and reversible driver comparisons

With the video stream stopped, `Native/EncoderLab/udp_probe.cpp` sent bounded,
sequence-numbered synthetic UDP payloads to a temporary listener on the paired
Frame. The listener checked unique packet receipts and Linux UDP error counters.
This is network capacity evidence, **not encoded video**. Receive rates divide
actual received bytes by receiver elapsed time: the sender could not maintain
the requested 450/550 Mbps schedule, so dividing by the nominal eight seconds
would incorrectly report the requested rate as delivered throughput.

| Requested Mbps | UDP bytes/datagram | Nominal batch interval | Received payload Mbps | Missing packets |
| ---: | ---: | ---: | ---: | ---: |
| 200 | 1040 | 100 µs | 199.96 | 0.032% |
| 450 | 1040 | 100 µs | 331.92 | 0.183% |
| 450 | 1040 | 11.111 ms | 339.55 | 0.179% |
| 550 | 1040 | 100 µs | 358.67 | 0.121% |
| 450 | 1400 | 100 µs | 346.78 | 0.317% |

A repeat of the 450/1040/100 µs case delivered 342.57 Mbps. Of 10.47 seconds
elapsed, cumulative `send()` duration was 10.15 seconds, process CPU usage was
2.89 seconds, and 289 sends exceeded 1 ms, with a **67.01 ms maximum**. This is
downstream socket backpressure/stalling, not just a timer failing to generate
enough packets. It does not identify a specific radio firmware scheduler as the
root cause. Zero Linux receive-buffer overflows argues against the test listener
being the loss point. Batch spacing is only nominal once the sender is overloaded.

A bounded administrator-run trial changed only Realtek `CoreAMSDU` from 0 to 1,
restarted that verified dongle, and automatically restored 0 and restarted it
again. It delivered 336.29 Mbps with 0.381% missing packets; the restored repeat
delivered 364.03 Mbps with 0.157% missing packets. Registry readback and restarts
are verified; active use of software A-MSDU aggregation was not independently
captured. This trial does not support enabling the option as an optimization.
Thermal protection, transmit power, retry limits and regulatory settings were
not changed. The full rollback record is in the ignored `amsdu-result.json`.

These results do not establish a universal hardware ceiling. They establish that
this connection, in its measured placement/state, could not deliver the roughly
450–500 Mbps of video-plus-FEC payload needed for 400 Mbps of video, before other
protocol overhead and retries. Small paced batches and larger datagrams did not
make the current link sufficient.

### Read-only Realtek SDK radio evidence

The installed `RealtekWiFi64.dll` has SHA-256
`a1fd6efdb278d94f1bdeefee67c30d62b0a10ae8b8c810b559f6b332d8e3f8e7`.
Its exported instance/metrics APIs were traced before use. The 149-byte response
contains a count and up to two 74-byte link records. Driver function
`0x1401F3834` copies the peer address, prints RSSI using `byte[6] - 110`, and
formats TX and RX rate strings at record offsets 7 and 39. Three live queries
returned **−76 dBm** using that driver conversion, TX strings `1SS MCS6` /
`2SS MCS4`, and RX strings `HE_NSS1_MCS4` / `HE_NSS1_MCS3`.

The exact SDK's read-only thermal-range command `0x8051` returned zero in all
three samples. Its driver handler `0x1401F56FC` reads byte `+43780`, **not** the
separate thermal duty level at `+43776`. Therefore this is not an actual
temperature, confirmed zero throttling, or proof of the running duty percentage.
The remaining three metric bytes are left uninterpreted. SDK status and bounds
were checked, the expected adapter GUID was verified, and the SDK instance was
destroyed afterward; no SDK setters were called.

Low reported live modulation, weak dongle-side reception and the capacity tests
make placement/RF conditions a useful next controlled comparison. Valve also
recommends a clear line of sight and keeping the adapter within approximately
20 feet of the playspace in its [Frame troubleshooting guide](https://help.steampowered.com/en/faqs/view/2668-0B6E-79E0-A85B).
The reception asymmetry and driver/firmware scheduling remain candidates, not
fully isolated causes. No thermal bypass or unmeasured radio tweak is warranted
by these readings.

SteamVR Home was then restored with 10-bit HEVC, 400 Mbps target, quarter-resolution
CBR, and the earlier −2/−6 quality offsets. Its 30-second unattended observation
produced 121.33 Mbps at 87.34 encoded fps, 30 retry events and 38 presentation
timeouts. It did not demonstrate smooth 400+ Mbps. Private raw snapshots, radio
responses, incremental logs and trial scripts are under ignored
`artifacts/RadioTrace/`; no Valve/Realtek binaries or decompiled code are bundled.

A final Home comparison reduced stream width to 1536 (accepted 1536×6144 Main10)
for 30 seconds. It produced 149.12 Mbps at 86.99 encoded fps with 38 presentation
timeouts. Mean encoder submission-to-lock completion fell from approximately
9.10 to 5.14 ms, but end-to-end cadence did not recover. This supports delivery
stalls remaining a problem after reducing encoder/image load; it does not prove
the decoder has unlimited headroom. Width was restored and verified at 2048.
Final runtime readback confirmed active Main10, 2048×8192, CBR/quarter, no pending
policy, and no filler. The app's local preferences were aligned with that verified
CBR/quarter request and the earlier −2/−6 quality offsets, retaining other fields;
the prior preferences were backed up privately. The requested driver target
remained 400 Mbps and asynchronous sending remained enabled. These are requested
settings, not a claim that 400 Mbps or smooth pacing was achieved.

## Improved-link follow-up, October 13, 2026

After the user made the connection available again, the read-only radio query
reported approximately −64/−63 dBm and TX `2SS MCS9`; Frame's last-ACK reception
was approximately −45 dBm. No radio, thermal, power, regulatory or USB setting
was changed in this follow-up. The same eight-second 450 Mbps UDP test delivered
450.01 Mbps, receiving all 432,692 sequence numbers, with no UDP buffer errors.
Maximum sender call time was 0.174 ms. This disproves a fixed 330–360 Mbps ceiling
for this hardware, but does not establish sustained video/decoder performance.

### What SteamVR's Waterfall bitrate actually counts

The exact supported host binary's display routine at `0x180A403B0` accumulates
the last second of completed transport-ring records. It multiplies byte counter
zero by eight and by **2^-20**, although the label says `mbps`. The counter includes
assembled UDP datagrams, including parity and protocol bytes. It is neither
decimal video-only Mbps nor a receiver delivery counter. Ninety recent completed
ring entries produce the associated 90 FPS display. The current incomplete slot
must be excluded from an external observer.

A private, read-only observer verifies the driver's SHA-256, ring ownership and
bounds, uses the same QPC clock, and reads those counters without changing them.
The diagnostic `dCurBW` estimate is a different quantity. App encoder-byte counts,
receiver interface traffic and Waterfall values should not be interchanged.

### Useful bits, image dimensions and delivery

These Home trials used Main10 HEVC, 90 Hz, CBR without filler, quarter-resolution
multipass and a 350 Mbps manual target. Values describe unattended observations,
not subjective gameplay smoothness. Offsets are gaze/periphery delta-QP additions.

| Width / offsets / sender | Seconds | Actual video Mbps | Encoded fps | Presentation timeouts |
| --- | ---: | ---: | ---: | ---: |
| 2048 / −2, −6 / async | 30 | 184.38 | 89.97 | 0 |
| 2048 / −8, −12 / async | 30 | 242.25 | 89.99 | 0 |
| 2048 / −20, −12 / async | 30 | 256.30 | 90.00 | 5 |
| 1792 / −20, −12 / synchronous | 30 | 287.25 | 89.97 | 0 |
| 1664 / −20, −12 / async, warmed | 90 | 317.77 | 89.97 | 23 |
| 1664 / −20, −12 / synchronous, warmed | 90 | 307.12 | 89.98 | 1 |
| 1536 / −20, −12 / synchronous, warmed | 90 | 303.97 | 89.97 | 2 |

At full width with the earlier offsets, the accepted average stayed at 350 Mbps
throughout despite actual output of only 184 Mbps. This particular gap was not
a reduced average-bit-rate request. Stronger quality offsets spent more useful
bits. Reducing width also reduced processing load, but sacrifices spatial
resolution; neither change is automatically better image quality in every scene.

The warmed 1664 async trial's Waterfall samples were 301.26–398.96 (mean 359.04),
all above 300. The synchronous trial reduced presentation timeouts to one but had
two sampled seconds below 300 (range 272.44–389.31, mean 345.66). An earlier async
trial had a roughly 1.5-second packet-repair/decoder-reset interruption. Average
90 fps and a high counter do not erase these delivery interruptions.

### Bounded fixed-budget and packet-pacing experiments

Private helper copies applied an override for at most 120 seconds, with the normal
helper restored by a fresh SteamVR session afterward. No packet contents, FEC,
frame counts or displayed counters were altered. These are not shipping presets.

Holding only the reconfiguration average at 350 Mbps for 70 seconds produced
321.91 Mbps of actual video at 89.96 fps. Waterfall stayed above 300 in every
sample (307.57–403.97, mean 367.99), but ten presentation timeouts remained.
An earlier variant that also changed initialization parameters failed encoder
initialization; it provides no evidence that any one of those fields was the cause.

A 500 Mbps per-worker packet pacer, limited to the paired peer and driver caller,
was then added to that fixed-average trial. Its attempt counters confirmed the
hook ran. Actual video fell to 244.38 Mbps; Valve reduced the other rate-control
fields even while the experiment held the average constant. Accepted peak ranged
from 103.28 to 291.11 Mbps and VBV from 0.94 to 3.37 Mbit. There were four presentation
timeouts and 65 of 71 Waterfall samples below 300. This does not support shipping
that pacer. Attempt counters are not successful sends or received packets.

### Maximum-QP removal requires a fresh session

A maximum-QP value of eight produced approximately 336 Mbps but only 40 encoded
fps. Removing the request returned successful reconfiguration calls while the
old quantizer behavior persisted; destroying/reinitializing the encoder restored
normal behavior. The local, unreleased app change treats enabling, changing **and removing** maximum QP
as initialization-only. Regression coverage verifies that removal remains pending
and that restoration hooks cannot be stopped prematurely. A successful NVENC API
return alone is not proof that an old quality constraint was removed.

Raw reports, temporary experiment sources and exact-build observers are in the
ignored `artifacts/RadioTrace/` directory. None of these results demonstrate a
constant 350 Mbps of useful video or smooth sustained 400+ Mbps gameplay yet.

### Full budget tuple and normal-controller follow-up

The next bounded helper overrode only reconfiguration, setting average to
350 Mbps, peak to 402.5 Mbps and VBV to 4,472,222 bits. It left initialization and
frame-rate fields unchanged. In 70 seconds, actual video was 323.91 Mbps, with
one-second samples of 313.40–328.27 Mbps at 89.91 FPS. Waterfall samples stayed
between 350.56 and 402.89; six presentation timeouts remained. This isolates the
tuple from the previous average-only experiment, not each individual field's
effect. In particular, the public NVENC API describes maximum bitrate as a VBR
control; this trial is not evidence that changing the peak independently caused
the output difference.

After restoring the normal helper, a 400 Mbps target with width 1664, synchronous
sending and the same −20/−12 quality offsets produced 323.61 Mbps at 90.01 FPS
over 70 seconds. Waterfall stayed at 326.35–369.81, with four presentation
timeouts. A subsequent 120-second observation produced 320.76 Mbps at 89.97 FPS;
all 121 sampled Waterfall values remained above 300 (315.41–406.38), with six
timeouts and no encode/lock errors. The host controller remained active: sampled
accepted averages ranged from 240.18 to 389.45 Mbps. These comparisons do not
establish a useful advantage for a persistent fixed-budget bypass.

Read-only inspection now samples the host's AFTM ring during ordinary streaming,
not only after a recovery. The [system map](research/README.md) combines its
observations with the separately traced host, receiver and radio paths, including
the actual semantics of encoder completion, decoder turnaround and display-target
feedback. Earlier CSV-derived names must not be read as pure hardware durations
or observed physical presentation times.
