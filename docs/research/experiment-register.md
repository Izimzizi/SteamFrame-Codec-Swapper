# Streaming experiment register

Publication cutoff: completed evidence reviewed on **2026-10-14**. This index
distinguishes a measured negative result from an untested option. A completed
capture can still fail streaming performance; a clean observer is not a pass.
It links the detailed maps rather than replacing their build and timing limits.

## Where to start

| Question | Research entry |
| --- | --- |
| Why can requested CBR differ from measured useful video? | [Host budgeting and coded bytes](host-stream-map.md#requested-bitrate-accepted-budget-and-coded-bytes) |
| What did the modern350 test establish? | [Near 90-Hz prefix, underfilled budget and terminal repair failure](host-stream-map.md#modern-fixed350-reference-near-90-hz-before-a-receiver-repair-failure) |
| What happened when the timed-prefix change was tried? | [Guarded transaction and live attempt limits](receiver-stream-map.md#timed-prefix-transaction-and-bounded-live-attempts), plus [startup/counter separation](host-stream-map.md#retry-prefix-attempt-startup-failure-precedes-the-counter-window) |
| What is the next allocation experiment? | [RC/QP readback and offline-built full-pass preparation](host-stream-map.md#underfill-readback-and-an-offline-built-full-pass-discriminator) |
| Why did the signed timer repair still permit an overflow? | [Reused-slot publication and timed request capacity](receiver-stream-map.md#fixed350-timed-overflow-during-rx-slot-reinitialization) |
| What did the latest minimum-CU change actually do? | [Accepted request, emitted SPS and failed cadence](host-stream-map.md#minimum32-coding-unit-trial-accepted-and-emitted-but-cadence-still-failed) |
| Do mutexes, PM or IRQ dispatch explain the receiver backlog? | [Matched thirteen-event observation](receiver-stream-map.md#matched-thirteen-event-observation-small-observed-lock-and-pm-spans) |
| Are the high-rate failures still a radio/USB problem? | [Recent send-queue and delivery evidence](radio-stream-map.md#loaded-follow-ups-do-not-show-a-sustained-host-send-backlog) |
| Is H.264 or multistream already a working alternative? | [Codec and decoder-session audit](codec-stream-options.md) |
| Did one/two/four Main10 sessions reach 90 Hz in isolation? | [Completed current-geometry comparison](#completed-current-geometry-decoder-comparison) |
| Did active resource reads explain the decoder shortfall? | [Same-clip telemetry-removal control](radio-stream-map.md#isolated-decoder-resource-votes-and-telemetry-control) |
| Did an additional memory request improve the single-session result? | [Completed ICC control](radio-stream-map.md#completed-single-session-memory-vote-control) |
| Is compressed-input copying consuming the missing frame time? | [Measured copy/QBUF boundaries](receiver-stream-map.md#measured-compressed-input-copy-and-qbuf-cost) |
| Does the lower-rate workload reach 90 Hz? | [Completed 208 and metadata-only control](codec-stream-options.md#retained-lower-rate-workload-completed-hardware-test) |
| Does the installed driver expose an unused throughput setting? | [Arrival-rate estimator, supported decoder request and control table](receiver-stream-map.md#installed-decoder-arrival-rate-and-control-boundary) |
| What caused the long captured image jumps? | [Shared-texture ownership](pc-texture-handoff.md) and [receiver repair ordering](receiver-stream-map.md#packet-receive-repair-and-ordering) |

## Recent completed outcomes

Useful video is encoded bytes before network/FEC overhead. Decimal Mbps and
FPS below use each run's own host counter window. They are not equal-workload
A/B tests, and short guard-aborted averages are not sustained capacities.

| Experiment | Window | Useful Mbps / encoded FPS | Status and distinguishing evidence |
| --- | --- | --- | --- |
| Historical coherent350 (`coherent350-no-filler`) | 69.734 s | 323.91 / 89.91 | Six presentation timeouts; no AFTM/Iris trace. This older helper differs from the modern350v2 trial below. |
| Fixed380, timer plus six-frame window (`coherent380-window6-1`) | 149.694 s counters, 150 s requested | 327.35 / 89.45 | Failed smoothness: two source-side texture stalls, progress recovery, four presentation timeouts. |
| Fixed400 passive counter observation | About 2 s | 340.84 / 84.03 | Guard-aborted; accepted 400 did not preserve 90-Hz cadence. |
| Fixed400 nine-event IRQ/Iris trace | About 2 s counters, longer trace | 333.91 / 81.54 | Guard-aborted; added queue-to-input residence was not a comparable final IRQ dispatch delay. |
| Fixed400 cross-slice filter disabled | 1.999436 s | 336.35 / 82.02 | Actual emitted PPS verified; decoder-delay/backlog guard failed. |
| Fixed400 thirteen-event lock/PM trace | 1.999240 s counters, 10.000685 s trace | 304.19 / 68.53 | Cadence/output guard failed; bounded IRQ-thread contention/PM spans too small to explain the broad residence rise. |
| Fixed400 minimum32-CU, CTU32 unchanged | 1.999455 s | 307.79 / 69.02 | Actual emitted SPS verified; cadence guard failed, no simultaneous kernel-stage trace. |
| Modern fixed350 (`coherent350-v2-matched-2`), before terminal failure | 77.997863 s | 325.066 / 89.964 | Near 90-Hz encoding, but 65 presentation timeouts by the 70-second sample and useful payload below 350. |
| Same modern350 run, whole partial capture | 79.998168 s | 319.743 / 88.502 | Receiver timed retry-list capacity fault; final intervals 46.015/16.989 FPS. 203 timeouts and one counted drop overall. |
| Prefix plus signed timer, adaptive-budget canary (`timer-prefix20-canary-1`) | About 10 s counters, 30 s lease | 305.041 / 89.906 | Application/restoration validated; nine presentation timeouts and one counted drop. No branch-execution counter or reliability proof. |
| Combined fixed350/prefix/window first attempt (`coherent350-prefix20-1`) | 9.999441 s counters | 324.920 / 90.005 | Setup/handoff-aborted: texture fault after activation but before counter baseline. Twelve presentation timeouts; later counters do not erase the startup failure. |
| Combined fixed350/prefix/window scoped repeat (`coherent350-prefix20-scope-2`) | 1.999516 s counters | 297.881 / 76.519 | Cadence-aborted with growing decoder turnaround. Larger pictures, different source/map; zero fatal-overflow events is not successful-prefix coverage. |

The five fixed400 rows refer respectively to `coherent400-passive-scope-1`,
`coherent400-irq-1`, `coherent400-no-cross-filter-v2-2`,
`coherent400-lock-pm-1` and `coherent400-min-cu32-1`. The fixed380 row specifically
uses `coherent380-window6-1-stream.json`: 327.352703 Mbps and 89.449204 FPS
over 149.693898 seconds, rather than another 380-budget attempt.

The historical coherent350 run accepted 350/402.5 Mbit/s and 4,472,222 VBV bits,
with one-second useful rates 313.400--328.268 Mbps and sampled Waterfall above 300.
Its six presentation timeouts prevent calling it a clean smoothness pass.
Its older helper had first-callback activation and 120-second reconfigure-only
expiry; it lacked the current dormant/manual-begin contract and matched timing
coverage. It is real measured evidence at a 350 budget, not a fixed-nominal-90
experiment or proof of useful 350 Mbps.

The preceding lock/PM and minimum-CU counter windows account for all **137/138 pictures** and
**76,018,645/76,926,754 bytes** respectively. Strict completed AFTM covers
120 pictures in each, excluding the newest rows by design. Nominal allocation
is calculated as `400,000,000 / (8 * requested_fps)` per picture; observed
picture totals are 99.432%/99.767% of that arithmetic. FPS-denominator variation
changes mean nominal allocation by only 0.449%/0.574% from fixed 90. Those
comparisons explain the aggregate byte deficit without asserting exact CBR
filling, equal quality or sustainable 90-Hz service.

Waterfall is a different counter: it includes assembled transport overhead and
uses a binary-megabit divisor. The lock/PM and minimum-CU ranges, about 367--385 and 380--382,
remain above 300 while cadence is near 69. It therefore does not validate the
user's smooth-streaming goal. Requested bitrate, accepted NVENC fields, useful
bytes, decoder completion and physical presentation are distinct quantities.

### What is new in the modern350 run

The original-codec helper kept its accepted 350/402.5-Mbit/s budget and
4,472,222-bit VBV. The prefix contains 7,017 encoded pictures; pairing covers
6,990 and 99.612% of useful bytes. Paired pictures fill 92.408% of the nominal
per-picture allocation, while FPS-denominator variation adds only 0.544% to
average allocation. The missing useful bits therefore remain a separate
question from the terminal receiver fault. Median completed decoder turnaround
is 18.682 ms over the paired prefix, unlike the immediate broad backlog in the
fixed400 screens.

The rare-branch trace contains a timed overflow followed by its common edge,
not the earlier progress-only overflow. Its list replay is 560 entries against
capacity 512, before a later 20-ID emission clamp. A new slot's birth/retry clocks
are 25 microseconds apart, while its last-chunk clock nearly matches the previous
modulo16 owner. Installed store ordering permits precisely that mixed state.
This strongly supports a partial-publication race; sequential uprobe fetches
and absent initializer-boundary events prevent claiming the live interleaving
was directly observed. No 173-ms radio outage or new hardware ceiling is proven.

The first 350 attempt failed during helper startup and supplies no performance
measurement. Its disconnected target-restoration path was repaired with a
bounded reconnect/restore/restart sequence and offline failure tests. The
measured second attempt restored normal Main10/target 400 Mbps, original receiver
instructions, all-thread detachment and activity state. These local coordinator
changes and experimental helpers remain private and unreleased.

## Completed current-geometry decoder comparison

These are local decoder jobs with native streaming paused, not host counter
windows or wireless tests. Each configuration represents the same total
1664 x 6656 Main10 area and four independent slice segments per group.
Actual prepared source rates were 351.258344/350.036304/349.099240 Mbps, with no
filler. All seven clips passed software decode, actual-header checks and source
hash comparison. The wrapper compiled natively as ARM64 and its exact executable
and staged manifest were verified. Thirty-four offline preparation/runtime
validation tests passed before the completed device jobs.

| Job | Completion interval | Coded Mbps / complete-group Hz | Result |
| --- | ---: | ---: | --- |
| One session, four active resource snapshots | 12.054588 s | 290.726 / 74.494 | All 900 identities and drain complete; 855 missed input deadlines. Capacity/cadence failed. |
| Two distinct half-height sessions | 12.091089 s | 288.780 / 74.270 | All 900 groups complete; 854/856 missed input deadlines; median region skew 5.645 ms. Failed. |
| Four distinct quarter-height sessions | 12.511880 s | 278.431 / 71.772 | All 900 groups complete; 854--862 missed input deadlines per region; median region skew 20.735 ms. Failed. |
| Same one-session clip without active snapshots | 12.054356 s | 290.732 / 74.496 | All 900 identities complete; 855 missed deadlines. Mean cadence essentially unchanged. Failed. |

Every row uses `(group 1, group 899]`, 898 intervals, and the source bytes for
IDs 2--899. The first two measured byte totals are 438,073,321/436,457,384;
four sessions use 435,461,255, and the control repeats the single-session total.
The slowest regional callback defines each group. Independent reproductions
agree with saved timing/byte summaries; one also reparses the bound lab framing.
Functional process success must not be read as a 90-Hz pass.

Active video-consumer ICC averages were 1.608M/3.216M/3.304M kB/s, with sampled
MVS0/MVS0C framework state 600/900 MHz and no force override. Those are requests
and framework readbacks, not delivered memory bandwidth or engine occupancy.
The no-active-telemetry control changed cadence by only +0.001439 Hz in one
sequential pair. It does not exclude other harness costs, session-order effects,
workload differences or brief state changes. See the [codec methodology](codec-stream-options.md#current-geometry-comparison-completed-tests-missed-90-hz)
and [resource analysis](radio-stream-map.md#isolated-decoder-resource-votes-and-telemetry-control).

All jobs terminated, followed by filtered process disappearance, runtime suspend
and zero video-consumer votes. A separate final receipt verifies normal Main10
streaming restoration. This is not an exhaustive FD-holder census or firmware
destruction acknowledgement. No smooth useful-350 game-stream result was achieved.

## Completed memory-vote and input-copy follow-ups

These two later jobs retain the selected single-session 350-Mbps source. The
ICC job retains the original executable; the copy-timing job binds a separate
native build that adds observation around the existing copy/QBUF operations.

| Case | Exact 898-interval window | Coded Mbps / group Hz | Outcome |
| --- | ---: | ---: | --- |
| Additional 1,134,000 kB/s ICC request | 12.054047251 s | 290.739409 / 74.497800 | All 900 identities and drain complete; 855 late inputs. No material mean gain; capacity/cadence failed. |
| Copy/QBUF timing executable | 12.055406322 s | 290.706632 / 74.489401 | All 900 input/output identities validated; capacity/cadence failed. Synchronous copy and QBUF are small. |

Both windows credit 438,073,321 coded bytes. The ICC run verifies the added
owner/request, child lifetime bounds, global FD/format checks and software
cleanup. Its extra observation profile, temperatures and foreign requests differ
from historical controls. Reported DDR duration remains at 4224 MHz within the
sampled intervals; that is neither delivered bandwidth nor a matched active
no-vote baseline. See the [resource evidence](radio-stream-map.md#completed-single-session-memory-vote-control).

The copy experiment measures means of 0.044453 ms for `memcpy` and 0.020230 ms
for QBUF, versus 13.424729 ms mean callback spacing. The 900-input sums and
898-interval output window differ at bootstrap boundaries; they are a scale
comparison, not CPU utilization. A near-0.999 association between repeated-phase
coded bytes and callback intervals prompted this test, but did not prove copying
was responsible. The [input ownership audit](receiver-stream-map.md#measured-compressed-input-copy-and-qbuf-cost)
distinguishes native chunk copies before submission from the lab's whole-picture
copy, and preserves unknown actual mapping/cache attributes.

Final receipts verify normal Main10 streaming restoration, original saved
target/geometry/policy and no remaining lab decoder or ICC module. After copy
timing, direct stream start resumed the existing normal host session; this was
not a fresh helper restart. No native game smoothness pass or permanent decoder/
cache-policy change follows.

## Completed lower-rate and metadata-only workloads

Both later isolated jobs use the same original native decoder binary and
1664 x 6656 Main10 geometry. All 900 identities decode and drain. Independent
replays count the same 898 identity intervals, IDs 2--899:

| Completed case | Credited bytes / window seconds | Output Hz / coded Mbps | Result |
| --- | --- | --- | --- |
| Retained208 | 259,385,846 /9.974301297 | 90.031369 /208.043321 | Capacity criterion passes; cadence fails, including nine IDR-phase gaps |
| Same 208 payload with 350 VPS/SPS | 259,385,855 /9.972279170 | 90.049625 /208.085514 | Capacity passes; cadence still fails; combined metadata does not reproduce 74.5 Hz |

The metadata control preserves every VCL/AUD/SEI/PPS byte, validates compatible
picture-timing SEI and matches all 90 software-decoded picture/time hashes. It
changes level/HRD declarations together, not their individual effects. There
is no hardware pixel or physical-display validation, quality equivalence to the
350 workload, or native useful 350 result. Both jobs resumed normal streaming;
their software cleanup is verified. See the [comparison and limits](codec-stream-options.md#same-coded-pictures-with-the-heavier-clips-metadata).

The installed-driver audit separately finds actual QBUF-arrival-rate estimation,
a byte-sensitive frequency model overridden by a 600 MHz decoder request, and
five public HEVC controls without a verified unused throughput setter. A PC
encoder-capability query is included only with its unverified source/build
binding caveat and zero-frame status; it is not codec acceptance evidence.

## Controls that remain untested or incomplete

- **Coherent350v2 reliability and useful-rate filling:** the modern result above
  is measured, but an uninterrupted smooth 350-Mbps useful stream remains
  unproved. The temporary arithmetic/window repair does not cover initialization
  ownership or the entire retry-list construction.
- **Timed-prefix mitigation:** 18 offline tests and independent exact-function
  emulation support bounded collection through the existing 20-ID emitter.
  Native capacity aborts intentionally become retries; initialization/generation
  races and recovery liveness remain unresolved. A guarded backend, transaction
  tests and bounded live attempts now exist, but neither successful branch
  execution nor improved reliability is established. See the [proof](receiver-stream-map.md#timed-prefix-mitigation-offline-evidence-only)
  and [later transaction/live limits](receiver-stream-map.md#timed-prefix-transaction-and-bounded-live-attempts).
- **Full-resolution multipass at fixed350:** the separate derivative now passes
  offline native/managed tests and has an exact-hash read-only observer sibling.
  It retains the 350M/402.5M/4,472,222 tuple, Main10 geometry and QP map, requesting
  packed 14 / native full-pass enum 2. No live load, hardware acceptance, allocation,
  latency or quality result exists. The separate runtime chain is still under
  review and is outside this publication cutoff.
- **Further current-geometry multistream work:** the completed 350-Mbps
  comparison above found no 90-Hz gain. Its 400-Mbps plan remains unexecuted;
  native foveation/quality equivalence and a sustainable multistream renderer
  remain unproved.
- **Matched ICC observation control:** the completed added-vote job above supplies
  a measured null result. A no-vote active job with its newer FD/G_FMT/DDR
  observation profile has not run; the old sparse-telemetry control is different.
- **Remaining workload controls:** the completed 208 and metadata control above
  narrow the metadata explanation but do not isolate residual/prediction cost,
  firmware service, allocation or userspace pickup. Proposed display-order or
  allocation parity experiments have not run at this cutoff.
- **HFI command publication and firmware response readiness:** useful missing
  boundaries. Verified existing events do not expose those times. Additional
  graph/probe instrumentation is not declared safe or ready by static support.
- **Radio/firmware changes:** no new thermal, clock, aggregation or firmware
  control was applied in the latest screens. Their receiver delay does not
  establish a radio ceiling or justify bypassing safety mechanisms.

## Method and publication boundary

Counter-window arithmetic is `delta_encoded_bytes * 8 / elapsed_seconds` and
`delta_encoded_frames / elapsed_seconds`. Host frame pairs must cover exactly
that host-QPC interval and reconcile both identities and bytes. AFTM statistics
use the retained TX intersection, not all preceding history. Receiver phases
use monotonic-raw markers and explicit activation/cancellation brackets; no
host-QPC, wall-clock or receiver-raw epochs are equated. Sampled queues can miss
brief events; a stable double read is not a formal atomic snapshot.

The earlier receiver-patch trials restored a fresh normal Main10 helper, original
receiver instructions, detached threads and cleared activity state. The later
isolated decoder jobs have their own process/resource and normal-stream resume
receipts; they do not all restart the host. Observer cleanup and root tracing
preservation are separate from performance acceptance. Same-trace 90-Hz preroll/
tail does not rule out workload-dependent observation cost.

The repository publishes original explanatory research. Private reproducer
names, formulas and digests make local evidence auditable; without the retained
inputs these hashes alone do not make the measurements independently rerunnable.
Raw proprietary binaries, decompiler listings, authentication material, personal
logs and operational captures are excluded. No app release or experimental
runtime control is shipped by this documentation publication.

This update adds completed retained 208 and same-VCL metadata-control jobs,
independent replays, the installed decoder rate/control audit, a caveated PC
capability query and the offline full-pass build. Historical preparation freezes
retain their original unrun status; separate later receipts establish completion
where it actually occurred. The full-pass runtime remains unrun. Earlier scoped
retry attempts remain failed, and no steady smooth native useful 350 result is
established by this publication.

## Private evidence provenance

Paths in the following table are relative to ignored `artifacts/`. They identify
the completed evidence; they are not links to files included in this commit.
The two fixed400 experiments use different helpers and sessions. The minimum-CU
helper SHA-256 is `26f1529bff74745d0f6366fd3aff97d2d164b2a271c619e6b8d02208c67ac8d2`;
the original fixed400 helper is
`97a3cf596b2fad79407398f8effbfebd8b979ea20d52ca794135d55cb874e3c6`.
The modern350 helper is
`b76de48ded9ec8f4e764cedd4ddbfd9ea3b901547ca0af3e8980b31126ba3b8f`.

<!-- Evidence digest table is generated from the completed retained files. -->

| Retained private evidence | SHA-256 |
| --- | --- |
| `RepairTrial/coherent380-window6-1-stream.json` | `cdf920e50ee5aa7b484a55a922bf35eeb4a8a96e654fd7a642bb1e2891f4f9fe` |
| `RepairTrial/coherent400-lock-pm-1-stream.json` | `3916a8b480f2c94b727496bead6eb37db5495f8ae600a2204725f455afd11abb` |
| `RepairTrial/coherent400-lock-pm-1-host-queues.json` | `26f0d3017d381497a426d97eb983f037a0fe3e645063b2fbbcc5a0036e4466c7` |
| `RepairTrial/coherent400-lock-pm-1-iris-events.json` | `cd4a8cc65859b32788b4677a7578f26c8df17dee9b4d7be4ee04086a24402c37` |
| `RepairTrial/coherent400-lock-pm-1-iris-integration.json` | `6742e3bb95dbff0ead868ed4c613674042c0104642cd16f27f9c3f194a5ab211` |
| `RepairTrial/coherent400-lock-pm-1-transition.json` | `a9a6690f3af152449b5f29f6d8615e06fd27ddb2b5303c07b6737daad6bb175a` |
| `RepairTrial/coherent400-lock-pm-1-producer-bound.json` | `97c4d126b2bfe75e6a1a2e85588638fa108e54503c5171c45dc22d3596f1815c` |
| `RepairTrial/coherent400-lock-pm-1-coordination.json` | `0b926bea13e508c75a0e5625723e527c28b15dff61d4bf8ca1b34d55b634b6e6` |
| `RepairTrial/coherent400-lock-pm-1-host-transport-review.json` | `211ac8ceaa3dd287867919e47cd8a089a3ed0305160c3b84e73268812c566879` |
| `RepairTrial/coherent400-lock-pm-1-host-transport-review.py` | `96b77a9aac188a836c26aeae9ee07611fc347d9c518adc10912f763ce05279e0` |
| `RepairTrial/coherent400-lock-pm-1-receiver-assessment.md` | `62e8d4f5d0374ae0bf4ccac9a329eab967ed02579da92cec363a34d54e0d1b6e` |
| `RepairTrial/coherent400-lock-pm-1-next-boundary.md` | `5e95ef170f51f09521eff18dc81af89fa2860e50a4f56ab38f020bd9e78a0b71` |
| `RepairTrial/analyze_iris_lock_pm_transition.py` | `8fc94418fa3bcc3130554096abaadbafc5eeeca1fc4e3939d658972ba5fb8927` |
| `RadioTrace/Coherent400MinCu32v1/provenance.json` | `298e786745f20175c8778eb95189a31b3053e73ccf75a9f3437394db7cd65626` |
| `RepairTrial/coherent400-min-cu32-1-stream.json` | `a4efdd491c6070b3d5a6c3cc1c9bf2b43746e13a9903636b2fcf8852b491d610` |
| `RepairTrial/coherent400-min-cu32-1-host-queues.json` | `dc1ac6353114eccc736784deb53d01de1dc5fe632a8c702c980b6ef377c95e9b` |
| `RepairTrial/production-hevc-min-cu32-1.json` | `0383907b0bfda0302974bb1edafddd1c005925d91a5130b5e7a660902f0b80e7` |
| `RepairTrial/coherent400-min-cu32-1-coordination.json` | `e3b0276c8317ac4b78074a66e0641703124a12feb7e5c096225fe5fa2416dbfb` |
| `RepairTrial/coherent400-min-cu32-1-root-assessment.json` | `fa642a09a8eb906f8971a3d2ba9a849a5e51b936f4e14a637c44587e7aeb3b43` |
| `RepairTrial/coherent400-min-cu32-1-host-review.json` | `b6f92e3efbb461b060aeebaf47137cbc95aee6f38303736858a1fd92a7ae7d81` |
| `RepairTrial/coherent400-min-cu32-1-host-review.py` | `86c24fa9d66483d14a5bd5d125844f6eba8a996b1d68064543821e887868f748` |
| `RepairTrial/codec-route-audit.md` | `632012f5d0d7afa336d94e3fd8423f60b70906e6c8f5e289feef1a011a0491b2` |
| `RepairTrial/codec-route-audit-evidence.json` | `ddca8120072153c4e609ce207b5f5c13b936d2eb5b7a628d5f7b6fcb80d089af` |
| `RepairTrial/audit_codec_routes.py` | `f558e05d454a720f2e94546618911396a2bb68f8aa175c6b9a6f59a8ded94ec4` |
| `RadioTrace/coherent350-no-filler.json` | `856f32e0211d26f82fefec276cb3c7bdbc25a1200a03b6871b462d1aaf99e8d1` |
| `RepairTrial/fixed350-evidence-audit.md` | `ade36c0bcd9e4b8652b9e2ab836224e4f28056012a2f8d99a729ff4e42191237` |
| `RadioTrace/Coherent350v2/README.md` | `1d2f56cb9e3b362825bd913364632f83d5fcbf06bc4d1cda2ec10f30a0a8309e` |
| `RadioTrace/Coherent350v2/source-manifest.json` | `db02880e1e0622fe9e67c8ee88221ec270b1711ebf7de6eb3a44d20fe8a1a0e1` |
| `RadioTrace/Coherent350v2/output-manifest.json` | `7af5d9a85c477fd2abdc588ba6202405f03b542df420230738d865d450d29fe3` |

### Modern350 and offline decoder-harness additions

The helper review binds the unchanged original codec path, exact passive ABI and
lease fixtures. The finalized trial files bind the prefix/fault analysis; the
cleanup receipt separately records normal settings, receiver and activity
restoration. The harness manifest binds thirteen source/help/plan files and
explicitly reports no ARM64 execution. Local reproducer entry points are
`coherent350-v2-matched-2-host-review.py` and
`analyze_coherent350_v2_receiver.py`; the harness tests use
`python -m unittest discover -s artifacts/HEVC/current-geometry-lab -p test_compare.py`.
None of these private files is shipped by this documentation commit.
The earlier harness README has since been updated; its current digest appears
in the completed decoder evidence below. The original source/plan checkpoint
remains identified by `provenance.json`.

| Retained private evidence | SHA-256 |
| --- | --- |
| `RepairTrial/coherent350-v2-independent-review.json` | `da07a09af0e7789267b0ca4e82fa7d41c9baff0e725b3e3fdc8de9d38ea5311a` |
| `RepairTrial/coherent350-v2-integration-freeze2.json` | `143ff4e75927f12dbe7e6f6790ca2c250e7efd75d7b3fec2beb2a644a12f890d` |
| `RepairTrial/coherent350-v2-matched-1-coordination.json` | `aefb6242657c3f02e305889f49dc23e8f357125e4c1c16733231e5c4f9a3bd99` |
| `RepairTrial/coherent350-v2-matched-1-recovery.json` | `d10d2adc83d020930cb861338b3fe3fde5fa85498743db2af0e7ee93c742d334` |
| `RepairTrial/coherent350-v2-matched-2-stream.json` | `e91916a7467415a5eb3c3e25954f793b43483a7fc3005eed4f1f664dcbc7280f` |
| `RepairTrial/coherent350-v2-matched-2-host-queues.json` | `c86ba8e06d0ebe17d4be12f769ab03a1619420caccec16909955ad2dfe02170a` |
| `RepairTrial/coherent350-v2-matched-2-stream.receiver.log` | `5b4351e47943e45ba3d2af571d1d5312d4a38ca767811994ea5326d591577021` |
| `RepairTrial/coherent350-v2-matched-2-overflow.json` | `9eb75d3b7f0c9adf0e133a865e9dd9807ff200052bac78049367681acd650d78` |
| `RepairTrial/coherent350-v2-matched-2-lease.json` | `e36a48a92091d7e0da150dc1435170deaf5cbb42794518afb12f607914712b7e` |
| `RepairTrial/coherent350-v2-matched-2-coordination.json` | `3dea172ad5ec6bee8b73cec289ec7aa3992ddbbb57d0f19f5f025a05357b7f63` |
| `RepairTrial/coherent350-v2-matched-2-root-cleanup.json` | `ca369e26396cfca28dca85fc5508979c0b7caec8e9632d3ba93176c7f5dfdb12` |
| `RepairTrial/coherent350-v2-matched-2-host-review.json` | `a667d80a9f5ca41d78d1b3a0ff82b4a93ea4cbda4c35f19d228b34ac47acb96d` |
| `RepairTrial/coherent350-v2-matched-2-host-review.py` | `494b7e0b812cf9c95dedc2b81c1c468eea4e05cd41a5b26aa8a76cc9dfb67433` |
| `RepairTrial/coherent350-v2-matched-2-receiver-independent-assessment.json` | `0f2fd3a647238643013f386734d2ca79081b67ec20e63bf0f1829dc5963fb746` |
| `RepairTrial/analyze_coherent350_v2_receiver.py` | `4925deb965cdd7f6d8790afe4171122601313d6ebc6462bd97d0f315be8c5e6a` |
| `HEVC/current-geometry-lab/provenance.json` | `3ed6847a2c8d7f31ea0a4f518a0a1b39dff9135fd41a0ba4ee0204048663e945` |
| `HEVC/current-geometry-lab/plan-350-four-slices.json` | `2ff99128de2a740612b44fdeb2bc8bf857a880fa04fdb9de01ee13f2e9458373` |
| `HEVC/current-geometry-lab/plan-400-four-slices.json` | `9a982d5f6de055dc098458fd0479b299fd5775f5fc716f10a2fdee60ceaecd66` |

### Offline timed-prefix proof

The reference model and exact-function emulator test a proposed timed-only
collection bound. They do not implement a live patcher, and stubbed external
calls do not prove receiver recovery. Signed-timer composition is tested
separately; the ordering-window routine is not invoked. The historical codec-route audit
above includes the later clarification of sender deadline misses and first-frame
outer-decoder verification; its current complete-file digest is recorded.

| Retained private evidence | SHA-256 |
| --- | --- |
| `RepairTrial/timed-prefix-mitigation.md` | `4e21b9aa4ce5db58d052f921df3a0019be9a1b27ea355e514f21d5493d59f3fa` |
| `RepairTrial/timed-prefix-audit.json` | `3d5567f063554d31e1eb04c6172b98d28da08286e7499772742b3e62c8042ff5` |
| `RepairTrial/timed_prefix_model.py` | `ccb124102c1c067c31fde120c8d22edabf43ca88351439c3ff7e6cd3d4db8ebb` |
| `RepairTrial/test_timed_prefix_model.py` | `48b623515878e04a8fd1b20a172828694f2e38628daf9297519ecdd99baad504` |
| `RepairTrial/timed-prefix-independent-emulation.json` | `a71020cbe6e08bf68e3a56ac08d77f3e6450b5811687198a95e6761102441619` |
| `RepairTrial/timed_prefix_independent_emulation.py` | `94d27ad05031d22492b728d9c28cb7019867bef22064ca688ad5ee439ba8b863` |

### Guarded prefix implementation, first live attempts and allocation plan

The transaction review records frozen source identities and 92 offline tests;
its inert test entry points are `test_timed_prefix_transaction_independent.py`,
`test_timed_prefix_runner.py` and the timer/window/backend test modules. The
canary and first combined attempt each have separate stream and restoration
receipts. `coherent350-prefix20-1-bootstrap-review.py` reproduces the startup/
counter phase split; the final coordination and lease records establish cleanup
that was still pending when that note was written.

The finish-point discriminator is a separate offline emulator check, and the
underfill evidence extracts already-saved RC/QP counters. The full-pass manifest
binds 28 source files; its adapted tests have not been compiled or run. No
private implementation, executable or capture is included in this publication.

| Retained private evidence | SHA-256 |
| --- | --- |
| `RepairTrial/timed-prefix-backend-notes.md` | `453c57df5d1ca8b0a517b1dea746907b0c6774cfcddc7d6156fcfe1db22bb071` |
| `RepairTrial/timed-prefix-backend-manifest.json` | `c1c7bfd67468ca97b32f576b923b88e1778556e8fdbf853c2700ec42b07f6be4` |
| `RepairTrial/timed-prefix-transaction-independent-review.md` | `e0ba090c36b52810e621c6ee1a1f76822cf4c0f55b5718f9d75f257c5e496b29` |
| `RepairTrial/timed-prefix-transaction-independent.json` | `76d435434de80c6fe17e70a0152462d95596a5aec7591514d418c1b29a4ed040` |
| `RepairTrial/timer-prefix20-canary-1-root-assessment.json` | `3f5b67de4a1cb80d2a6eeb24a20513e30665421ac2110547dbc9c1f216b99c22` |
| `RepairTrial/timer-prefix20-canary-1-lease.json` | `fa68945e87c457d4b81db31a2f2cc2276d4d59bcdd96f2fb0b49ed5d84cc25ae` |
| `RepairTrial/timer-prefix20-canary-1-stream.json` | `a799f73711e3bf573e69262edaec892527286749fd981bc44f91a3a0cb798376` |
| `RepairTrial/coherent350-prefix20-1-coordination.json` | `04946a6e92efeb546732ebd117c8e70f5c662ac9c6abf8053e440cc84c98a8bd` |
| `RepairTrial/coherent350-prefix20-1-stream.json` | `daac5b610ed6300414f002de1335fe3b66e0034c6f3280d437349e5dd78424b5` |
| `RepairTrial/coherent350-prefix20-1-lease.json` | `cb1884d210299c75751c91dc5ed6c077c1482132ba7c8ce5188b35af187b18a8` |
| `RepairTrial/coherent350-prefix20-1-overflow.json` | `cd62ffb0b57b3cff86d3321a2e1be005385bed8b658eef974242961d6fbe34c6` |
| `RepairTrial/coherent350-prefix20-1-bootstrap-review.md` | `0688ce62785c3c8287d529d26791c0fa92f50ff526a8276dcea8509a413f9f9f` |
| `RepairTrial/coherent350-prefix20-1-bootstrap-review.json` | `134b38cd001a2b0c604b9ae7353aee377924b75029082ed14054a99ac28a34b2` |
| `RepairTrial/coherent350-prefix20-1-bootstrap-review.py` | `929505e201dc903c4c860ed956fec105ce3cde595d7fd4e365639f09c5087f9d` |
| `RepairTrial/timed-prefix-observation-and-underfill-plan.md` | `5345dffd08ba2a959e9b02fd5cf50938fe258a4cf600b5853ebb99e675b2642d` |
| `RepairTrial/timed-prefix-observation-and-underfill-evidence.json` | `ad4b309dfef5d09da71a353fa670c02939dd2d7fe40b4bebe45a25652195b0da` |
| `RepairTrial/timed_prefix_finish_observation_check.py` | `975aa1f6702b23cd2b0f0eeff76c643ba43fdc9765ef187c0db6ba1f6930a46f` |
| `RepairTrial/timed_prefix_finish_observation_check.json` | `217439a66250d507f3e7118cddfbffde7ec64b7a42d673038ec2434155cb5bf8` |
| `RadioTrace/Coherent350Fullv1/source-preparation-manifest.json` | `403f7bccee5fb54350f95f83e245f95329ebaeb13af9cb109de30b24257e3355` |
| `RadioTrace/Coherent350Fullv1/source-preparation.diff` | `a6e2eb08750694be9b31e8d055802acc4af3c706ed94cf8c77722e9a7864977d` |
| `RadioTrace/Coherent350Fullv1/request.json` | `6f90d4084dfeb65e0b1a9e2ab6d5453ef4d7fc2536ed95165d4d326b78a30ab8` |

### Completed scoped repeat

The second attempt has its own counter, scope, queue and restoration records.
The host review reconciles all 153 picture pairs and their bytes, and keeps the
135 completed timing rows separate from the eighteen newest unmatched timings.
Its startup history and later read-only scope do not cover identical intervals.

| Retained private evidence | SHA-256 |
| --- | --- |
| `RepairTrial/coherent350-prefix20-scope-2-root-assessment.json` | `265c384e9bbf07a4fccd211393d1999f650b2ba171f2ab52ff20b58fad085cfd` |
| `RepairTrial/coherent350-prefix20-scope-2-coordination.json` | `63321c32f5f74fc80e8a0d0a651f5720f6c91ad3168edcb917df725409f1d63e` |
| `RepairTrial/coherent350-prefix20-scope-2-stream.json` | `78a693765fb16a8c8b5b828144d136e02c1facf385e8de60837f3f1a4be314bf` |
| `RepairTrial/coherent350-prefix20-scope-2-lease.json` | `3459adf53e6fda6b982ef8a552e767562dd52585e51eea6d5864da765718cacd` |
| `RepairTrial/coherent350-prefix20-scope-2-overflow.json` | `db6a19a8fbb24e0e3005d3612c026e3b166267cb78d911cb5b04f5b1ab6375b1` |
| `RepairTrial/coherent350-prefix20-scope-2-scopes.json` | `9c8e8720cab585567c8a22cfcc3566dd24b308347a1e9608248cfee88ac705e3` |
| `RepairTrial/coherent350-prefix20-scope-2-host-queues.json` | `452dfde1850b88268f749a07d2fd588ee968f10e7c80753eff7c66487fa74321` |
| `RepairTrial/coherent350-prefix20-scope-2-host-review.json` | `265024f4fd39baa0b85e64064dd3e080fae965c31d3f7c0fab08b0255eebcf87` |
| `RepairTrial/coherent350-prefix20-scope-2-host-review.py` | `3064d006c6c6e7b7d728cc8bb688eaee32e74b9c8a33ae4e32b2529c658fd5b1` |

### Completed local decoder jobs and telemetry control

These records bind the actual 350-Mbps clips, portable manifest, native ARM64
build, staged runtime launcher, four completed jobs and final normal-stream
restoration. The preparation freeze predates the launcher's added result-binding
checks; the runtime-readiness review and payload receipt bind the version used.
The initial resource-analysis report records the old runner digest under its
then-current name; those exact bytes are preserved as `run_native_case.v1.py`.
The later runner adds the no-active-telemetry control. No frozen report is
rewritten to make a historical source version appear current.

Local reproductions are `validate_prepared_350.py`,
`decoder350-independent-comparison.py`, `decoder350-resource-comparison.py` and
`decoder350-resource-control.py`. The first reads existing clips and headers;
the latter scripts recompute recorded results without running a decoder.
The historical version-one memory-vote contract/freeze below records preparation
only. Those exact bytes were preserved under `icc-coordinator-v1/` before a
canonical-executable detection fix produced version two. The later completed
receipt is separate evidence; historical manifests were not rewritten as results.

| Retained private evidence | SHA-256 |
| --- | --- |
| `HEVC/current-geometry-lab/README.md` | `ddc33124400794e50294cf55820c9a7d256565599a1fc78f103d792b07c3d49c` |
| `HEVC/current-geometry-lab/preparation-350.md` | `7ec52d097c3de4f5a9c982b6149d267f1d3fa0e194213fdcf5c534b0eb02f1a0` |
| `HEVC/current-geometry-lab/preparation-350-freeze.json` | `aeb2ecfb6db3776fea226a33505e18aad7529d77e1b32a4416589980e9d026b7` |
| `HEVC/current-geometry-lab/validate_prepared_350.py` | `0f298e52b46e8ed083f228355fafae3b72b76b2d5bfa393273e6e0f482ac5f59` |
| `HEVC/current-geometry-lab/prepared-350/manifest.json` | `0337f855b109d1fc630217bb81994da5483c0dbecc72e925f5d3e78b1da54575` |
| `HEVC/current-geometry-lab/prepared-350/manifest.windows-paths.original.json` | `e7079aa467ee2c1c6ec52abfa253ace49b7b73d474b23cfa7233a0e2ff7f0533` |
| `HEVC/current-geometry-lab/prepared-350/path-portability-audit.json` | `603970ab6ad1f1f308d4f321d2fc149913d082627435c326bb623c4d99dd1ccf` |
| `HEVC/current-geometry-lab/native-build-350-1.json` | `81ebe80eec48d01a493785aef6e17ffc097e073fa82c1019f5c110a3e1986291` |
| `HEVC/current-geometry-lab/payload-350-1-receipt.json` | `09d90d8d31a8b2aac7b0fb852f8a0f30ca68f367a585a1d7079a66da5397e06c` |
| `HEVC/current-geometry-lab/runtime-readiness-review.json` | `e22263ecf9dfe6cc1b4b9021acc7e9dff0a965d449ea52d6273ee143e673f6f8` |
| `HEVC/current-geometry-lab/decode_job.py` | `0641d1e766548483fb962c28dc47a84e25fc26b7daa8e5f8dc1b17e74cb07220` |
| `HEVC/current-geometry-lab/run_native_case.v1.py` | `25b5a71c9e99ad1be313efb58460d4c7ad926eb34b68a6e05ecd242824995f28` |
| `HEVC/current-geometry-lab/run_native_case.py` | `79db484a3d8146ddad18a436a6c32b52b13eb1d831525cd0635573b0cba2141e` |
| `HEVC/current-geometry-lab/decoder350-one-canary-1.json` | `3641c581974ac2aa728fcc628ad91e2fbb39606fcfea8a6f587fe017d4637a6d` |
| `HEVC/current-geometry-lab/decoder350-two-1.json` | `081db8857cabc38492334d25f42f6813f0d3bbb12df89041e3e7afda88ee44e7` |
| `HEVC/current-geometry-lab/decoder350-four-1.json` | `94e0526f2d357d6ed93ca97282448dbd14169f8828e25d273727723d2f7349ea` |
| `HEVC/current-geometry-lab/decoder350-one-no-telemetry-1.json` | `fd15abcd9df7f0f477fad4e188a69b07a25cee7858dc99bff8ba21dd6cd30f7b` |
| `HEVC/current-geometry-lab/decoder350-independent-comparison.json` | `8ef94f498f26c1431f824ba9fc6425c5a04c86f778ca18762c8321ecfd2a9bd4` |
| `HEVC/current-geometry-lab/decoder350-independent-comparison.py` | `eb86c067ba530565de9c202510ea6ee242e4b9db1f9fec4f1c6a84e83d6694b5` |
| `HEVC/current-geometry-lab/runtime-coordination-350-1.json` | `df7da0e859561d752ee45ba10c76e58694d2ebcd5844b66e43212ca3ca9112eb` |
| `HEVC/current-geometry-lab/icc-coordinator-v1/icc-decode-coordinator-freeze.json` | `81fb7906cdc84d5cf089c01f42df16b05ce9f00269c07620ece9bdfa622690c2` |
| `HEVC/current-geometry-lab/icc-coordinator-v1/icc-decode-coordinator-contract.md` | `f3ea2ead45830e6ccac486179ae0a7bbc198a0f4d1b28a980f920d98e71bddf3` |
| `RepairTrial/current-geometry-operating-state-plan.md` | `c51d23129396d10d51d77ec0b35fb42153a436f30826fc8b8ef3ba0908d7ecd1` |
| `RepairTrial/current-geometry-operating-state-evidence.json` | `30045117abb61429866e5301e89c088cb3d33056b8627ef4d6ef55b112738701` |
| `RepairTrial/decoder350-resource-comparison.json` | `0b621e7b3f81bce3689881656aaea0a58ab45539c62561ac2f68dc951f575bdd` |
| `RepairTrial/decoder350-resource-comparison.py` | `6b684e03d4bd19798718590adacd7dd242d6d934adc504cebab1d766ea0d7371` |
| `RepairTrial/decoder350-resource-comparison.md` | `80cf9bd3ff378d61a013cb74dd3f032c95df6591d2c620a8f64def863707d760` |
| `RepairTrial/decoder350-resource-control.json` | `5d5eb5d90a8c094390dde71733939723f7af5c310af4c52434874e83fe0fcf30` |
| `RepairTrial/decoder350-resource-control.py` | `4662f4365282de380b0ca41f538f3bb56e8eda26f054aef0e086ba65a1a6a961` |

### Completed ICC and copy-timing follow-ups; retained208 preparation checkpoint

Version two of the ICC coordinator was prepared after fixing canonical native-
receiver detection, passed 61 offline tests and then ran the recorded workload.
Its freeze still says prepared/not executed because it is a source checkpoint;
the completed receipt supplies runtime evidence. The copy-timing source manifest
also predates execution and must be read with its separate build and job receipts.
Neither historical status was rewritten. Final normal-stream receipts distinguish
resume of an existing host from a fresh restart.

Local replays are `decoder350-icc-independent.py`,
`decoder350-icc-independent-review.py` and `decoder350-input-copy-review.py`.
They reaggregate recorded identities/bytes/times and verify source or resource
binding. The retained208 `verify.py` rereads the separately frozen package; its
software-decode receipt and parameter-set comparison do not constitute a headset
run. Ten preparation tests and the 15 dedicated/76 total runtime tests passed.
At that earlier preparation checkpoint no retained208 runtime result existed.
The completed jobs below supersede that status without rewriting their freezes.

| Retained private evidence | SHA-256 |
| --- | --- |
| `HEVC/current-geometry-lab/icc-decode-coordinator-freeze.json` | `147dbb2987ef5b3d508b636998c6ecb7fe546335f1eb25817d4c1155fa8576a9` |
| `HEVC/current-geometry-lab/icc-decode-coordinator-contract.md` | `065ca25180d39aa67538bf24369e39b66eaed67f1ec7fd32261da0acd24c677d` |
| `HEVC/current-geometry-lab/run_icc_decode_case.py` | `cdaa4483bc5f543097de290fe4f2942ee4fb64ae5ec64caa6aa67181acb3041f` |
| `HEVC/current-geometry-lab/icc_decode_contract.py` | `0cbe6754d7274c2b0915d134169527cc5d35d85708f8407923cb90fe6c33b48f` |
| `HEVC/current-geometry-lab/decoder350-one-icc-1.json` | `c75771a626c128256982353660878d4d4a95c747bab4f18e7aca8ba28c4e0e3f` |
| `HEVC/current-geometry-lab/decoder350-icc-independent.json` | `da2549dd150bc26abaec6df590f04099404795efe41a16deaa88acb697708e63` |
| `HEVC/current-geometry-lab/decoder350-icc-independent.py` | `229f5f78a972f2f3ac473f9c3abbc5b4cac2a5d66c14383f45485a290a724e59` |
| `HEVC/current-geometry-lab/decoder350-icc-independent.md` | `7d801fdbdeeb7c9aa7d22d5639b8a859d8f886dad3f3760b93689642ba30ef5e` |
| `HEVC/current-geometry-lab/runtime-coordination-icc-1.json` | `8a7fe2271c96e16ce8c2e48b5a02aa5fce8fefcb146c0fcbb8994d228cfc2e0e` |
| `HEVC/current-geometry-lab/normal-restore-after-icc-1.json` | `6cc445213cbea8430fa80b345e9f2e4c7321e8b3f518e1bcc900abfd17d051d5` |
| `RepairTrial/decoder350-icc-independent-review.json` | `8841bf9f93309c0aed4281660e9de32794814a24c77e24eba2590b1186e49ee7` |
| `RepairTrial/decoder350-icc-independent-review.py` | `1ea4a1ed808a122945c8d250872d280f4a2c3b428932181cb0e717df6cb2c80e` |
| `RepairTrial/decoder350-icc-independent-review.md` | `a98477f849302b049b636b0c28b9d101bc0c1679975faa2fa6f9fbd913a97198` |
| `HEVC/current-geometry-lab/decoder350-byte-cadence-correlation.json` | `165d660279f0f9e95185aa18a88ff234a079def903e5b538c775fc3d39d61d01` |
| `HEVC/current-geometry-lab/copy-timing-v1/source.json` | `249a1f0eccff381d3a55a05b6a6a11f3458af454f8b4a94d1025e19f7f893294` |
| `HEVC/current-geometry-lab/copy-timing-v1/native-build.json` | `ddb7291ad812b1972996f174e795eeda23bfb5e5204fdb86ddf540e260220fec` |
| `HEVC/current-geometry-lab/copy-timing-v1/hardware_decode.cpp` | `86d28c308ab1a5fb5e5699d16dd649a43184a799a6b1e4ae296f184885a3d085` |
| `HEVC/current-geometry-lab/run_copy_timing_case.py` | `66a909d44af62047138253933e450db0c822d3cc7daedf7c36fe13c65a0cab46` |
| `HEVC/current-geometry-lab/decoder350-copytiming-1.json` | `71fa7e04358648a203566bef7f9003386746ff3c2575a3345a2f598a3b0ab102` |
| `HEVC/current-geometry-lab/decoder350-copytiming-summary.json` | `16c9b12994994df8ddbe1c753d273b9271d28026c1ae42d63447a066755f9cbb` |
| `HEVC/current-geometry-lab/runtime-coordination-copytiming-1.json` | `224af45ccdc466cefbd81c44695bf49a64f81dc90f5eafce16dbc765a75eba2e` |
| `HEVC/current-geometry-lab/normal-restore-after-copytiming-1.json` | `3a60161373ccbf3add622d0f2d4e919678dfe7a4db27699c7f3684d7c3e5bebe` |
| `RepairTrial/decoder350-input-copy-review.json` | `9f7c97c51e055d35d65941cf1e3cde2e86cf60f94ede579d210587aee6506868` |
| `RepairTrial/decoder350-input-copy-review.py` | `b100eb21a5bb384f3aaca75c244f3348d82bc8424b9826476b54de5c774aaa5c` |
| `RepairTrial/decoder350-input-copy-review.md` | `9e3fa71d7f9ac182b552793e92fde86a0e955e19f69c21adfcbd464ac3bb4dd1` |
| `HEVC/current-geometry-lab/prepared-one-rate208/freeze.json` | `28ddc843c36ce8e9c2dd318da1229647e122cbe815b9be174852bc6d036532fb` |
| `HEVC/current-geometry-lab/prepared-one-rate208/manifest.json` | `fceddeca7cd9e17faaa4762370c48751ca74c60b166a68c939c79f22d5ab03e6` |
| `HEVC/current-geometry-lab/prepared-one-rate208/README.md` | `6a5f753531ac31b46d44e6396b6ceb1d8c01d7796520836fa02b9baed769542a` |
| `HEVC/current-geometry-lab/prepared-one-rate208/preparation-report.json` | `ea07840f99e2a21aba7c83678c9174db4269af8ab7fe0d1a1baff5f495fae957` |
| `HEVC/current-geometry-lab/prepared-one-rate208/parameter-set-comparison.json` | `3334dee9645aebdd226af51b39efdf3ab6ff912f402806969188821d97f184a2` |
| `HEVC/current-geometry-lab/prepared-one-rate208/software-decode-receipt.json` | `49156806b9570cc7cf1419ea791d9e1e98597a69f43eb322ff2bb84ecd6dee39` |
| `HEVC/current-geometry-lab/prepared-one-rate208/prepare.py` | `689f087d33321e6494ae4ca60db90b5efb92ca10d3dbca5e0ac83593f9f5f632` |
| `HEVC/current-geometry-lab/prepared-one-rate208/verify.py` | `cdbbede4aa0b24dec97a6889218237d3bd666afa8762f9fc72348bc13ee8340b` |
| `HEVC/current-geometry-lab/retained208-runtime-freeze.json` | `20a6235f2d9fb0fa895c085d71c928f2ced03651e106ee7971fb787c79f67ce0` |
| `HEVC/current-geometry-lab/retained208-runtime-contract.md` | `d6eae59f1e86df592db5f47645afa7a3b22da224ccafdb0fe86b911be8ed0bb7` |


### Completed 208 and metadata controls; offline full-pass build

The retained208 and metadata208 preparation/runtime freezes still state their
historical pre-run status. Later job receipts and restoration records establish
the completed experiments; those frozen source records are not rewritten.
Independent local replays are `analyze_decoder208_vs350.py` and
`analyze_metadata208_vs_original.py`. They validate pinned clip framing, callback
identities, byte accounting, source/package hashes and cleanup. Six focused
analysis tests reject malformed identities/geometry and verify phase alignment;
the metadata package also has independent parameter/SEI/pixel-hash checks.

The exact installed-driver audit binds 28 private sources. Its source addresses
and capability/clock findings are summarized in original prose; its disassembly
and binaries are not published. The PC caps receipt explicitly records that the
source-to-binary build binding was not verified and no frame was encoded.

For Fullv1, the old source-preparation manifest/diff above remain unchanged.
The new build receipt binds 28 historical sources, toolchain and binaries, mock
NVENC tests and native/managed readback checks. It preserves the old 350v2 and
observer files and the existing dirty app files. No live trial is claimed.
The separate fullpass runtime chain remains under review and is intentionally
outside this cutoff. A digest identifies evidence, not hardware acceptance.

| Retained private evidence | SHA-256 |
| --- | --- |
| `HEVC/current-geometry-lab/decoder208-one-1.json` | `fe79546718f62400d093437efd73929883157fa611d65f0959d83779f0bc9b8e` |
| `HEVC/current-geometry-lab/decoder208-vs350-independent.json` | `e69b59117191271b6b188e156ee9e806f3ec0e5a3fbddc28f856da31003b3d16` |
| `HEVC/current-geometry-lab/decoder208-vs350-independent.md` | `889982ac8d577c7bb2cf32c34ff36024cf7c472716f37b9106aa2910051480e5` |
| `HEVC/current-geometry-lab/analyze_decoder208_vs350.py` | `969f1c48776c2f476d393ff91259a23f0dd31c057b1378ab26a531244a3b8743` |
| `HEVC/current-geometry-lab/runtime-coordination-retained208-1.json` | `6e3ca95ab971ee4a5d83f3e3c632ecb3eac2d2e4091e0c0f2a2f437a6da46cd0` |
| `HEVC/current-geometry-lab/retained208_job.py` | `87dd20018c5e49a9575baf48e882b442ed7d558f74afcdd9e184bc07f068eef7` |
| `HEVC/current-geometry-lab/run_retained208_case.py` | `e0836783fb84ac826f39ea85bba0c512a29d7012534008c10cde01875deed36e` |
| `HEVC/current-geometry-lab/decoder208-meta350-one-1.json` | `35942e1ea0612a0aacc7dcfc9d91d1c413550c04c7efc1f3c4c71dffac7aa5c6` |
| `HEVC/current-geometry-lab/metadata208-runtime-independent.json` | `1394e5b1faae6c82a6c68fab6344417ca85c16a3fe2efaff05fb0200c3da135e` |
| `HEVC/current-geometry-lab/metadata208-runtime-independent.md` | `b20fd67a98dd5a87519462c02c3e6004ee0c98698fb8205c5b07b7ab7e608d5e` |
| `HEVC/current-geometry-lab/analyze_metadata208_vs_original.py` | `29b9081e239ba0965cd6ac801b7c92fe4b6df63f63fccd61275cbf7ff5f71bdc` |
| `HEVC/current-geometry-lab/runtime-coordination-metadata208-1.json` | `4a293e59e1ffbf7100c013cdb95bea862b335d1c2e4e27cc9de2783a0b7eae1c` |
| `HEVC/current-geometry-lab/metadata208-runtime-freeze.json` | `2ba1b3671489c84072dfb534131cbe4ed512c451c8901b28b9deb3e0a294f12b` |
| `HEVC/current-geometry-lab/metadata208-runtime-contract.md` | `744c472c61b11deaeb89257500cba9570e591e6697761072a52337e58371cf2d` |
| `HEVC/current-geometry-lab/metadata208_job.py` | `65eb73b094b3374749d39720e93c8e8f0c79c4a32ecdd7ede892cd6d99d92b20` |
| `HEVC/current-geometry-lab/run_metadata208_case.py` | `84957ba31ccec078e9d1058f3caf77a6a21db968037f4d55ca98ded4806d2f4e` |
| `HEVC/current-geometry-lab/prepared-one-rate208-meta350/freeze.json` | `3f106cd0a277c876bd7b171ea9246f70a07b1e297f119039d21ab3609c61874f` |
| `HEVC/current-geometry-lab/prepared-one-rate208-meta350/manifest.json` | `e1a74f2660cc69e5a7bc68d8b5659aec8b7ff15a4987e0f24b7b77d0713cb60f` |
| `HEVC/current-geometry-lab/prepared-one-rate208-meta350/preparation-report.json` | `2a56d5adc8b97cd0a15d6c551acf890823c8f5ed79de4ae3d295a902ff29fca4` |
| `HEVC/current-geometry-lab/prepared-one-rate208-meta350/software-decode-receipts.json` | `1fc261f055418048e6f0dc18d2e2ec69202b872d746c5213ab950f8bf5160d06` |
| `HEVC/current-geometry-lab/prepared-one-rate208-meta350/prepare.py` | `153f80f380ae77992541c337bdb65e2cc27126906d4e627d37b402c0443b64a5` |
| `HEVC/current-geometry-lab/prepared-one-rate208-meta350/verify.py` | `1712925de09822c3122ad5f67dd5c8b452118df312b37071674279241e66fb88` |
| `HEVC/current-geometry-lab/pc-codec-caps-current.json` | `5d0fd537b6872cca32f6bf1eb877c80d651d3881593fdce613948a827dc5d0d6` |
| `RepairTrial/iris-installed/decoder-rate-policy-audit/README.md` | `d63dfe44754e699faa1939c0d6286c5dbf8834f1e9eb6cfcbd659087bcdb4656` |
| `RepairTrial/iris-installed/decoder-rate-policy-audit/evidence.json` | `6490d5e1c203e091d53c0843e88f59cbda5e2dbfd8ac2a8bc177080f94c48a7d` |
| `RepairTrial/iris-installed/decoder-rate-policy-audit/audit.py` | `de88c3bf53652b0af45849004444aed9af8f532a4b50db7e37d40363bea2eccd` |
| `RepairTrial/iris-installed/decoder-rate-policy-audit/clock-binding-inventory.json` | `147563a68fce2e3c568a50cdb28c056d0171b91d4bbbc682b17763b573bf1baf` |
| `RadioTrace/Coherent350Fullv1/build-receipt.json` | `72074f03a9c4ff60c0585d8de9f5e2d45757cf1b3d43cea752a35c7ba90924db` |
| `RadioTrace/Coherent350Fullv1/source-manifest.json` | `6e1af00f6c6e55a322628d6ae13fd8cb3b9eca7170192c9472cf9afe4d29c0da` |
| `RadioTrace/Coherent350Fullv1/source-diff.patch` | `0ed8a24aab4b9c7e25674f65893fa09d13d158007f265bf8c4074b83d010db1b` |
| `RadioTrace/Coherent350Fullv1/toolchain.json` | `54a6011ae4126cea27a2abf5f0a3a6de2b94d181e22b8c45595e23691608956f` |
| `RadioTrace/Coherent350Fullv1/output-manifest.json` | `976a3a4d33ce511afbcce0f5dc4eec210f0becb49a10868af50d35850f38c28b` |
| `RadioTrace/Coherent350Fullv1/passive-abi-check.json` | `ad4ecceb2926684b70c523a802a6b30a115dcbc5a74574bb1c15fb236b140d31` |
| `RadioTrace/Coherent350Fullv1/native-path-check.json` | `1dd15c302bceecd63ad54ef67bb8132b9882ccf6e0fe912608ce7cbc44954af0` |
| `RadioTrace/Coherent350Fullv1/test_fullpass_observer.py` | `c88c1b840f4a12ad69293e6f8399573fd3dde29acec356fcaad5b5311270d4fc` |
| `RadioTrace/Coherent350Fullv1/observer-registry.diff` | `991f2f90c9c802a8faebf70f6c9a112b2e5b3563c51df5223e859468ac196f89` |
| `RadioTrace/Coherent350Fullv1/README.md` | `52b5a3d5bdc7c7ef888d0cdd7975310ee3d5d8e39c81ddb5cc261a80f0236dd5` |
| `RadioTrace/Coherent350Fullv1/integration-review.md` | `d1ca486e190b356d8ce23be9922167f6d08535fa74af5867168753964d0b79d8` |
| `RepairTrial/host_queue_observer_fullpass.py` | `79ec6cfdfbee8e48fb60f4dc457d0fb687738483a15aa238b1d9a846c614829e` |
