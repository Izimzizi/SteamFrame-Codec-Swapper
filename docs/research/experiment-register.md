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
| Why did the signed timer repair still permit an overflow? | [Reused-slot publication and timed request capacity](receiver-stream-map.md#fixed350-timed-overflow-during-rx-slot-reinitialization) |
| What did the latest minimum-CU change actually do? | [Accepted request, emitted SPS and failed cadence](host-stream-map.md#minimum32-coding-unit-trial-accepted-and-emitted-but-cadence-still-failed) |
| Do mutexes, PM or IRQ dispatch explain the receiver backlog? | [Matched thirteen-event observation](receiver-stream-map.md#matched-thirteen-event-observation-small-observed-lock-and-pm-spans) |
| Are the high-rate failures still a radio/USB problem? | [Recent send-queue and delivery evidence](radio-stream-map.md#loaded-follow-ups-do-not-show-a-sustained-host-send-backlog) |
| Is H.264 or multistream already a working alternative? | [Codec and decoder-session audit](codec-stream-options.md) |
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

## Controls that remain untested or incomplete

- **Coherent350v2 reliability and useful-rate filling:** the modern result above
  is measured, but an uninterrupted smooth 350-Mbps useful stream remains
  unproved. The temporary arithmetic/window repair does not cover initialization
  ownership or the entire retry-list construction.
- **Timed-prefix mitigation:** 18 offline tests and independent exact-function
  emulation support bounded collection through the existing 20-ID emitter.
  Native capacity aborts intentionally become retries; initialization/generation
  races and recovery liveness remain unresolved. No runtime integration or live
  result exists. See the [offline proof and limits](receiver-stream-map.md#timed-prefix-mitigation-offline-evidence-only).
- **Equal-current-geometry Main10 one/two/four sessions:** a private harness and
  350/400 CPU-only plans are now implemented and pass 25 offline tests. They
  normalize total 1664 x 6656 area and four requested constrained slices. No
  clips, ARM64 executable, headset job or performance result exists. See its
  [implementation and confounds](codec-stream-options.md#current-geometry-comparison-prepared-offline-not-yet-run).
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

Each latest experiment restored a fresh normal Main10 helper, original receiver
instructions, detached threads and cleared activity state. Observer cleanup and
root tracing preservation are separate from performance acceptance. Same-trace
90-Hz preroll/tail does not rule out workload-dependent observation cost.

The repository publishes original explanatory research. Private reproducer
names, formulas and digests make local evidence auditable; without the retained
inputs these hashes alone do not make the measurements independently rerunnable.
Raw proprietary binaries, decompiler listings, authentication material, personal
logs and operational captures are excluded. No app release or experimental
runtime control is shipped by this documentation publication.

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
| `HEVC/current-geometry-lab/README.md` | `74341181b45649c131087d9008ed48103100298a3434d6871cc2386b5184e773` |
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
