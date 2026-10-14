# H.264 and multiple decoder streams: implementation and evidence

This is an offline audit of retained source and completed experiments through
2026-10-14. **There is no validated multistream replacement that preserves the
current native Main10/foveation representation and delivers steady useful
350 Mbps with smooth pacing.** The existing game prototype is H.264; independent
HEVC-session tests are local capacity experiments without a game renderer.
The later square-layout work now includes an isolated hardware comparison and
bounded native startup trials. These remain private prototypes, not a release.

## Four paths that must not be conflated

| Path | Implemented representation | Established boundary |
| --- | --- | --- |
| Current native stream | One packed Main10 HEVC picture/session, currently 1664 x 6656; host requests four constrained slices | Slices are not independent decoder sessions. Emitted headers and request state are separate evidence. |
| Existing game multistream prototype | Two or four H.264 sessions, public left/right SteamVR mirror textures, NV12 DMA-buffer composition and per-eye projection overlays | A different image/composition path, not a drop-in split of the native packed foveated frame. |
| Local HEVC comparison | One/two/four stock V4L2 sessions decoding saved Main10 clips to QC10C | Measures local completion/pacing without network, game composition or panel presentation. |
| New square H.264 path | One 3328 x 3328 8-bit picture, preserving the tall layout's pixel area | Isolated CAVLC capacity/callback cadence passed; native integration has only a short 150-Mbps functional canary, with later startup failures. |

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

The earlier native adapter explicitly limits H.264 to 1024 x 4096 and 8-bit,
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

Two later one-session observations retain the selected 350-Mbps clip. A verified
additional memory request yielded 74.497800 Hz / 290.739409 coded Mbps, while a
separate copy/QBUF-timed executable yielded 74.489401 Hz / 290.706632 Mbps.
Both completed all 900 pictures and failed capacity/cadence. The
[resource control](radio-stream-map.md#completed-single-session-memory-vote-control)
has added observation and environmental differences; the
[timing experiment](receiver-stream-map.md#measured-compressed-input-copy-and-qbuf-cost)
rules out dominant synchronous input-copy cost in its run, not all software cost.

## Retained lower-rate workload: completed hardware test

The first calibration attempt already contained a 208.075080-Mbps one-session
stream from the same deterministic source. A separate package now preserves
that elementary stream and validates all 90 access units, Main10 1664 x 6656,
four independent 2,704-CTU slices, CU16/CTU32, no tiles/WPP, cross-slice filtering
and no filler. Explicit CPU HEVC decoding passed, and its new lab framing
concatenates exactly to the original bytes. No GPU regeneration was needed.

The original encoder request was 350 Mbps; the 26,009,385 coded bytes establish
the lower actual rate. The completed ten-repeat hardware run credits
259,385,846 bytes for IDs 2--899 over 9.974301297 seconds: **90.031369 output
Hz / 208.043321 coded Mbps**. All 900 output identities decoded and drained,
with zero input deadline misses. The original three-configuration gate and
decoder binary were preserved; a separate pinned single-clip launcher was used.
The harness capacity criterion passed, but its cadence criterion failed:
p99 callback gap 19.491367 ms and maximum 20.525452 ms. No panel timing or
hardware-decoded pixel equivalence was measured.

| Emitted parameter-set field | Retained 208-Mbps clip | Selected 350-Mbps clip |
| --- | ---: | ---: |
| Profile ID / tier flag | 2 / 1 | 2 / 1 |
| VPS/SPS level ID | 183 | 186 |
| NAL HRD bitrate, bits/s | 350,000,000 | 520,590,080 |
| NAL HRD CPB size, bits | 3,888,880 | 5,784,320 |
| Maximum decoded-picture buffering minus one | 3 | 3 |
| Reordered pictures | 0 | 0 |
| PPS default active L0/L1 references minus one | 2 / 2 | 2 / 2 |
| VUI time scale / units per tick | 90 / 1 | 90 / 1 |

The full FFmpeg parameter-set trace differs only in level for VPS, and level,
HRD bitrate and CPB size for SPS; PPS bytes are identical. The structural
prefix parser alone does not establish the full comparison. HRD signals the
encoder's allocation, not measured delivered bytes. Quantization, residuals,
prediction choices and image quality can also differ. The first comparison
therefore changed lower coded workload **and** capability/rate metadata. It
does not isolate an entropy engine, memory bandwidth or byte count alone.

For nine balanced repeats, IDs 90--899, all nine intervals above 13.8889 ms
occur at source phase zero, the IDR picture. Their median is 19.587823 ms,
followed by phase-one intervals of 4.715--4.790 ms. The cycles span
999.163--1001.516 ms. The heavier 350-Mbps source instead has 253/810 late
intervals across many phases and roughly 1.208-second cycles. This excludes
a universal 74.5-Hz limit for every tested stream at this geometry, while
leaving workload-dependent decoder or software service unresolved.

## Same coded pictures with the heavier clip's metadata

A separate control replaces only the retained clip's first VPS and SPS with
the selected 350-Mbps clip's parameter sets. Full parameter traces establish
that these changes are level and HRD bitrate/CPB declarations. Every VCL,
AUD, SEI and PPS NAL remains byte-identical and in the same order: 360 slices,
90 AUDs and 90 SEIs across 90 access units. The full elementary stream grows
by one byte. Explicit software decoding produces identical hashes, sizes and
timestamps for all 90 output pictures.

The retained SEIs are picture-timing messages. Their field widths, timing
syntax, values and trailing bits were checked under both parameter sets;
unknown message types or changed timing shapes are rejected. This is stronger
than blindly replacing headers, but is not general Annex C HRD conformance
proof or hardware pixel validation.

The hardware control completed all 900 identities at **90.049625 Hz /
208.085514 coded Mbps**, counting 259,385,855 bytes over 9.972279170 seconds.
The nine additional credited bytes come from repeated first access units.
The unchanged decoder executable, queue/pacing loop and sparse telemetry were
used. Level 186 and HRD 520,590,080 bps / 5,784,320-bit CPB did **not** reproduce
the 350-Mbps workload's 74.5-Hz outcome. This weakens a level/HRD-only explanation
for these clips; it does not prove those fields irrelevant to every workload.

Again, all nine balanced late intervals are at the IDR phase: 19.293--19.566 ms,
followed by 4.741--5.082 ms. The capacity criterion passes, but cadence still
fails. The +0.018256-Hz difference from original208 is not a significance or
quality claim. Input/output callbacks do not separate hardware completion,
firmware dispatch, poll wakeup and dequeue work. Both jobs ended, their owned
processes were absent and normal Main10 streaming resumed. The stricter later
process inventory and different sparse sample counts also prevent treating the
older 350-Mbps baseline as perfectly observer-matched.

## PC encoder capability query: provisional corroboration

A retained PC NVENC capability query returned status zero for maximum dimensions,
level, 10-bit and constrained-encoding flags, with no encoded frames:

| Codec | Reported maximum width / height | Maximum level value | 10-bit flag | Constrained-encoding flag |
| --- | --- | ---: | ---: | ---: |
| H.264 | 4096 / 4096 | 62 | 1 | 1 |
| HEVC | 8192 / 8192 | 186 | 1 | 1 |
| AV1 | 8192 / 8192 | 23 | 1 | 0 |

The executable and source hashes were captured, but their build binding was
**not verified**. Treat these as provisional reported capabilities, not tested
format combinations or new codec support. In particular, the H.264 height
report is inconsistent with reusing the packed 6656-pixel HEVC height unchanged;
the 10-bit flag does not establish a working H.264 10-bit stream or receiver.
No acceptance, decoder throughput or image-quality conclusion follows.

## Existing square packing: a static implementation path

The inspected host and receiver contain a specific two-by-two foveated layout.
Four 1664 × 1664 regions could use **3328 × 3328** packed dimensions instead of
1664 × 6656, preserving 11,075,584 coded luma positions. This is evidence of an
existing implementation path. The subsequent isolated decoder and native
prototype results below add evidence without establishing a finished replacement.

Host geometry function `0x180A1C7F0` computes W × 4H for mode 0 and 2W × 2H
for mode 1. The installed NVENC selector `0x1809FE100` returns mode 0. No exposed
square-layout setting was found in the inspected settings path. Host packing
selects shipped `full_stack` or `two_by_two` luma/chroma shaders by active mode.
Mode-specific capacity matters too: changing dimensions without changing the
mode-0 width limit would shrink a requested 3328-pixel image to 3200 pixels.

The host sends packed and regional dimensions. Retained receiver function
`0x34A08` uses their ratios to select a predefined layout: tall four-strip,
two-by-two, or nonfoveated top/bottom. In the square arrangement, the left column
holds the two peripheral regions and the right column holds the corresponding
gaze regions. This metadata does not describe arbitrary atlas rectangles. The
receiver then supplies eye/gaze descriptors to the native compositor. Correct
orientation, blend edges, gaze motion and timing markings remain untested for
a new square-stream candidate.

Quality control is a concrete blocker. The traced native QP builder handles
mode 0, and the current helper classifies four raster quarters as alternating
gaze and periphery. Both layouts have 10,816 cells on a 32-pixel grid, but that
quarter classifier would assign the wrong region to 5,408 square-layout cells
before protected sentinel/timing exceptions. Ten offline arithmetic tests cover
this mismatch, dimensions, caps and region mapping. They do not measure image
quality. A square implementation needs layout-aware QP and protected-position
mapping; H.264 also has separate macroblock/QP-map requirements.

The audit verifies complete bytes for 22 selected host functions against the
installed driver and records six installed shader identities. The receiver
portion uses previously retained, separately hashed extracts; this pass did not
revalidate its complete binary or execute the shaders. The provisional H.264
4096 × 4096 capability report suggests that a square picture could fit, but
accepted profile, bit depth, CAVLC selection, receiver support and throughput
were unproved at the static audit's cutoff. The later tests below address
specific parts of that boundary. Equal pixel area does not establish equal
image quality, and the layout-aware quality-map work remains unresolved.

## Full-area square H.264: isolated CAVLC capacity and cadence pass

A deterministic 8-bit source was generated at 1664 x 6656 and rearranged into
3328 x 3328: the two peripheral bands on the left, their corresponding gaze
bands on the right. The inverse rearrangement restored every source-frame hash.
Both entropy calibrations used the same original and square source digests.
No coded luma area was removed: both layouts contain 11,075,584 positions.
These synthetic bands do not reproduce native foveation content or Main10 detail.

The prepared High-profile clips contain 90 pictures at nominal 90 Hz, four
slices per picture, three-reference DPB syntax, no B pictures and no filler.
Independent parsing, FFmpeg header tracing and full software decoding agree.
An initial preparation was refused because the emitted DPB bound was six pictures despite
a three-reference request; a separately preserved revision explicitly requested
DPB three and verified the emitted result. Requested settings alone were not
treated as output proof. Actual selected source rates were 351.286312 Mbps for
CABAC and 350.060160 Mbps for CAVLC, both within 0.5% of 350 Mbps.

| Isolated capacity case | Credited coded bytes / interval | Coded Mbps / callback FPS | Callback interval p99 / maximum | Input deadline misses |
| --- | --- | --- | --- | ---: |
| CABAC | 438,195,978 / 10.107757385 s | 346.819546 / 88.842655 | 11.911280 / 12.268042 ms | 298 |
| CAVLC | 436,759,516 / 9.978136518 s | **350.173213 / 89.996764** | 13.497225 / 13.556183 ms | 0 |

Both jobs completed all 900 picture identities and drain, without reported
decode, empty-buffer or missing-frame errors. Each rate uses callback 1 to
callback 899 and the exact coded bytes for IDs 2--899, including nine later
IDRs. CAVLC passed the declared 89--91-FPS capacity and 13.889-ms callback-p99
criteria; CABAC failed average capacity/input pacing despite a smaller callback
p99. Backend QBUF-to-userspace-completion residence p99 was 95.271 ms for CABAC
and 9.36151 ms for CAVLC. That residence includes queueing and observation work,
not pure hardware execution.

Separate linear NV12 canaries decoded all 90 pictures and matched each clip's
own software-decoded first-picture golden. Capacity used compressed Q08C output;
it did not inspect every hardware-decoded pixel. Sparse active readings showed
600-MHz decoder clock state and the same video memory request in both jobs,
with no added ICC experiment. These are observations, not continuous hardware
occupancy or thermal controls. The exact decoder backend remained unchanged.

This is a useful full-area **local CAVLC result**, not wireless/gameplay or panel
validation. The jobs ran once, sequentially, with different compressed pictures
and unproven equal visual quality. CABAC's source rate was also slightly higher.
No universal entropy-mode or decoder ceiling follows. Owned jobs ended and normal
HEVC Main10 streaming resumed with saved settings restored.

## Native square prototype: functional startup, not steady 350 Mbps

A private bridge now uses the existing square packer and native receiver path
with H.264 High 8-bit, CAVLC, four slices, DPB/reference bound three, fixed nominal
90 Hz, P2/ultra-low latency, unpadded VBR and multipass disabled. It first uses a
150-Mbps average / 172.5-Mbps peak request. The native mode-specific capacity
wrapper uses actual successful H.264 capability queries; it does not globally
replace the shared selector stub. The receiver codec selection is temporary.
The native quality map is explicitly disabled: this changes quality policy and
does not preserve the normal Main10 gaze/periphery offsets.

A standalone GPU canary accepted all 90 pictures and its actual output proved
3328 x 3328, CAVLC, four slices, DPB three, VUI bitstream restriction, maximum
decoded-picture buffering three and no reordering. Its simple input produced
only 4.476328 coded Mbps, so it validates syntax/acceptance, not a 150- or
350-Mbps workload. These are standalone headers, not a native gameplay capture.

The successful native 150-Mbps canary then recorded accepted initialization and
reconfiguration, actual receiver capture allocation of 16 buffers, and bounded
encoder progress: 399 encode calls, 401 completed bitstream locks and 61,514,759
bytes. Its reported timer starts after an observer gap while the counter baseline
precedes that gap; these counts cannot be assigned to that exact duration for
a bitrate or FPS calculation. Different call/completion counts can also drain
work already in flight. This functional check did not independently prove
native Vulkan import, metadata reconstruction, valid pose, image orientation,
physical presentation or visual quality. One I-frame request before the first
source event was classified separately; no hard reset was accepted as success.

Two fresh 350-Mbps starts subsequently failed during startup, with seven
completed locks each and no measurement window. A second bridge added bounded
first-IDR evidence capture and a one-shot 150-to-350 request after healthy
warmup. That run also failed during 150-Mbps warmup, at ten locks; the 350 ramp
and read-only AU dump were never invoked. All three failures had zero recorded
NVENC API/guard errors. They do not establish a steady-state H.264 payload limit.

The installed receiver watchdog starts OUTPUT/CAPTURE progress clocks during
initialization, before the first compressed picture, and checks their older
age after QBUF. It requests recovery after 330 ms and resets after 800 ms;
successful input submission does not itself refresh those return-progress
clocks. In the v2 receipt, the reset was only 28.775 ms after the first source
event, with an I-frame request 292 microseconds before that event. This supports
startup-age investigation, not an observed 800-ms post-decode stall. Exact first
QBUF/return clocks were not captured, and a source event alone does not prove
capture setup or decode completion. AU scan/copy/hash cost was not timed, so
its possible marginal startup contribution remains unknown. Earlier failures
without that capture show it is not necessary for this failure class.

Every completed trial restored normal Main10 streaming, terminated its candidate
host and closed the temporary receiver lease. No native useful-350 interval,
sustained game/panel test or released square-stream option exists at this cutoff.

## Restored normal HEVC: why 400 requested became about 240 useful

A subsequent passive normal reference used the saved **400-Mbps** target,
1664 x 6656 Main10, unpadded CBR and quarter multipass. Over 20.463580300 seconds,
613,926,132 encoded bytes and 1,838 pictures give **240.007320 Mbps / 89.818105
FPS**. This is not a fixed 350-Mbps trial. Endpoint successful API requests had average
budgets 236.798 and 248.249 Mbps; no helper status calls occurred inside the
counter window and there is no continuous accepted-configuration readback.

All 21 sampled controller targets match `400 x queue factor x ping factor`
within 0.000014 Mbps. Ping factor was one; queue factor averaged 0.595393,
yielding 238.157292 Mbps. Retained AFTM budget averaged 238.742783 decimal Mbps
over a slightly different history window. The
[mapped controller](host-stream-map.md#requested-bitrate-accepted-budget-and-coded-bytes)
retains decoded-ACK/pending-work feedback in manual mode, including a reduction
at queue distance four. This explains the major requested-to-useful reduction
here; it is not evidence of a 240-Mbps cap or unexplained 160-Mbps CBR underfill.
The capture does not isolate the origin of every pending frame or delayed ACK.

Waterfall averaged **291.487297 Mib/s**, equivalent to **305.646584 decimal Mbps**.
Its same rolling-one-second samples contain 239.105753 Mbps useful video,
54.639470 Mbps parity and 11.901361 Mbps remaining aggregate overhead/accounting.
Adaptive second parity was active in 17 of 21 samples. Receiver interface RX
was **319.722220 Mbps** over its own 19.997518-second interval. Different windows
and accounting layers prevent assigning the remaining difference entirely to
headers, retransmissions or other traffic. The higher traffic rates are not
additional picture detail, and mixing binary and decimal units magnifies the gap.

There were zero encoder/API/UDP error deltas, zero socket-drop delta and no
captured reset or texture failure, but one receiver presentation timeout.
Retained transport and decoder-turnaround tails reached 54.272 and 43.171 ms.
This short normal reference does not establish zero jitter or sustained
350 Mbps of useful video.

## Tested, proposed and still unknown

Only a measured current-geometry gain would justify building a native-quality
multistream game renderer. The earlier HEVC one/two/four-session comparison
supplies no such gain;
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
