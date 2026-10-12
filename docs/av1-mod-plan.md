# Steam Frame AV1 implementation plan and feasibility tests

Implementation has started. See [the 2026-10-12 implementation status](av1-implementation-status.md) for the matching build assets, compiled decoder candidate, PC backend, supplied-repository findings and outstanding runtime gates.

2026-10-11. Target: retain 120-Hz operation over the dedicated dongle, preserve gaze/foveation and recovery behavior, and add a verified AV1 option with HEVC fallback.

The PC encoder is viable. The first implementation milestone must be a working **Frame hardware AV1 decoder**. The installed Iris driver does not expose AV1, although the existing firmware contains AV1 decoder code. A targeted driver backport, initially retaining that firmware, is the most credible approach. The paired prototype now streams AV1 through SteamVR/VRLink end to end. The implementation status records the verified 120-Hz dongle test and remaining general-release work.

## What was tested

All generated media was synthetic. PC tests created independent encoder sessions; they did not change SteamVR's codec, patch its DLL or replace the headset kernel/firmware. Frame decoder queries used separate file descriptors. No cameras, user video, authentication material or process arguments were collected.

| Test | Result | Meaning |
| --- | --- | --- |
| PC FFmpeg NVENC AV1, 512 Ã— 512, 8-bit | 120 frames encoded and decoded successfully | Basic AV1 encoding and NVIDIA hardware decoding work. |
| PC FFmpeg NVENC AV1, 1920 Ã— 1080, 10-bit | 120 frames encoded and decoded successfully | 10-bit input/output works. |
| PC FFmpeg NVENC AV1, 1536 Ã— 6144, 10-bit | 120 frames encoded and decoded successfully; no B-frames | The actual packed dimensions are accepted. CPU-generated/uploaded pipeline measured about 88 fps, so this is not the GPU performance result. |
| Direct D3D11 â†’ NVENC API 12.1, packed dimensions, 10-bit output | 600 frames; about 365 fps; p99 about 4.46 ms | A GPU-resident source avoids the CPU test-pattern bottleneck. This includes draw, synchronization, encoding, output lock/write and unmap, but excludes initialization. |
| Same direct path with an AV1 delta-QP map | 600 frames; 2,304 map entries accepted; about 358 fps; p99 about 4.56 ms | An AV1-specific 64 Ã— 64 grid can be submitted. It does not prove a correct Valve gaze-map conversion or improved center quality. |
| Direct source paced at 120 Hz, no map | 600 frames; p99 4.56 ms; 2 absolute schedule misses | Short functional timing check, not a sustained gaming-load guarantee. Frame work itself stayed below 8.33 ms; wake/schedule delays also count toward deadlines. |
| Direct source paced at 120 Hz, AV1 map | 600 frames; p99 4.51 ms; 0 schedule misses | Promising short timing result for the mapped path. |
| PC bitstream inspection | AV1 Main, 10-bit, level index 17; all 600 frames decoded in both unpaced direct tests | A valid AV1 stream was produced. Header profile/level/tier must also be accepted by the Frame decoder. |
| Frame hardware format enumeration | H264, HEVC, VP90 only | Current hardware-decoder interface has no AV1 option. |
| Frame raw `VIDIOC_TRY_FMT`, AV01 | Returned H264 at 320 Ã— 240 | Kernel fallback, not AV1 acceptance. A successful ioctl alone would be misleading. |
| Frame raw `VIDIOC_TRY_FMT`, HEVC | Retained HEVC at requested 512 Ã— 512 | Positive control for the same ioctl/structure. |
| Frame software AV1, packed 10-bit clip, 4 threads | About 90.5 fps | Below 120 fps even before renderer upload/composition. |
| Frame software AV1, 8 threads, automatic frame delay | About 122 fps | Short throughput result with buffering; insufficient evidence of low latency or sustained headroom. |
| Frame software AV1, 8 threads, maximum frame delay 1 | About 35.6 fps | Not a viable low-delay replacement at the current packed dimensions in this test. |

The tested PC is RTX 5090, NVIDIA driver 616.56. The direct test uses the locally installed public NVIDIA API 12.1 header, P2, ultra-low-latency tuning, VBR 200/230 Mbps, single-frame-sized VBV, no B-frames/lookahead/multipass/AQ/padding. An 8-bit GPU texture is converted to 10-bit by NVENC; the separate FFmpeg test also exercised 10-bit input.

These are short feasibility tests using two synthetic patterns. They do not establish sustained thermal performance, latency during a demanding game, packet-loss recovery or subjective visual quality.

## Exact Frame baseline and firmware evidence

- SteamOS 0.4.5, build `20261007.6125817`; SM8650 / Adreno 750.
- Kernel package `linux-618-deckard 6.18.0+ge66bc2ca6f8c-1`, running release `6.18.0-ge66bc2ca6f8c`.
- Package source identifies commit `e66bc2ca6f8ca6baa5260c69c9b79dc01a6238b9` in Valve's `deckard/deckardos/linux-valve` repository.
- Matching `/lib/modules/<release>/build` headers are absent. Anonymous access to the exact source URL redirects to sign-in. A matching source/config/build environment has not been obtained.
- Decoder is `qcom-iris`, device tree compatible `qcom,sm8650-iris`, `/dev/video22`.
- Installed `iris.ko` SHA256: `7a5647a009d60068965a476a887c2c73a51bedcec22727d71f010e9165d64a86`.
- Existing firmware `/lib/firmware/qcom/vpu/vpu33_4v.mbn`, 2,343,624 bytes, SHA256 `7b829fc1c8ce7cca836d10e898b99c5bcbd86e22073b690147168c9d0a5de378`.

Read-only firmware string inspection found AV1 parser, reference/DPB, buffer-requirement, input/output sequence-change and worker routines (`av1DecForwardParseHeader`, `av1DecPopulateDPBList`, `av1DecCalcBufReq`, among others). This is evidence that AV1 code exists in this image, not proof that the required session/profile is enabled or works.

Upstream Iris now has an SM8650 platform using an AV1-inclusive format table, AV1-specific buffer calculations and HFI codec ID **7**. Its SM8650 firmware filename is `vpu33_p4.mbn`, whereas this Frame uses `vpu33_4v.mbn`; those images must not be treated as interchangeable. Use the upstream implementation as backport reference and validate the Frame firmware interface first. [Linux SM8650 platform](https://raw.githubusercontent.com/torvalds/linux/master/drivers/media/platform/qcom/iris/iris_platform_vpu3x.c), [HFI definitions](https://raw.githubusercontent.com/torvalds/linux/master/drivers/media/platform/qcom/iris/iris_hfi_gen2_defines.h)

## Implementation order and pass criteria

### 1. Backport and independently prove the Frame hardware decoder

Obtain the exact Valve kernel source/config and build assets before producing a module. Preserve the Frame board/device-tree integration, clocks, power domains, memory mappings, existing HEVC behavior and current firmware. Prepare and verify a known-good boot/rollback path before loading a modified kernel or module; no replacement kernel was installed for these tests.

Backport the AV1 path rather than simply adding a format-table entry:

- Stateful `AV01` enumeration, codec mask and HFI decode-codec mapping.
- AV1 capability/profile/level/tier/operating-point controls and the necessary firmware subscriptions/properties.
- AV1 input, reference/DPB, COMV, non-COMV, line, bin and persistent buffer sizing/count/lifetime handling.
- Tile/superblock metadata, 8/10-bit surfaces, source-change handling, drain/EOS and error recovery.
- Compatibility with Frame-specific compressed-surface allocation and capture-buffer import requirements.

The installed module's relevant symbols include `get_hfi_codec`, `vdec_enum_fmt`, `dec_comv_size_iris3`, `dec_non_comv_size_iris3`, `dec_persist_size_iris3`, `iris_int_buf_size_iris3` and `iris_get_buffer_size`. Upstream `iris_hfi_gen2_command.c`, `iris_vpu_buffer.c`, `iris_ctrls.c` and platform tables supply concrete AV1 reference implementations. Their newer interfaces require adaptation to Valve's kernel; copying a modern module into the old kernel is not a backport.

Additional headless decompilation confirms a concrete buffer dependency: installed `dec_comv_size_iris3` handles codec masks 1/2 and returns zero for other masks. Adding a new AV1 codec mask/HFI identifier without implementing its buffer paths would therefore be incomplete.

**Gate:** AV01 must survive format negotiation and decode known clips in hardware: first 512 Ã— 512, then 1080p, then the actual packed 1536 Ã— 6144 layout. Test both 8/10-bit, supported header profile/level/tier and 100/150/200-Mbps cases. Validate decoded pixels, buffer counts/lifetimes and source-change/crop/stride behavior. Then run paced 120-fps input for at least 10 minutes, measuring completion latency, queue age, late frames, resets and thermal throttling. CPU software decode or a successful format ioctl is not a pass.

If the AV1 backend rejects the unusually tall packed image, independently test **3072 Ã— 3072**, which has the same pixel count. A square 2 Ã— 2 region packing is a possible fallback, but requires matching host packing, Frame reconstruction, QP-map geometry and timing-tile coordinates. It is not a resolution-setting substitution. Do not assume HEVC's accepted dimensions establish AV1's limits.

### 2. Implement a PC AV1 backend while retaining Valve's surrounding stream

Use the tested D3D11/API 12.1 implementation as the starting point; a newer supported SDK is also possible. The current Valve encoder requests API 11.1, validates H.264/HEVC and writes HEVC-specific configuration. A codec GUID swap or changed version field is insufficient.

Preferred mod architecture: a session-scoped NVENC adapter at Valve's public function-table boundary, retaining the known legacy ABI toward the driver while creating a separate modern AV1 backend. Translate session opening, presets/capabilities, initialization/reconfiguration, resource registration/map/unmap, picture submission, sequence parameters, output lock/unlock, events and destruction. Validate each legacy/new structure version and reject unknown layouts. Other apps' NVENC sessions must pass through unchanged.

Retain Valve's packed stereo textures, gaze metadata, frame identity, clock/timestamps, bitrate adaptation, keyframe requests and transport. Do not inject AV1 until the paired receiver has passed the hardware gate. Inspect the post-encode path for HEVC NAL/slice assumptions and replace any such processing; opaque transport cannot be assumed from the encoder API alone.

**Gate:** independent adapter tests preserve caller memory and resource lifetime; every submitted frame produces the expected output, restart/reconfigure works, unsupported requests recover to HEVC, and the actual codec/dimensions/bit depth are reported. Repeat the GPU-resident tests during a representative game workload; measure p99 and deadline misses, not only mean throughput.

### 3. Adapt the Frame streaming client and codec agreement

`SVLCodecV4L2::OutputSetup` currently selects HEVC explicitly. Add negotiated AV1 selection and AV1 header/keyframe handling, with the existing capture/Vulkan path retained where its layout is compatible. Validate raw/UBWC formats, DMA imports, synchronization, stride/crop and color range.

Recover the exact VRLink capability/codec agreement and any codec-specific framing before altering it. Generic Steam Remote Play AV1 settings do not establish VRLink compatibility. For the first controlled prototype, a paired, explicit trial configuration can select matching backends on both ends; a general release needs capability agreement and automatic HEVC fallback. Preserve Steam's existing authentication and the dedicated dongle connection.

AV1 uses sequence-header OBUs and keyframes rather than HEVC SPS/PPS/IDR syntax. NVENC can combine leading non-display AV1 frames with the next displayed frame in one output buffer, so the frame-boundary/timestamp path must be tested explicitly. Begin with no B-frames/lookahead and verify displayed-frame ordering. [NVIDIA NVENC guide](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.1/nvenc-video-encoder-api-prog-guide/index.html)

**Gate:** a visible, correctly colored moving pattern reaches the headset at low resolution; then the full packed stream at 120 Hz. Both ends report AV1. Test a missing header, dropped/damaged packets, forced keyframe, reconnect, stop and HEVC fallback. Do not label an AV1 file decoded by a separate tool as an end-to-end VRLink stream.

### 4. Port foveation and codec-aware controls

Translate the HEVC 32 Ã— 32 map (9,216 entries here) into the AV1 64 Ã— 64 map (2,304 entries). Use packed-region geometry and fresh gaze metadata, including mixed boundary blocks. Preserve the timing tile and other protected pixels; do not blindly average sentinel values. Calibrate AV1 quantizer/delta semantics rather than reusing HEVC offsets numerically.

The native test proves the AV1 map is accepted, but its synthetic center stripe is not Valve's gaze map. Validate region assignment and gaze movement against known source frames, then compare center/periphery reconstruction at equal useful bitrate. Inspect frame-average QP separately from center quality. Make CQ/QP ranges, presets, bit-depth, level/tier, padding and accepted-policy telemetry codec-aware. The present helper's API 11.1 metadata checks cannot simply be reused on modern backend calls.

**Gate:** deterministic geometry/protected-pixel tests pass, decoded timing/frame IDs remain correct, and center-image measurements improve or remain comparable without increasing late/drop/recovery events.

### 5. End-to-end acceptance and packaging

Keep 120 Hz, Wi-Fi enabled for discovery and the dedicated dongle as video path. Compare HEVC and AV1 at equal source content and 100/150/200-Mbps budgets, with identical gaze traces. Measure actual video bytes, frame-size bursts, encode/decode p50/p95/p99, receiver queue age, retries, late/drop counts, recovery time, image quality and temperature.

Use at least a 10-minute patterned soak and a longer representative-game run. Exercise SteamVR/client restarts and disconnect/reconnect repeatedly. The desired outcome is better or equivalent center quality with no material increase in latency/stuttering; higher byte throughput is not the objective.

Package codec-aware capability checks, requested-versus-accepted readbacks, an explicit rollback and verified module/client identities. AV1 stays unavailable when the hardware decoder/client bridge is absent, incompatible or unverified. Driver/OS updates must revalidate the mod; future private binary layouts cannot be guaranteed compatible.

## Current limits and next concrete action

The strongest result is a fast, valid, mapped PC AV1 encoder prototype plus evidence of AV1 routines in the Frame's current firmware. The unpassed gate is actual Frame hardware AV1 decoding. The immediate dependency is matching kernel source/config/build assets, followed by a scoped Iris backport and decoder test harness. Until that passes, a production AV1 stream selector would promise functionality that does not exist.

Software decoding remains useful for parsing/reference tests and low-resolution prototypes. The buffered 122-fps result does not establish a usable full-resolution low-latency fallback; the single-frame-delay test measured only about 36 fps.

Live SteamVR settings changed independently during this investigation: the final snapshot differed in runtime PID, preset/AQ/chroma/bit depth and temporary power settings from the restored initial state. The independent AV1 encoder/decoder tests do not set those controls. Avoid overwriting that concurrent work; use a coordinated, frozen stream profile for later quality comparisons.

## Reproducible local evidence

Files in `artifacts/AV1Lab/`:

- `gpu_probe.cpp`, `build_gpu.ps1`, `nvEncodeAPI.h`: standalone public-API prototype and build dependencies.
- `gpu-default.json`, `gpu-map.json`, `gpu-default-paced.json`, `gpu-map-paced.json`: direct GPU timing results.
- `gpu-default-probe.json`, `gpu-map-probe.json`, `gpu-stream-info.json`: decoded frame counts and stream metadata.
- `pc_encode_tests.py`, `pc-tests.json`: FFmpeg functional/capability tests.
- `frame_capability_probe.py`, `frame-capability-tests.json`: real enumeration, raw TRY_FMT, installed kernel/device metadata.
- `frame_decode_tests.py`, `frame-software-tests.json`, `frame_followup.py`, `frame-followup.json`: software throughput/delay, firmware hash and kernel source identity.
- `linux-reference/`, `source-inventory.json`: upstream source snippets with blob/SHA256 provenance; not the matching Valve source tree.

Run the direct probe from that directory with `gpu_probe.exe default paced` or `gpu_probe.exe map paced`. The SDK header is from the locally installed Unreal Engine NVIDIA integration and retains NVIDIA's original license; it is not part of the streaming app package. Proprietary Valve/firmware binaries are not redistributed.
