# AV1 implementation status

2026-10-12. **The paired AV1 prototype renders a visible SteamVR image on the Frame over the dedicated dongle at 120 Hz.** The final 615.86-second test averaged 119.92 encoded fps, with zero encoder errors and zero new compositor dropped frames. A Frame compositor screenshot shows the PC's moving diagnostic pattern after hardware decoding. Encoded video averaged 76.52 Mbps; dongle receive traffic averaged 100.74 Mbps including other traffic/overhead. Wi-Fi stayed enabled, with home Wi-Fi and USB video inactive. One late-metadata event and 73 timed-out compositor presentation events were recorded, so this is not a claim of zero transport/presentation anomalies. The 8-bit, 100-Mbps-budget paired prototype is verified; general negotiation, 10-bit capture and quality comparisons remain development work.

## Kernel identity and source access

The running Frame kernel is `6.18.0-ge66bc2ca6f8c`, package `linux-618-deckard 6.18.0+ge66bc2ca6f8c-1`. Its package source URL identifies Valve commit `e66bc2ca6f8ca6baa5260c69c9b79dc01a6238b9`. The exact source repository/archive requires sign-in; guessed source packages returned 404. The package's license metadata also includes `no-source-release`. This investigation did not obtain the matching Valve source tree.

The Frame's configured Valve package mirror did supply the exact kernel and headers. Both were downloaded and SHA256-verified into the isolated Frame directory `/tmp/steam-frame-av1-lab-20261011/kernel`, without package installation:

| Package | SHA256 |
| --- | --- |
| Exact kernel | `2550877b669b57a5ba16eb1b5736a792a9e18cc20d36d69ff8180f3024c902b3` |
| Exact headers | `1be8af9b51c6d8ca33d03228d3c1045a423e2f8cd81989edc1641b38441f8810` |

The headers contain the matching config, `Module.symvers`, generated headers and `vmlinux`. GCC on Frame matches the kernel compiler (`15.1.1 20250729`). Mirror credentials/opaque access paths are excluded from saved/public source references.

## Frame implementation

`Frame/AV1/prepare_iris_candidate.py` ports public Linux 7.0 Iris source, pinned to `028ef9c96e96197026887c0f092424679298aae8`, for an out-of-tree compile against those exact headers. This is an upstream decoder port, not Valve's matching source. It retains `qcom/vpu/vpu33_4v.mbn`, introduces the missing stateful `AV01` FourCC, replaces newer object-allocation shorthand with the 6.18 allocator, and includes the public 6.18 V4L2 mem-to-mem helper because the Frame kernel disables that framework. Optional module BTF generation is disabled because the Frame has no `pahole`; kernel config and installed modules are not changed.

The full AV1 path includes codec ID 7, profile/level handling, firmware properties, AV1 COMV/line/bin/persistent/partial buffers, source-change and drain processing. Preparation checks for the AV1 source features before building. The first 6.19 compile experiment was only a base-port check: that tag lacked AV1, and its module must not be used as an AV1 candidate.

The first AV1-capable module (`d81498c1c00a7d6a6148cb6cad95c67d37ab32534243caf4d3df2fd724269b2c`) built with matching vermagic, loaded while disabled, and subsequently bound when armed. Opening its decoder failed because its PAS firmware path differs from this Frame's existing non-TZ `video-firmware` integration. The stock driver was restored successfully.

IDA headless then identified the stock `use_tz`, `load_fw`, `init_fw_no_tz`, `auth_reset_fw_no_tz`, `reset_cpu_no_tz`, suspend and unload paths. `Frame/AV1/frame_firmware_no_tz.c` ports that board integration: a separate firmware platform/IOMMU device, `qcom_mdt_load_no_init`, the existing reserved-memory mapping and verified local reset-register sequence. Public [Linux 6.18 Venus firmware source](https://github.com/torvalds/linux/blob/v6.18/drivers/media/platform/qcom/venus/firmware.c) is an additional lifecycle reference. No firmware image is substituted.

The revised candidate boots the existing firmware and decodes AV1. Its current SHA256 is `c83ea764512488af7e9c3a357d78c21a4f32d4d193c39de01bb075226d440339`, with build/module receipts under `artifacts/AV1Lab/kernel-capture-noop-build/`. The port also supplies the AV1 format description missing from the 6.18 generic ioctl table, preventing the observed unknown-FourCC enumeration warning. Decoder clock/interconnect votes now use 120 fps; the upstream defaults had assumed 30 fps. Existing OPP/session limits remain in force. No automatic alias, default probing, kernel-release or board guard was removed.

IDA headless decompilation of the installed `hfi_packet_sys_init` and ELF object inspection found its seven UBWC values `(8,32,16,0,1,1,1)`, matching the candidate's platform values. Current device-tree clock, reset, power-domain and interconnect names agree with the upstream SM8650 names. These are useful compatibility checks, not complete board validation.

`v4l2_av1_probe.cpp` is compiled on Frame and tested through a separate descriptor. It explicitly checks the returned FourCC and dimensions. The installed driver still enumerates H264/HEVC/VP90 and returns H264 320×240 for every AV01 request, including 512×512, 1080p, packed and square layouts. No buffers or streams are started by this probe.

`stage_client_codec.py` creates a separate, hash-guarded Frame client trial binary. IDA located the two instructions that build the HEVC FourCC in `OutputSetup`; the trial changes only those instructions to AV01. Original client SHA256 is `31addef0c9553d3770b5570487699e96d70271d4073cd6c03b080fdee6ae2153`, candidate is `59231d5b681022d56c697583ccc3ecdc0f32b97cf6110b3d15ad33b8e819d731`. It is activated by an ephemeral bind mount for the paired AV1 trial. This paired trial override does not implement general codec negotiation, and does not establish that all remaining client paths accept AV1.

## PC implementation

`Native/AV1Lab/` now builds a separate `SteamFrameAV1Backend.dll` using the public NVENC 12.1 header. It implements GPU texture input, synchronous low-delay AV1 encoding, 8/10-bit output, AV1 delta maps, keyframes with repeated sequence headers, dynamic bitrate reconfiguration, timestamp checks, counters and resource ownership/cleanup. It caches up to 16 textures and supports BGRA, NV12 and P010 input. It requires 120 fps, disables B frames/lookahead/AQ/padding, and uses P2 ULL and a one-frame VBV. It is not linked into or loaded by the production helper.

The trial requests level index 17/high tier for the full packed test. Those output sequence headers were verified. The SM8650 vendor table reports AV1 up to level 6.1/high tier. Small 512×512 trials with high requested rates reject explicit levels as `Invalid Level`; AUTO succeeds but emits unconstrained level 31. Those small clips cannot yet be assumed acceptable to the hardware decoder. The level is an explicit lab config field so this behavior can be tested rather than silently assumed. Average/peak budget requests are bounded to 480 Mbps for this level-6.1 trial; this is not measured headset throughput.

The ABI probes compile SDK 11.1, 12.1 and the supplied OpenVR project's 13.1 headers separately. SDK 11.1/12.1 picture sizes are 3,344/3,360 bytes; the QP-map pointer moves from byte 1,696 to 1,712. Blind prefix copying is insufficient. `legacy_picture.cpp` extracts common picture fields through the correct SDK 11.1 layout and rejects unknown versions/sizes; canary checks prove caller preservation.

`legacy_api_bridge.cpp` now builds an isolated SDK-11.1-facing function table over actual NVENC 12.1 sessions. It translates initialization/reconfiguration, pictures, lock results, resources and async events, caps the paired AV1 trial to 100 Mbps average/120 Mbps peak, and uses a native AV1 P2 preset rather than reinterpreting HEVC codec configuration. AV1 color-description defaults must be retained; zeroing the codec union caused an invalid-color error and was fixed. The bridge currently uses 8-bit AV1, level 6.1/high tier and 120 fps. A provisional 32→64-pixel QP-map reduction preserves the timing-marker block at high quality; its numerical QP scaling is not yet a validated HEVC/AV1 quality equivalence.

`legacy_gpu_test.cpp`, compiled against the real SDK 11.1 header, successfully opens this table and encodes 120 AV1 frames using both synchronous and asynchronous completion. It exercises resource registration/mapping, rate reconfiguration and output locks, checks picture/lock canaries and preserves caller open/init/config/picture parameters. Both outputs independently decode on PC. These are isolated functional tests; the adapter is now injected into the real SteamVR process for the paired trial, and unsupported API coverage, broader structure audits, real-driver integration, timing-marker rendering and in-game foveation/quality remain gates.

## Supplied projects

- `E:\Github\CustomHeadsetOpenVrGxR`, commit `a5e8cc9`: `NvencTap.cpp` provides a useful session-upgrade and resource-bookkeeping reference; `NvencPostPack.cpp` shows packed NV12/P010 texture handling and shader insertion. Its comments describe measured HEVC packet-size, AQ and preset behavior for Galaxy XR. They are not measurements of this Frame. The header/ABI audit above identifies the shifted QP-map field that our bridge must handle explicitly. No files in that repository were changed.
- `E:\Github\video-driver`, commit `269addef`: the Qualcomm SM8650/pineapple platform identifies `vpu33_4v.mbn` and contains complete AV1 buffer and HFI implementations. `driver/variant/iris33/` adds AV1-specific performance/clock modeling and buffer counts. The vendor table supports AV1 Main through level 6.1, high tier, NV12/P010/UBWC and dimensions up to 8192, subject to pixel-rate and session limits. Its Android `sm8650-vidc` device-tree binding and external resource dependencies differ from the Frame's `sm8650-iris` binding. It is a valuable firmware/backport reference, not a drop-in install. Its `MAX_BITRATE` constant applies to encoder controls; it is not a measured AV1 decoder bitrate ceiling. No installer or boot-blacklisting script was run.

## Tests and remaining gates

| Test | Result |
| --- | --- |
| Legacy picture snapshot | Size/version rejection, pointer extraction, caller/canary preservation passed. |
| Legacy SDK 11.1 function-table bridge | 120 AV1 frames in each sync/async test; completion events signal; resource/map/reconfigure calls succeed; picture/lock canaries intact; independent PC decode succeeds. Real SteamVR injection also tested. |
| NV12 8-bit input/output and P010 10-bit input/output | 120 AV1 frames each, zero encode errors; both files hardware-decoded on PC. Not paced tests. |
| First packed 600-frame backend test | All frames encoded and independently decoded; rate changed 200→150 Mbps; zero encoder errors; p99 7.25 ms, 7 deadlines missed. |
| Cached-resource packed 1200-frame test | Zero encode errors; p99 9.07 ms; 333 deadline misses. Does not pass sustained 120 fps. |
| Explicit level-6.1 packed 120-frame test | Zero errors; p99 7.51 ms; one deadline miss. Short test only. |
| Dongle, 512×512, 1200 AV1 frames | All 1200 received and software-decoded with no identity errors. About 11.7 seconds elapsed, with 751 sender deadline misses. Functional transfer passes; 120-fps pacing fails. |
| Frame AV1 hardware, 512×512, 2400 frames | Paced 120 fps for about 20 seconds; no decode, identity, empty or missing-frame errors; exact first-frame golden match; p99 1.60 ms. |
| Frame AV1 hardware, 1920×1080, 1200 frames | After harness padding/timing repairs: paced 120 fps, no input deadline misses or decode errors; exact golden match; about 10 seconds, p99 7.04 ms. |
| Packed AV1 PC source, 1536×6144, 1200 frames | 8-bit, level 6.1/high tier, target 200→150 Mbps; zero encoder errors/deadline misses; p99 4.71 ms. |
| Packed Frame AV1, original 30-fps votes | All 1200 decode correctly, exact golden match; 19.77 seconds, about 61 fps; fails 120 fps. |
| Packed Frame AV1, revised 120-fps votes | All 1200 decode correctly, exact golden match; 12.93 seconds, about 93 fps; 1171 input deadlines missed; fails 120 fps. |
| Packed Frame AV1, 4×4 tiles / linear NV12 | All 1200 decode correctly, exact golden match; 13.26 seconds; fails 120 fps. |
| Packed Frame AV1, QC08C compressed capture | All 1200 returned without errors; 12.91 seconds; fails 120 fps. Pixel comparison of compressed capture is not yet implemented. |
| Packed Frame AV1, 100→75 Mbps / linear NV12 | All 1200 decode correctly; exact first-frame golden match; no input deadline misses; 10.006 seconds; Frame-side 120-fps pass. p99 queue-to-output 14.51 ms is not end-to-end latency. |
| Live dongle hardware path, 1200 frames at 100→75 Mbps | PC produces packed 8-bit AV1, binds `10.35.78.21`, Frame listens at `10.35.78.1` and hardware-decodes as data arrive. All frames decode without errors; first-frame SHA matches independent PC decode. One PC deadline miss with multimedia scheduling; Frame-side zero misses. |
| Live dongle hardware path, 4800 frames at 100→75 Mbps | About 40 seconds, all frames received/hardware-decoded, no decoder or identity errors, no missing frames, exact first-frame pixel match. PC p99 3.78 ms and one late submission; Frame p99 queue-to-output 15.29 ms and zero late inputs. Overall strict zero-miss pass is false. No display/VRLink acceptance. |
| Live dongle hardware path, 80→60 Mbps | All 1200 frames decode without errors and pixels match. One PC deadline miss; Frame-side zero misses. Lower bitrate alone did not eliminate the last PC miss. |
| Visible AV1 through SteamVR/VRLink | Frame compositor screenshots show the PC moving pattern; a 615.86-second paired dongle run averages 119.92 encoded fps, zero encoder errors and zero new compositor dropped frames. |

The dongle sender binds to PC `10.35.78.21`, connects to Frame `10.35.78.1`, and uses the Valve Realtek adapter. Frame status shows the dedicated type-2 link active, home-Wi-Fi and USB video links inactive, with Wi-Fi enabled. The test receiver accepts that paired PC only. It software-decodes to frame hashes; it does not display the test or measure headset refresh, motion-to-photon latency or subjective quality.

An existing HEVC/SteamVR stream was concurrently active during later tests (encoder utilization remained nonzero after the AV1 tests). Its runtime PID/profile changed independently. These results are not isolated encoder performance measurements, and no production settings were restored over those concurrent changes.

Administrator authentication was subsequently verified through sudo over the paired SSH connection. The authorized password was sent through hidden input/stdin, never put in command arguments, source files or logs. The test helper exposes a bounded set of lab actions through an ephemeral privileged SSH session and restores the stock driver on orderly exit if a trial is active.

The candidate loaded and unloaded successfully **with probing disabled**, while the stock `qcom-iris` driver and its existing VRLink decoder descriptor stayed active. Exact module/firmware fingerprints and temporary root-owned rollback copies were checked. Later authenticated trials replaced the active decoder temporarily and successfully rendered AV1 through VRLink.

At 00:17:51 America/New_York on October 12, Windows logged a requested disconnect of the Valve WLAN interface (reason code 2). That reason can be produced by software; it does not establish a physical/user action or a kernel fault. The dongle then reported disconnected, lost its IP address, and the SSH session ended with a connection reset. This happened before any stock-driver unload or AV1 probe. Reconnection is required for the next hardware test.

The small hardware trial was corrected to 512×512, 8-bit, 120-fps input at 5 Mbps. NVENC then accepts explicit AV1 level 4.0/high tier (sequence-header index 8/tier 1 verified). The high-rate small trials' level failures are not an inherent inability to encode a constrained low-resolution AV1 clip. A decoded NV12 golden frame is saved for pixel comparison.

`hardware_decode.cpp` now compiles with strict warnings on both the Frame and a Linux host. It checks AV01 without codec fallback, distinguishes coded padding from visible dimensions, maps input/capture queues, handles source-change and explicit drain, checks all frame timestamps/counts, paces at 120 fps and captures the first frame. Linear NV12 padding is removed before an exact golden-image comparison. Reference-file reading/writing happens outside the timed decode loop. Host/native padding and root-session regression checks pass. QC08C is a separate timing mode and explicitly reports that pixels are unverified. The upstream 7.0 capture path initially exposes NV12/QC08C; 10-bit capture integration remains additional work.

Its `tcp` mode is a separate live hardware receiver. It binds only the Frame's dedicated-link IP, accepts only the paired PC address, receives with timeouts and a bounded read-ahead queue, and releases compressed-frame memory after submission. `run_hardware_dongle_test.py` coordinates the PC and Frame, then independently decodes the actual transmitted source's first frame on PC and compares SHA256 to the hardware capture. It reports functional decoding, source/decoder timing and display/VRLink status separately. This is not the earlier software/libdav1d dongle receiver.

The PC benchmark uses its own Windows multimedia scheduling registration, a short final cadence wait and GPU-shader warmup. No machine-wide scheduling or clock setting was changed. Subsequent reports include the IDs of late submissions and distinguish first-frame startup from steady deadlines; those fields have not yet been collected in a new live trial.

Stock rollback was verified again after the live tests: `qcom-iris` is bound, the AV1 module is unloaded, original module/firmware hashes are unchanged, and the private root session is closed. SteamVR needed a fresh Steam-managed launch after its old connection became stale. The ordinary VR stream and compositor are connected again with Wi-Fi enabled.

Next gates: audit/complete the PC adapter's API coverage and memory contracts; test it against the real SteamVR caller; activate the separately staged Frame client codec trial with temporary rollback; verify decoded timing markers/foveation/rendering and visible AV1 at 120 Hz over the dongle; add 10-bit capture and longer thermal/recovery soaks. The actual Valve source tree still requires repository access. No installed Frame kernel, firmware or VRLink binary was replaced.


## Paired SteamVR acceptance and recovery

The factory interception must be installed before VRLink builds its encoder table. A late injection misses cached tables. The existing SDK-11.1 HEVC monitor cannot coexist with the modern AV1 adapter; the updated app reads AV1 counters through `Av1ReadTrialStats` and never installs those HEVC hooks while AV1 is active. Its monitor start/stop/restart was tested against the running adapter: counters advanced, stopping monitoring did not stop encoding, and only the AV1 DLL remained loaded.

The host initializes at its advertised maximum dimensions before selecting the packed stream. The adapter bounds those maxima to 1536×6144, advertises 8-bit input, and translates measured-rate reconfigurations to the nominal 120-fps AV1 session. Otherwise the initial 3200×8192 allocation fails explicit AV1 level validation, and 118/121-fps rate updates would be rejected by an overly strict adapter.

Frame compatibility changes retain the stock `iris_decoder` query-capability card name because `vrdevice_path video_decode` relies on it. Capture S_FMT now accepts an exact unchanged AV1 capture layout only after STREAMOFF, retaining existing imported DMA buffers; changed layouts still require buffer release. Firmware child/IOMMU cleanup also runs on removal after an error state. A guarded one-shot recovery module released an orphan child from an earlier test, allowing the stock driver to recover without reboot. It was unloaded afterwards. Subsequent repeated load/query/idle/reload/rollback cycles succeeded.

The final controlled run uses the `c83ea764512488af7e9c3a357d78c21a4f32d4d193c39de01bb075226d440339` candidate and the hash-guarded two-instruction AV01 client. `artifacts/AV1Lab/steamvr-soak-capture-noop-motion-10min.json` contains the complete counters: 615.86 seconds, 119.92 encoded fps, 76.52 Mbps encoded video, zero encoder-error delta, zero new compositor dropped frames, 73 compositor timed-out presentation events and one receiver late-metadata event. Those timeout/reprojection counters are not unique network losses. `frame-av1-capture-noop-motion.png` is a Frame compositor screenshot of the PC-generated pattern. The dongle carried the video, home Wi-Fi received about 0.02 Mbps background traffic, and USB carried none. Peak sampled temperature was 62°C; this is a short validation, not a gaming/thermal guarantee.

Current scope is a paired 8-bit lab prototype with a 100-Mbps encoder-budget ceiling, not a general AV1 capability-negotiation release. The actual encoder budget can be lower because the host controller changes it; no filler or minimum transmission rate is forced. Ten-bit capture, calibrated gaze/QP quality comparison, long gaming soaks and generic build discovery remain future work. Installed firmware, kernel/module boot files and original client/host driver bytes are retained.

The paired launcher also passed a one-minute start/automatic-restore cycle: it confirmed an open Frame AV1 decoder and initialized PC adapter, restored the original client and stock decoder at expiry, and restarted ordinary HEVC streaming. A new supervised 90-minute AV1 session was then started successfully, with the updated settings application open. Wi-Fi is enabled, only the dedicated type-2 link carries the VR stream, and only the AV1 adapter is loaded in the host; the HEVC monitor is not injected. The launcher restores HEVC at SteamVR exit or expiry. The Frame's original five-second/standby power policy was restored after the synthetic awake leases. Passwords were consumed through hidden stdin and not saved. The original host driver, Frame client boot file, stock module and firmware fingerprints remain unchanged.

## Explicit 200 and 100 Mbps trials, 2026-10-12

The temporary paired launcher now accepts `--mbps`, `--peak-mbps`, and `--fixed-target`. The default remains the previous adaptive 100/115 Mbps ceiling. Fixed target pins the NVENC VBR budget through host reconfigurations; it does not add padding or force a minimum transmitted bitrate. Configuration has size/version/range guards and cannot be changed with an encoder open. The controller verifies its remote argument. The packed SDK-11.1 async bridge test at a fixed 200/200 Mbps target passed initialization, events, caller/canary preservation and reconfiguration; its actual AV1 file was independently decoded on PC. The small 512-square fixture cannot request that rate at the selected level's compression-ratio constraint, so the 200 Mbps regression uses the real 1536×6144 layout.

In `steamvr-soak-fixed200-motion-first.json`, 61.67 seconds of moving detail at an accepted 200/200 Mbps budget produced only 69.14 new encoded fps and 115.20 Mbps actual video. The Frame compositor continued presenting at 120 Hz, with zero new dropped-frame counter increments but 489 timed-out presentation events. Dedicated-link receive traffic was 157.82 Mbps including overhead; home Wi-Fi carried only background traffic and USB carried none. Scoped steady receiver logs showed no decoder reset/stuck/buffer errors and ten FEC retries. These counters do not prove the decoder alone caused the slowdown. This setting fails the fresh-frame 120-fps goal and was removed; stock client/module recovery was confirmed before starting the next trial.

The user then requested 100 Mbps for personal viewing and disabled the custom foveation override. The saved `QualityEnabled` preference is false, and the fresh AV1 process has no HEVC quality helper. The adapter still translates Valve's original quality map to AV1; native gaze streaming is retained. `steamvr-soak-fixed100-motion-ready.json` measures 41.07 seconds at an accepted 100/100 Mbps target: 96.61 Mbps actual video, 116.18 new encoded fps, zero encoder errors, zero compositor dropped-frame delta, and 396 timed-out presentation events. Dedicated-link receive traffic was 121.05 Mbps including overhead, with no interface receive errors/drops; home Wi-Fi was 0.02 Mbps and USB zero. Scoped logs showed no decoder reset/stuck/buffer errors or FEC retries. The Frame compositor screenshot `frame-av1-fixed100-motion.png` proves that the PC pattern reached the display. This is a brief objective check, not a claim that the fixed target sustains 120 new fps or is subjectively smooth.

The pattern was cleared for the user to wear the headset and judge a real scene. The 100 Mbps temporary pair is supervised for up to 90 minutes, with HEVC restoration on stop/expiry. This supersedes the previous running-session description; the old 90-minute trial had already ended before these tests.

The user subsequently requested a 90 Hz display. The paired adapter now accepts explicit nominal 90 or 120 fps; version-2 trial arguments include the rate, active-session changes remain rejected, and the launcher verifies initialization at that rate. GOP/IDR interval and one-frame VBV follow the selected rate. The packed async SDK bridge test at 100 Mbps/90 fps passed and its output decoded independently. A live settings helper selected `steamvr.preferredRefreshRate=90` on PC and Frame; the actual HMD display-frequency property read 90.000008 Hz on both after reconnect. The decoder retains the previously tested higher performance votes; no kernel rebuild was required.

`steamvr-soak-fixed100-90-motion.json` records the brief moving-scene comparison: 41.03 seconds, 85.19 new encoded fps, 94.67 Mbps actual video, zero encoder errors/dropped-frame delta, and two compositor timed-out presentation events. Dedicated-link receive traffic was 129.45 Mbps including overhead, with no interface receive errors/drops; home Wi-Fi was background only and USB zero. The 90 Hz display is confirmed, but the current fixed 100 Mbps setup does not yet deliver every one of the 90 new frames per second. The test pattern and temporary awake lease were cleared/restored for personal viewing. The SteamVR requested target was also set to 100 Mbps so the app's requested target agrees with this trial.

The app now displays verified codec evidence with explicit Waiting/Unknown/stopped states, uses successful NVENC codec GUIDs for HEVC monitoring, and only labels AV1 after successful initialization plus advancing AV1 output counters. Auto-reapply skips the incompatible HEVC hooks for AV1. Read-only live tests against the 90-fps AV1 session verified first-sample Waiting, active AV1, stopped Unknown with last-verified AV1, fresh baseline on resume, then confirmed AV1 again while encoding continued. Six native tests and 24 managed core tests passed. The portable app was rebuilt and opened after the live test.

## v1.2.0 bundled codec companion acceptance, 2026-10-12

The codec-enabled portable package now contains the supervised companion, its
isolated CPython runtime, the PC NVENC adapter and the exact-build AV1 module with
corresponding GPL source. The C# controller stages the Frame assets through the
configured trusted SSH connection and supplies the user password only through
private standard input. The root helper generates AV1/H.264 client patches from
the Frame's own hash-checked original binary. End users do not need the historical
lab's manually staged client copies, PC Python installation or module build.

The final packaged C# controller started an AV1 session using only its bundled
runtime/assets. Fresh output measured approximately 99 Mbps and 89.6 encoded fps
at a 100 Mbps request and 90 Hz display over the dedicated dongle. This is a brief
functional check, not a sustained-performance guarantee. The GUI's Switch codec
action then selected H.264 successfully, with fresh verified H.264 8-bit output
at 1024×4096. Its earlier 41-second moving-pattern trial measured 89.907 encoded fps
and 68.636 Mbps actual video at the 100 Mbps/90 Hz request, with zero encoder errors,
zero new compositor dropped frames and two presentation timeouts. The dongle
received about 95.94 Mbps including overhead; home Wi-Fi carried background traffic.

The GUI's Advanced Supersample Filtering toggle was disabled and confirmed through
live SteamVR readback and the saved `steamvr.allowSupersampleFiltering=false` value.
The bitrate request and stream width were retained by that operation. The GUI's
Restore HEVC and end session action returned a successful closed-session receipt,
and the dashboard verified stock HEVC 10-bit output. Frame inventory confirmed the
original client fingerprint and `qcom-iris` decoder. A subsequent fresh AV1 startup
from the same bundle passed again. No constrained-encoding fatal error or adapter
measured-rate reconfiguration error was observed in these final checks.

The PC adapter now answers HEVC-facing legacy capability queries consistently while
validating the selected native codec separately. This fixes the old driver's
`NV_ENC_CAPS_SUPPORT_CONSTRAINED_ENCODING` check against AV1's different capability
surface. Measured throughput updates such as 65/300 fps retain the user's selected
nominal 90/120-fps cadence. Four async bridge regression configurations (AV1 and
H.264, each at 90/120 fps) passed caller/canary preservation and reconfiguration;
their actual output bitstreams independently decoded on PC. The WinUI build,
33 core tests and the native/companion checks passed.

This remains an exact-build experiment: kernel `6.18.0-ge66bc2ca6f8c`, client SHA256
`31addef0c9553d3770b5570487699e96d70271d4073cd6c03b080fdee6ae2153`, the pinned
module/firmware fingerprints, NVIDIA NVENC and the dedicated dongle are required.
AV1 is 8-bit, packed up to 1536×6144; H.264 currently uses the smaller 1024×4096 image.
The UI offers 25–200 Mbps and 90/120 Hz, but the earlier fixed 200 Mbps AV1 trial
failed the requested fresh-frame rate. No filler is added and no minimum emitted
bitrate is promised. General codec negotiation, unknown Frame builds, 10-bit AV1
capture and calibrated AV1 foveation quality remain outside this release.
