# H.264 and multiple decoder streams: implementation and evidence

This is an offline audit of retained source and completed experiments through
2026-10-14. **There is no validated multistream replacement that preserves the
current native Main10/foveation representation and delivers steady useful
350 Mbps with smooth pacing.** The existing game prototype is H.264; independent
HEVC-session tests are local capacity experiments without a game renderer.
No live change or new performance test was made for this audit.

## Three paths that must not be conflated

| Path | Implemented representation | Established boundary |
| --- | --- | --- |
| Current native stream | One packed Main10 HEVC picture/session, currently 1664 x 6656; host requests four constrained slices | Slices are not independent decoder sessions. Emitted headers and request state are separate evidence. |
| Existing game multistream prototype | Two or four H.264 sessions, public left/right SteamVR mirror textures, NV12 DMA-buffer composition and per-eye projection overlays | A different image/composition path, not a drop-in split of the native packed foveated frame. |
| Local HEVC comparison | One/two/four stock V4L2 sessions decoding saved Main10 clips to QC10C | Measures local completion/pacing without network, game composition or panel presentation. |

The game sender hard-codes H.264 High profile, CBR, one-frame VBV, P2/ultra-low
latency, one slice per region, three references and no filler. CAVLC is the
default; `--cabac` selects the alternative. Its current game geometry is two
2048 x 2048 or four 1024 x 2048 regions: 8,388,608 total pixels. The native
1664 x 6656 layout has 11,075,584 coded luma samples, arranged for Valve's
foveation renderer. These totals do not establish a perceptual-quality or
eye-resolution ratio between the representations.

The prototype retains a 25 Mbps native H.264 connection for tracking, audio and
controllers while adding its image path, so another native decoder workload
remains. Public gaze was invalid in the retained measurements and the prototype
quality map was uniform. Current transport framing names H.264, and composition
expects NV12. A HEVC implementation would need codec-aware framing/NVENC setup,
Main10 import/composition, group/pose ordering and surface-lifetime validation.
Preserving the native packed representation additionally needs compatible
region reconstruction and foveation rendering. Changing a codec GUID is
insufficient.

## What the H.264 experiments actually established

The original native H.264 startup problem was a buffer-contract mismatch:
the driver returned 18 capture buffers while the client backed 16, followed
by QBUF index-16 errors. A three-reference SPS/VUI restriction was added to
keep the requirement compatible. This was an integration defect, not evidence
of a 150, 350 or 400 Mbps hardware ceiling.

The current native adapter explicitly limits H.264 to 1024 x 4096 and 8-bit,
and uses CAVLC above 200 Mbps. This audit did not recover a saved GPU capability
response proving that 1024 is a hardware width maximum. That adapter clamp
must not be presented as hardware capability. CAVLC is already selected in
the high-rate path; enabling it again is not a new fix. HEVC has no equivalent
CAVLC option in these sources.

| Retained experiment | Result | Interpretation |
| --- | --- | --- |
| Native corrected H.264 USB3 soak | 61.588 s, 274.783 useful encoded Mbps, 89.985 FPS, one compositor drop, 266 presentation timeouts; accepted average/peak 400 Mbps | Usable transport, but not a smooth 400-Mbps pass. VBR without filler does not guarantee the target becomes coded bytes. |
| Local one-session CAVLC, 1024 x 4096 | 900/900 decoded frames in 9.99971 s; 399.590 processing Mbps; p99 7.046 ms; no errors/deadline misses | Short local decode success near 400, without network, rendering or current native geometry. |
| Local one-session CABAC | 900 frames in 10.7766 s; 795 input deadline misses; p99 106.393 ms | Entropy-mode sensitivity is supported, but source rates differed: CABAC 412.192 versus CAVLC 399.579 Mbps. This is not an exactly bitrate-matched causal comparison. Two/four CABAC sessions also failed pacing. |
| Synthetic two/four-session TCP | About 403 Mbps; all 900 groups decoded/composed; p99 submission interval about 20.7/20.4 ms | Byte throughput succeeded while pacing failed. Synthetic aggregate geometry was 4096 x 1024. |
| Synthetic isolated UDP | 402.431 Mbps; 900 matched groups, 899 after bootstrap; 89.9998 composed FPS; p99 interval 11.889 ms; zero presentation drops or clock resets | A short synthetic timing success with overlay disabled. It was not a game/panel result and used the older eight-data/one-parity transport. |

The earlier physical link initially enumerated at USB High-Speed. After
unplug/replug it enumerated at SuperSpeed and the same two-session workload
rose from roughly 237 to 403 Mbps. That was a real negotiated-bus bottleneck,
already corrected; a registry preference alone did not establish bus speed.

## The closest retained Home result still failed the goal

`home-cbr-bootstrap` used four 1024 x 2048 streams and a requested 465 Mbps
budget. It delivered **354.304 Mbps**, with 900 matched groups, 896 rendered,
three dropped presentation sets and p99 presentation interval **11.926 ms**
over approximately ten seconds. Seven source captures were skipped, five
sender deadlines missed and gaze was invalid. Its functional check passed,
but its throughput threshold was 400 Mbps and its throughput/presentation
latency checks failed. A short average rate above 350 is not a sustained,
zero-drop or uniquely scanned-out frame result.

The outer comparison found matching first decoded-region hashes for all four
regions against FFmpeg. Per-decoder `golden_checked` was false; neither fact
proves whole-stream image correctness. The run also predates the documented
projection correction. Its build record reports successful builds/tests but
contains no source or executable hashes, so hashing today's files cannot bind
that historical run to the current binary.

A higher-budget Home TCP run reached **421.059 Mbps** and decoded all 900 groups,
but dropped **103** presentation sets with **55.805 ms** p99 interval. A Home
UDP run recorded a Windows send call taking about **132 ms**, followed by a
bounded send-queue failure and downstream decoder workers waiting for input.
That ordering identifies upstream delivery failure, not a hardware decoder
Mbps ceiling. MMCSS did not remove it. These packet pacing/FEC/segmentation
choices are original prototype work, not a verified copy of Virtual Desktop's
transport implementation.

The inverse-moving duplicate overlay was traced to transform direction and
corrected to world-to-eye inversion, with rigid-pose tests and three rotating
output textures. Math tests and API acceptance are not headset visual or
compositor-lifetime proof. The renderer's `glFinish` bounds input DMA use, but
its function has no explicit compositor-consumption fence for output textures.
The playout policy also expires incomplete matches, drops from a bounded queue
and rebases after lateness. Those behaviors must remain visible in counters
rather than being hidden by average displayed FPS.

## HEVC sessions and hardware-stage claims

The saved equal-total-area Main10 comparison at 2048 x 8192 produced slowest
complete-group rates of **84.333 / 84.342 / 78.822 FPS** for one/two/four sessions.
All frames decoded, but all pacing gates failed. Source coded rates were
305.369 / 302.936 / 300.088 Mbps; those source rates are not time-normalized
gameplay throughput. There was no network, composition or independent Main10
pixel verification. This neither establishes a universal decoder limit nor
supports assuming that additional sessions improve throughput.

The installed Iris driver already requests STAGE2 for the current image and
PIPE4 from its decoder capability. Those values do not prove four physical
engines execute concurrently. The audited interface exposes no public V4L2
control for either, and decoder S_PARM is rejected. There is no established
switch that unlocks spare engines by requesting a second stream.

Likewise the roughly 3.35 ms input-response-to-output-response handling gap in
the [matched trace](receiver-stream-map.md#matched-thirteen-event-observation-small-observed-lock-and-pm-spans)
is not an entropy-versus-reconstruction timing split. The input event follows
buffer index/state matching; output handling follows HFI message dispatch.
Neither carries a hardware-stage completion clock. Firmware may pipeline
frames and messages may wait before host handling. The short observed
queue-event-to-post-submit interval narrows the synchronous producer path,
conditional on successful submission, but does not establish hardware service
time or response-ready time.

## Current-geometry comparison: completed tests missed 90 Hz

A private extension completed the one/two/four stock-decoder comparison at
**1664 x 6656 Main10** total geometry. All regions decoded all 900 pictures,
but none passed the 90-Hz capacity or cadence criteria. The tracked comparison
tool and underlying decoder remain unchanged; the experimental wrapper stays private.

| Sessions | Region size | Actual independent slices per picture | Source coded Mbps at 90 Hz | Complete-group Hz | Coded Mbps in completion window |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | 1664 x 6656 | 4 | 351.258 | 74.494 | 290.726 |
| 2 | 1664 x 3328 | 2 | 350.036 | 74.270 | 288.780 |
| 4 | 1664 x 1664 | 1 | 349.099 | 71.772 | 278.431 |

Distinct contiguous bands came from the same deterministic full-frame source.
Every calibration attempt matched its full-source digest. Actual coded bytes
were within 0.5% of 350 Mbps, with a 0.618% cross-configuration spread. All seven
selected clips passed software decode without filler. Validation covered 630
access units and 1,080 slice segments: minimum CU16, CTU32, no tiles or WPP,
cross-slice filtering enabled, and four total independent slices per source
group. Each slice spans 2,704 CTUs. The pure syntax reader and FFmpeg header
trace agree. The 400-Mbps plan remains unexecuted in this comparison.

The synthetic pattern begins as 8-bit content converted to P010; neither native
foveation nor independent 10-bit source detail is represented. Preparation used
VBR with multipass disabled, rather than native CBR/quarter multipass. Calibration
required aggregate encoder requests of 520.590, 454.881 and 407.612 Mbps to obtain
the actual coded rates above. Requested values are not achieved throughput.
Independent pictures change reference domains, entropy contexts, parameter
sets and queues/DPB storage despite equal pixels and normalized slice count.
Neither matched coded bytes nor successful decode proves equal image quality.

The wrapper compiled natively as ARM64 with warnings treated as errors. The
reviewed launcher pins the manifest, executable, kernel and installed Iris
identities and checks every session's ready/driver/result identity. Thirty-four
offline tests passed, including portable manifest paths and result binding.
Preparation preserved its original Windows-path manifest before normalizing
only relative path separators for the Linux device; encoded bytes and recorded
commands did not change. This was followed by the one-session canary and the
two/four-session jobs, not inferred from compilation alone.

The native wrapper waits for all sessions to finish setup before releasing a
readiness barrier. For each picture ID, the latest region callback defines
complete-group time; decoder FPS is never summed. Two independent analyses
reproduced group timing and coded-byte totals; one also reparsed the bound clip
payloads. The steady interval
is `(group 1, group 899]`: 898 group intervals and the coded bytes for IDs 2--899.
Every region returned all identities without reported error/empty/missing frames
and completed drain. Yet input deadlines were missed 855 times for one session,
854/856 for two, and 861/858/854/862 for four. These are late input submissions,
not timeout counts. Process success establishes functional completion only.

Group-interval p99 was 20.065/20.127/14.515 ms for one/two/four sessions; median
inter-region skew was 0/5.645/20.735 ms. The four-session interval distribution
is tighter but slower on average. The lab's 89--91 group-FPS and 13.889-ms p99
criteria do not measure unique headset presentation or motion-to-photon latency.
Callback timestamps include queueing and userspace observation, not pure engine
execution, and QC10C pixels were not independently linearized or golden-checked.

A same-clip one-session control removed all four active resource snapshots while
retaining before/after reads. It still produced **74.495895 Hz and 290.731973 Mbps**,
with 855 input-deadline misses. The mean change was only +0.001439 Hz. Those
snapshot reads do not explain the approximately 15.5-Hz shortfall in this one
sequential pair; other harness overhead and workload-dependent costs remain.

The [resource comparison](radio-stream-map.md#isolated-decoder-resource-votes-and-telemetry-control)
records unequal measured memory votes and clock-framework state. Cleanup receipts
show completed jobs, the filtered processes absent, runtime suspend and zero
video-consumer votes, followed by normal Main10 streaming restoration. They are
not an exhaustive device-holder census or a firmware session-destruction reply.
The [experiment register](experiment-register.md#completed-current-geometry-decoder-comparison)
binds preparation, native build, jobs, control, analyses and restoration separately.

## Tested, proposed and still unknown

Only a measured current-geometry gain would justify building a native-quality
multistream game renderer. This completed comparison supplies no such gain;
one sequential run per configuration does not prove that multiple sessions
can never help or establish a universal decoder ceiling.

The existing four-session H.264/TCP game path is a possible separate-format
prototype starting point. It first needs current source/binary provenance,
projection and texture-lifetime validation, and sustained unique-frame pacing.
It must not be offered as equivalent to the present packed Main10 stream.
Main8, disabled cross-slice filtering and minimum32-CU have their own failed
fixed-400 screens; none demonstrated a smooth replacement. No setting in this
audit guarantees steady useful 350 Mbps.

## Evidence and reproducibility limits

The retained private audit is `RepairTrial/codec-route-audit.md`. Its companion
`audit_codec_routes.py` extracts selected measurements and hashes 37 source
files into `codec-route-audit-evidence.json`; digests are listed in the
[experiment register](experiment-register.md#private-evidence-provenance).
These establish the retained evidence identity, not retrospective provenance
of unbound historical binaries. Raw logs, executables, device identifiers and
installed-code extracts remain private. This document publishes original
analysis and explicitly preserves the limits of each test.
