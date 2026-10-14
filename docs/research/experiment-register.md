# Streaming experiment register

Publication cutoff: completed evidence reviewed on **2026-10-14**. This index
distinguishes a measured negative result from an untested option. A completed
capture can still fail streaming performance; a clean observer is not a pass.
It links the detailed maps rather than replacing their build and timing limits.

## Where to start

| Question | Research entry |
| --- | --- |
| Why can requested CBR differ from measured useful video? | [Host budgeting and coded bytes](host-stream-map.md#requested-bitrate-accepted-budget-and-coded-bytes) |
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
| Historical coherent350 (`coherent350-no-filler`) | 69.734 s | 323.91 / 89.91 | Six presentation timeouts; no AFTM/Iris trace. This older helper differs from the new offline350v2 candidate. |
| Fixed380, timer plus six-frame window (`coherent380-window6-1`) | 149.694 s counters, 150 s requested | 327.35 / 89.45 | Failed smoothness: two source-side texture stalls, progress recovery, four presentation timeouts. |
| Fixed400 passive counter observation | About 2 s | 340.84 / 84.03 | Guard-aborted; accepted400 did not preserve 90-Hz cadence. |
| Fixed400 nine-event IRQ/Iris trace | About 2 s counters, longer trace | 333.91 / 81.54 | Guard-aborted; added queue-to-input residence was not a comparable final IRQ dispatch delay. |
| Fixed400 cross-slice filter disabled | 1.999436 s | 336.35 / 82.02 | Actual emitted PPS verified; decoder-delay/backlog guard failed. |
| Fixed400 thirteen-event lock/PM trace | 1.999240 s counters, 10.000685 s trace | 304.19 / 68.53 | Cadence/output guard failed; bounded IRQ-thread contention/PM spans too small to explain the broad residence rise. |
| Fixed400 minimum32-CU, CTU32 unchanged | 1.999455 s | 307.79 / 69.02 | Actual emitted SPS verified; cadence guard failed, no simultaneous kernel-stage trace. |

The five fixed400 rows refer respectively to `coherent400-passive-scope-1`,
`coherent400-irq-1`, `coherent400-no-cross-filter-v2-2`,
`coherent400-lock-pm-1` and `coherent400-min-cu32-1`. The fixed380 row specifically
uses `coherent380-window6-1-stream.json`: 327.352703 Mbps and 89.449204 FPS
over 149.693898 seconds, rather than another 380-budget attempt.

The historical coherent350 run accepted 350/402.5 Mbit/s and 4,472,222 VBV bits,
with one-second useful rates 313.400--328.268 Mbps and sampled Waterfall above300.
Its six presentation timeouts prevent calling it a clean smoothness pass.
Its older helper had first-callback activation and 120-second reconfigure-only
expiry; it lacked the current dormant/manual-begin contract and matched timing
coverage. It is real measured evidence at a 350 budget, not a fixed-nominal90
experiment or proof of useful350 Mbps.

The latest two windows account for all **137/138 pictures** and
**76,018,645/76,926,754 bytes** respectively. Strict completed AFTM covers
120 pictures in each, excluding the newest rows by design. Nominal allocation
is calculated as `400,000,000 / (8 * requested_fps)` per picture; observed
picture totals are 99.432%/99.767% of that arithmetic. FPS-denominator variation
changes mean nominal allocation by only 0.449%/0.574% from fixed90. Those
comparisons explain the aggregate byte deficit without asserting exact CBR
filling, equal quality or sustainable 90-Hz service.

Waterfall is a different counter: it includes assembled transport overhead and
uses a binary-megabit divisor. Its latest ranges, about 367--385 and 380--382,
remain above 300 while cadence is near 69. It therefore does not validate the
user's smooth-streaming goal. Requested bitrate, accepted NVENC fields, useful
bytes, decoder completion and physical presentation are distinct quantities.

## Controls that remain untested or incomplete

- **Coherent350v2:** built and checked offline as a lower-load reference. It
  retains the original codec/QP path and uses average350 Mbit/s, peak402.5 Mbit/s
  and VBV4,472,222 bits, with dormant explicit activation and a bounded,
  nonrenewing lease. No live result exists at this cutoff. Neither its name nor
  its budget demonstrates useful350 Mbps.
- **Equal-current-geometry Main10 one/two/four sessions:** proposed, not
  implemented or tested. Prior 2048-width local tests cannot establish behavior
  at the current 1664-wide packed geometry or justify a full game integration.
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
The offline-only350 helper is
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
| `RepairTrial/codec-route-audit.md` | `f01a5721896160e9fe6eff5994c1101783660089835aedcdecbbe2385c32230c` |
| `RepairTrial/codec-route-audit-evidence.json` | `ddca8120072153c4e609ce207b5f5c13b936d2eb5b7a628d5f7b6fcb80d089af` |
| `RepairTrial/audit_codec_routes.py` | `f558e05d454a720f2e94546618911396a2bb68f8aa175c6b9a6f59a8ded94ec4` |
| `RadioTrace/coherent350-no-filler.json` | `856f32e0211d26f82fefec276cb3c7bdbc25a1200a03b6871b462d1aaf99e8d1` |
| `RepairTrial/fixed350-evidence-audit.md` | `ade36c0bcd9e4b8652b9e2ab836224e4f28056012a2f8d99a729ff4e42191237` |
| `RadioTrace/Coherent350v2/README.md` | `1d2f56cb9e3b362825bd913364632f83d5fcbf06bc4d1cda2ec10f30a0a8309e` |
| `RadioTrace/Coherent350v2/source-manifest.json` | `db02880e1e0622fe9e67c8ee88221ec270b1711ebf7de6eb3a44d20fe8a1a0e1` |
| `RadioTrace/Coherent350v2/output-manifest.json` | `7af5d9a85c477fd2abdc588ba6202405f03b542df420230738d865d450d29fe3` |
