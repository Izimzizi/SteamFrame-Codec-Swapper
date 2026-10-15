# Auto tune presentation diagnostics

## Resolution/refresh exploration update (October 15, later request)

The user superseded the Valve-reference goal below. Current auto tune explores
explicit width/Hz/pass combinations and returns several options. It no longer
requires an improvement over a Valve reference and no longer recommends a stock
reset. See [current behavior](../auto-tune.md). Version 3 reports record each
candidate's geometry and refresh rate, plus all saved choices.

The implementation has 92 passing managed tests covering the mode/pass matrix,
bounded search, exact confirmed settings, configurable issue thresholds,
unmeasured recommendations, display-mode crash recovery and encoder ownership
during restoration. Both embedded display-control Python blocks parse, and the
Windows release build succeeds. The real UI was checked through an unavailable
connection: it displayed and saved the explicit unmeasured option, and its Load
button populated the editor with 1280-pixel width / 90 Hz / 100 Mbps without
applying it. The app was closed afterward to discard those review-only edits.

Live testing on the reconnected dedicated USB 3 path found and corrected three
integration problems:

- A full-pass transition inside a running encoder was followed by an NVIDIA
  driver event 153 at 11:16:04 and NVENC invalid-device failures. The API had
  accepted reconfiguration. App-owned multipass changes now retain the current
  allocation until fresh initialization. Seven native tests pass, including
  transition/restore guards and caller-owned parameter preservation. Subsequent
  full-pass screens completed without another GPU fault; this does not identify
  the driver's internal fault or prove that every full-pass configuration works.
- The Frame's `vrlink.client` per-app refresh preference overrides its global
  `steamvr` preference. Changing the global setting left the display at 90 Hz.
  The new path verified actual 120 Hz and 90 Hz transitions, with PC encoder and
  receiver delivery near the corresponding rate. Back to stock clears both app
  and global refresh overrides.
- SteamVR removes explicit host preferences equal to its default when saving.
  The display lease incorrectly treated the absent 90 Hz override as an external
  edit on the next sweep. Ownership now compares the effective value, allows
  native float rounding, and still journals/restores original key absence.

The first partial mode runs (`benchmark-20261015-112433-cbe3e886.json` and
`benchmark-20261015-113346-5550f20f.json`) each completed 15 screens before the
last ownership bug. They saved explicit measured choices labeled as screens,
and restored the starting 400 Mbps/1664-width/90 Hz setup. They did not complete
the longer confirmations. Earlier numbers below belong to the prior fixed-mode
comparison, not the current exploration.

The subsequent full run `benchmark-20261015-114136-84c25097.json` completed all
18 mode/pass screens and nine refinement/confirmation windows. It saved three
choices and restored the starting setup, with no remaining recovery journal.
The 1280/90 quarter-pass confirmation measured 71.3 Mbps and 90.0 encoded fps
at a 75 Mbps target, with a 0.616% affected-frame estimate. Two 120 Hz
confirmations exceeded the default serious-issue threshold and were labeled
accordingly. In particular, the 1280/120 trial continued at about 267.5 Mbps
after its target was lowered to 100, delivering only 97.4 encoded fps. The
internal cause of that overshoot is not established.

Final confirmations now start fresh encoder sessions to test the intended
settings without the preceding bitrate trial's rate-control history. A poor or
invalid confirmation can trigger one bounded confirmation of another measured
pass/quality map for the same width/Hz. These changes retain failed observations
and explicit recommendations; they do not reintroduce a flawless-run gate.

`benchmark-20261015-115847-0b2f3b4f.json` reused the 18 completed mode screens
above, with explicit provenance, then measured 12 new refinement/confirmation
windows. Fresh sessions alone did **not** fix the severe 120 Hz cases. A
100 Mbps / 1280-width quarter-pass confirmation still encoded 256.3 Mbps at
96.6 fps. At 1664/120, a fresh 180 Mbps single-pass confirmation encoded
285.3 Mbps at 67.5 fps, while its bounded quarter-pass alternative encoded
132.6 Mbps at 118.8 fps with 1.934% estimated affected frames. Both 2048/120
confirmations had serious delivery deficits. The run restored the original
settings without a pending recovery journal.

Read-only snapshots during two poor confirmations showed Valve submitting
much lower NVENC average-bitrate requests (8 Mbps at 1280/full pass and about
2.27 Mbps at 2048/quarter pass) despite the app's manual target and accepted
CBR policy. Aggressive quality offsets remained enabled. Those observations
establish that target, accepted NVENC request and actual payload can diverge;
they do not isolate the cause to GPU saturation or prove that a fresh session
solves the controller/quality-map interaction.

Selection now keeps screened alternatives at other width/Hz combinations
instead of allowing one poor confirmation to exclude them all. Within a mode,
confirmations take precedence. Repeated observations of identical settings
are grouped independently of whether the trial requested a fresh session, with
the more conservative score retained.

The final verification report `benchmark-20261015-120840-5b7bcd1d.json`
explicitly combines those prior measured windows with two new 35-second
confirmations at 90 Hz; it is not a new complete matrix sweep. The repeated
1280/90 quarter-pass / 75 Mbps trial encoded 276.4 Mbps at 88.6 fps, with a
3.298% affected-frame estimate, worse than the earlier same-settings result.
The conservative repeated-observation rule retains this worse result. The
2048/90 single-pass / 200 Mbps confirmation encoded 314.5 Mbps at only
57.2 fps, so a higher-resolution recommendation was not justified by the
longer measurements.

Saved choices, all timestamped `10-15 12-08-40`:

| Choice | Width / Hz | Pass / target | Actual Mbps / encoded FPS | Evidence |
| --- | --- | --- | --- | --- |
| Balanced | 1664 / 120 | Quarter / 180 Mbps | 132.6 / 118.8 | 35-second confirmation; 1.934% affected estimate |
| Lower refresh | 1664 / 90 | Single / 150 Mbps | 96.1 / 89.9 | 10-second screen only; 2.211% affected estimate |
| Lower resolution | 1280 / 90 | Quarter / 75 Mbps | 276.4 / 88.6 | Repeated confirmation; 3.298% affected estimate |

The lower-refresh choice has explicitly lower confidence. None of these is
claimed flawless or a guaranteed best setting for every game. The original
400 Mbps / 1664 / 90 Hz, quarter-pass CBR and quality offsets were restored;
both PC and Frame reported 90.0000076 Hz, the helper had no pending policy,
and both recovery journals were removed. The desktop launcher resolves the
updated `artifacts/BenchmarkExplorerApp` build. Parsec was left running.

### GPU load investigation

The PC has an RTX 5090. A concurrent `parsecd` process owns a separate HEVC
3840×2160 encoder session near 120 fps; it was left running. Windows engine
counters attributed approximately 6% on each of the three encode engines to
Parsec and about 59% per engine to SteamVR during a full-pass sample. These are
per-engine readings and must not be summed as overall GPU utilization.

Read-only `nvidia-smi` samples were recorded every three seconds, then matched
by QPC time to actual measured windows, excluding transitions. In the 11:33 run,
single-pass encoder utilization averaged about 28%, 41%, and 57% at 120 Hz for
1280, 1664, and 2048 widths respectively. Quarter pass at 2048/120 averaged 64%.
Overall GPU activity was roughly 16–26%, temperature 44–46°C and VRAM use around
6.5–7 GB of 32 GB. The 2048/90 full-pass screen delivered about 75 encoded fps
in two runs versus about 90 for single pass. Encoding pressure is a plausible
contributor, but average utilization alone does not prove saturation or identify
the cause of every presentation hitch. No comparison with Parsec disabled was run.

Raw local GPU samples: `artifacts/BenchmarkLab/gpu-mode-samples.jsonl`.

The complete 11:41 run provides the same largest-mode comparison:

| 2048-pixel width / 120 Hz | Encoded FPS | NVENC utilization, window mean |
| --- | ---: | ---: |
| Single pass | 116.9 | 52% |
| Quarter-resolution multipass | 118.2 | 64% |
| Full-resolution multipass | 73.5 | 63% |

These short windows contain only three to four GPU samples each. The encoder
receives a packed 2048×8192 image at this width, about 16.8 million pixels per
frame. Full pass had a repeatable delivery penalty without overall GPU,
temperature or memory-capacity exhaustion. Per-engine scheduling, encoder
memory traffic and the concurrent Parsec stream remain possible contributors;
the measurements do not isolate them. CPU snapshots were modest (vrserver
about 0.4 logical core, compositor about 0.2), so this evidence does not show
CPU saturation either.

## Practical tuning policy and Valve reference (October 15 follow-up)

The user changed the goal from zero-hitch certification to finding a useful
quality improvement before major performance problems, with Valve defaults as
the guaranteed fallback recommendation. Historical strict failures below remain
observations, not proof that those profiles are unsuitable for playing. See
[the current evaluator and search policy](../auto-tune.md).

Live verification found that an ordinary stream reconnect retained the NVENC
session; switching from an app CBR override to the default policy still reported
`NeedsNewSession`. The benchmark now uses a bounded SteamVR restart for that
transition, resumes the same host, journals replacement helpers before writes,
recreates the scene and receiver monitor, and starts fresh measurement windows.

There was also an independent helper defect: default monitoring could retain a
previously accepted RC mode when Valve changed its own configuration, despite
the app never overriding RC in that session. The helper now tracks whether an
explicit app RC override actually occurred. Default/inherited-RC requests pass
Valve's own changes through; restoring an app override still respects the
reconnect guard. Fake-NVENC regression tests cover both cases and unchanged
caller-owned inputs. A fresh live reference subsequently accepted default VBR,
single pass, Main10, with zero policy errors and no pending policy. The original
CBR/quarter-pass request and manual 400 Mbps target were restored afterward.

This corrects the benchmark's reference, not a demonstrated radio-throughput
ceiling or a claim that every presentation timeout is fixed. The receiver
observer additionally waits up to eight seconds for the resumed streaming scene
to acquire focus before publishing its first sample; once sampling begins,
identity/route changes still invalidate measurement.

### Completed practical comparison

The October 15 search used the dedicated USB 3 wireless path, active 90 Hz,
1664-pixel stream width, HEVC Main10/P2 and synchronous sending. It screened
single/quarter multipass and the permitted quality maps. The final comparison
remeasured Valve's encoding reference and the two headroom-adjusted finalists
with the moving unattended scene, over equal two-minute windows:

| Setting | Window | Actual video | Affected-frame estimate | Combined score |
| --- | ---: | ---: | ---: | ---: |
| Valve automatic encoding reference | 122.29 s | 151.09 Mbps | 2.480% | 20.21 |
| 180 Mbps, quarter multipass, gaze −20/periphery −12 | 122.30 s | 166.65 Mbps | 1.054% | 50.56 |
| 315 Mbps, single pass, gaze −20/periphery −12 | 121.36 s | 168.09 Mbps | 0.851% | 55.82 |

The single-pass finalist won and was saved as
`Auto tuned HEVC 90 Hz 2026-10-15 10-22-45`. Its target includes 10% headroom
from the 350 Mbps screen. The original 400 Mbps CBR/quarter-multipass setup was
restored and read back successfully; applying the new preset remains explicit.
These counters estimate affected delivery, not unique optical dropped frames.
The reference retains the current geometry, bit depth and other fixed controls;
this is not a whole-device factory-reset comparison or a guarantee for games.

This comparison also exposed a remaining binary 1% performance cutoff. It has
been removed from finalist eligibility and winner selection: warnings remain
visible and stop repeated upward escalation, but valid observations compete by
relative score. Re-evaluating the recorded windows with the final code selected
the same two finalists and the same saved winner. Regression tests also prove
that the 1.054%-style result can beat a worse reference rather than be discarded.

Local source reports (not uploaded):
`benchmark-20261015-094443-6f23009e.json` contains the original screening;
`benchmark-20261015-102245-63ee5f0f.json` retains that evidence as
`Previous search` and contains the three fresh final windows. The first run was
stopped after screening to correct finalist selection; no interrupted window
was promoted to a completed comparison. Final verification: 77 managed
benchmark tests, seven native tests and the Windows release build passed.

## October 14–15, 2026 investigation

The original `No candidate passed final validation` result combined several
different failure types. A trial can fail because of a genuine presentation
stall, missing measurement history, a stale observer, or an encoder/stream
failure. These are not interchangeable evidence of a radio bandwidth ceiling.

### Confirmed history-ordering defect

Two consecutive PC timing entries, frame IDs 208393 and 208394, had predicted
vsync times 2328.6402403895954 and 2328.640239778973 seconds. Sorting by those
timestamps reversed their order and invented two missing-history gaps. Both
entries were present. Timing now follows the unsigned frame index, handles
wraparound, and ignores entries already processed. The regression test supplies
equal/reversed timestamps and verifies that repeated reads create no gaps.

This correction does not erase genuine drops. Frame 208394 in that capture also
contained a nonzero per-frame dropped count; the false gaps alone do not certify
the entire captured interval as smooth.

### Reduce measurement interference

The old loop repeatedly connected/disconnected background OpenVR clients,
started status threads inside `vrserver`, wrote helper status files, and launched
SSH/Python/`vrcmd --stats` on the headset. The benchmark now retains one background
OpenVR connection on each device and reads sequence-checked encoder metadata
published by the helper's existing completion callback. No status thread or
status-file write is needed for each measured encoder sample. Counters, runtime
identity, configuration revision, freshness and the active wireless path remain
validated.

The scene's submitting thread uses the scoped Windows Games multimedia scheduling
class. A captured long scene interval was 29.0782 ms, including 28.9271 ms of
previous render/submit work and only 0.071 ms of pose waiting. It was not evidence
that waiting for poses alone consumed that interval. PC timing is polled every
250 ms because this runtime returns 128 history entries even when asked for 256.

At 100 Mbps / 90 Hz, separate 60-second diagnostic windows observed 16 receiver
timeout presentations with regular polling, nine with receiver endpoint-only
reads, and four with both encoder and receiver endpoint-only reads. These were
sequential experiments, not controlled proof that every difference was caused by
polling. Later searches with passive encoder telemetry had no observed PC timeout
events, while occasional receiver timeout presentations remained.

### Search and failure reporting

An isolated screening failure is retried once at the same setting, and both
outcomes remain in the report. A repeated failure stops escalation for that
strategy, with a 25 Mbps refinement below a failed 50 Mbps step. If the four leading validation candidates fail, the
lowest measured passing setting for each strategy is also considered with
headroom. Final validation still requires the full configured window with no
observed drops, late frames, timeouts, submit errors or missing history, and both
source and encoder delivery must maintain the configured cadence. A failed
final validation is never converted to a pass. Error text distinguishes the PC
from the Frame and includes counter deltas. Interrupted reports identify the
candidate that was running.

### Corrected sender-mode comparison

The early benchmark changed `asyncSend` and checked the settings readback, but
the FEC constructor at `0x180A48170` reads it only when constructing the connection.
A read-only live check during a trial labeled asynchronous found FEC byte +16
still false. The apparent comparison was invalid. The automatic within-session
comparison has been removed; the current driver observer verifies its exact
file hash, live consumer bytes, RTTI, owner links and stable mode before testing.
The mode stays fixed throughout the run. A requested/active mismatch requires a
reconnect rather than saving an unverified profile. Separate reconnects are
required to compare sender modes.

The earlier 23:47:30 preset labeled asynchronous was invalidated and removed from
the generated preset catalog. Its underlying measured counters are retained in
the report, with an invalidation reason. Its pass must not be presented as proof
of an asynchronous sender test. The later 00:03:13 synchronous fallback's actual
mode was corroborated by the same running sender observation.

One expanded run failed while transitioning to full-resolution multipass:
SteamVR reported `nvEncEncodePicture` error 4, followed by its frame queue failing
to return to the expected state within two seconds. The SDK names status 4
`NV_ENC_ERR_INVALID_DEVICE`. This identifies an encoder/driver failure, not a
wireless capacity failure, and does not establish that full-resolution multipass
is universally unsupported. The run was interrupted and its starting settings
were restored; it saved no preset.

An additional read-only host timing capture during the next search contained
4,576 frames. Transport interval median was 1.896 ms, p99 4.319 ms and maximum
34.522 ms; decoder turnaround median was 6.848 ms and maximum 12.338 ms. This
window crossed 100/150 Mbps configurations. It shows an occasional long transport
tail and does not isolate RF airtime from host queues, repair or receiver assembly.
These overlapping stages must not be added as independent latency measurements.

Receiver timeout presentations are compositor observations, not measured panel
scanouts or decoder-timeout events. See the existing receiver and host maps for
the distinct transport, decode, submission and presentation boundaries.

## Verified-mode live results

The 00:25:11 run verified a synchronous sender before testing and kept it fixed.
It passed a 125 Mbps single-pass screening trial, but its 110 Mbps headroom
candidate incurred one receiver timeout during 63.45 seconds of validation.
The 90 Mbps single-pass fallback passed a full two-minute final validation at
about 58 Mbps of useful video and 90 Hz. It was saved as a personal preset and
the original 400 Mbps / quarter-resolution-multipass settings were restored.
An earlier independently validated synchronous fallback was also clean for
121.66 seconds. These are finite test windows, not a universal wireless ceiling.

A separate 00:32:14 experiment persisted asynchronous sending, restarted SteamVR,
and verified that the actual FEC mode was true before benchmarking. It failed both
100 Mbps single-pass attempts and both 100 Mbps quarter-pass attempts. The short
3.18–3.20 second windows encoded 232.6–291.5 Mbps at only 78.0–84.1 frames/s,
with cumulative PC presentation-drop deltas of 21–40. The controller log still
reported a 100 Mbps request; requested bandwidth is not a measured output cap.
This run cannot certify a stable 100 Mbps preset or attribute all failure solely
to RF loss. No preset was saved. The original settings and actual synchronous
sender mode were restored through another verified reconnect.

A repeat after that reconnect did not reproduce a clean conservative validation.
At 90 Mbps single-pass, it recorded four PC presentation drops and one receiver
timeout after 11.58 seconds (58.69 Mbps measured). The host log at the same time
contains three stream-reset requests, a 43.8 ms bad-link event and a 20.155 ms
`nvEncReconfigureEncoder` warning. These establish a recovery/stall sequence,
not which device or RF event originally triggered it. A clean two-minute window
must therefore not be promoted to a guarantee across restarts or later conditions.

The receiver observer also still performed HTTP discovery and a new Steam UI
debugger connection for every route sample. It now retains that connection,
checks each reply's sequence and closes it with the observer. Both the one-shot
connection query and bounded unattended activity calls use the same protocol
implementation. Trial-boundary PC log snapshots now retain recovery, bad-link,
encoder-warning and compositor-acquire counts, with incomplete coverage marked.
These observations supplement the original counters and never excuse a failure.

The receiver tail also contains substantive packet retry requests and a later
backtrack-window reset/unrecoverable-error sequence. Retry counts are not unique
packet-loss totals, and wall-clock coincidence alone is not frame-ID correlation.
Trial reports now include bounded receiver-log deltas read outside measurement.

A separate 1.5 Mbit VBV/buffer-delay experiment did not establish a higher stable
result. Its 225 and 180 Mbps single-pass, 135 Mbps single-pass, and 90 Mbps
quarter-pass validation candidates failed. Only the 90 Mbps single-pass fallback
completed 120 seconds. Original advanced controls were verified restored. This
experiment does not justify making the smaller buffer a default.

Further observer cleanup removes periodic process creation from the unattended
window: the existing UI connection renews the bounded activity extension, after
an acknowledged handoff from the preflight lease. Cleanup still releases it, and
failure leaves at most 60 seconds. The owned Python observer lowers its own nice
priority and rechecks pinned PID/start-time identities instead of rescanning all
headset processes. No streaming-process priority or radio configuration changes.

## Submission spacing is not a presentation deadline

The 01:08:06 app run used five-minute validation and the new 50 Mbps baseline.
Higher candidates still incurred real receiver timeout presentations, sometimes
with logged retries and sometimes without any selected log event. At 45 Mbps
single-pass, however, the trial stopped after 51.59 seconds solely because a
scene interval crossed 1.75 frame periods. Encoded cadence was 90.00 fps, and
PC drops/timeouts/late counters and receiver drops/timeouts had zero deltas.
Those counters did not establish a missed presentation from the wall-clock gap
between WaitGetPoses/submissions alone. The earlier 41.56 ms maximum in its cumulative
scene telemetry belonged to an earlier part of the run, not this interval.

The evaluator now retains uneven scene spacing as a visible warning, rejects
counter rollback, and explicitly requires source cadence to reach 97% as well
as encoder cadence. Every actual PC/receiver drop, timeout and late event remains
a failure. Regression coverage distinguishes warning-only spacing from both real
receiver timeouts and a slow scene whose encoder repeats at full rate. The old
51.59-second trial remains a failed/incomplete historical run; it is not relabeled
as a five-minute pass or used to create a preset.

## Longer checks and quality-map saturation

The 01:23:26 app run retained strict presentation checks and used five-minute
validation. All candidates failed: the lowest 45 Mbps single-pass fallback ran
250.37 seconds at 38.34 useful Mbps / 90.00 encoded FPS before one receiver
timeout presentation. Its receiver log included one retry request for 43 packets;
the host log had no selected warning. This is not proof that every earlier
timeout was caused by RF loss, and that trial was not saved as a preset.

A separate five-minute SteamVR Home control at the original 400 Mbps request,
1664-pixel width and 90 Hz measured 280.80 useful Mbps / 89.71 encoded FPS,
76 PC presentation drops and 488 receiver timeout presentations. It used the
same persistent observers and unattended activity extension. The issue therefore
also occurred outside the synthetic scene, but the workloads are not identical.

The subsequent 1344-pixel experiment did not isolate decoder pixel capacity:
its 50 Mbps trials still emitted 319–361 Mbps with PC drops. Width and original
controls were restored. A diagnostic at the original 1664-pixel width then
compared the aggressive -20/-12 quality map with the boost disabled. Four endpoint
status reads were used in each run; these were budget diagnostics, not benchmark
stability passes. Counter windows between the first and last reads were:

| Quality request | Seconds | Requested Mbps | Actual video Mbps | Encoded FPS | Sampled accepted average budget |
| --- | ---: | ---: | ---: | ---: | --- |
| Gaze -20, periphery -12 | 15.136 | 50 | 403.043 | 52.392 | 1.512–3.274 Mbps |
| Override disabled | 15.176 | 50 | 33.776 | 89.942 | 50 Mbps |

Returned whole-frame QP stayed 39 in the first run and ranged 44–47 in the
second. The helper's map counters confirmed disabled modification in the second
run. The first run's low accepted budget shows controller backoff; it was not a
stale 400-Mbps request. Strong negative offsets can set a picture-complexity floor
that rate control cannot compensate for. This diagnoses a concrete overshoot
mechanism, not every receiver timeout and not a matched perceptual quality result.
Original quality offsets were restored after both diagnostics.

Auto tune now offers an explicit quality fallback after the starting map fails:
gentler negative offsets, then no negative boost, preserving positive offsets.
Each map has an independent search and full validation; a current-quality pass
ends the search. Resolution, active Hz and bit depth remain fixed. Trial/preset
labels disclose the tested map, and before-write recovery also owns quality.
The default final window is five minutes because earlier two-minute passes did
not consistently survive longer measurements. No finite pass guarantees later
wireless conditions.

## Fresh-session and quiet low-rate controls

The 02:06:02 run exercised all three quality strategies through the actual app,
including five-minute validation. It saved no preset. With Valve's map, a 225 Mbps
single-pass screen delivered 114.67 Mbps for 26.98 seconds, but lower final
candidates still failed. A 135 Mbps single-pass candidate lasted 213.62 seconds
before one receiver timeout; a 45 Mbps quarter-pass candidate lasted 155.23 seconds
before two PC dropped presentations. Short clean screens were not promoted to passes.

After a verified SteamVR reconnect, the 02:35:20 baseline search removed negative
quality offsets and capped requests at 100 Mbps. Its final 45 Mbps quarter-pass
candidate delivered 31.48 Mbps for 110.37 seconds before one receiver timeout,
with one logged retry request for eleven packets. A separate 45 Mbps single-pass
candidate failed after 37.50 seconds with two PC dropped presentations; the same
window contained three recovery requests, one bad-link warning, a backtrack reset
and an unrecoverable receive error. These are transport recovery observations,
not proof that RF alone caused the delays. Original quality and controls were restored.

A subsequent endpoint-only control retained the moving scene, 1664-pixel geometry,
90 Hz, 45 Mbps single-pass and Valve's quality map. It removed all periodic PC,
encoder, receiver, route and settings queries. The headset activity extension was
set once to a bounded 360 seconds, with explicit release afterward. Across the
300-second remote interval (299.968 seconds between host encoder observations),
it measured 37.84 Mbps, 89.97 encoded FPS, four PC dropped presentations, one PC
timeout event and three receiver timeout presentations. No receiver drop was
counted. Thus regular monitoring is not necessary to reproduce the problem.
This endpoint-only diagnostic lacks full PC timing history and is not an auto-tune
validation pass. Original controls and power settings were restored.

The following read-only dongle samples remained around -68 dBm with transmit
2SS MCS6/MCS7 and receive HE_NSS2_MCS6. Windows' 2.4 Gbps adapter link-speed label
is not useful-video throughput. A targeted adapter restart was denied by Windows
for insufficient privileges; it did not reset the adapter. The user subsequently
confirmed the Frame was already close with a clear view, in the normal play area.
Placement therefore is not a pending prerequisite, and -68 dBm alone does not
establish a placement fault. No new reliable preset or 400 Mbps result was certified.

## Same-content timing capture after placement confirmation

The 08:30:56 diagnostic used 100 Mbps, single-pass, synchronous sending and Valve's
quality map at the original 1664-pixel width / 90 Hz. It retained 16,687 AFTM
records plus PC/receiver counters over approximately three minutes. Useful video
averaged 73.60 Mbps. The PC recorded one timeout-state entry without a counted
drop; the Frame recorded nine timeout presentations. These are different counters.

Encode-output completion median/p99/max were 4.898/6.480/69.640 ms; transport
completion was 2.014/4.819/34.785 ms; receiver-clock decoder turnaround was
7.208/7.963/15.730 ms. A host warning independently records GetEncodedPacket
taking 69.631 ms. The interval containing two receiver timeout presentations
overlapped this long encoder-output completion. Other failure cohorts had
33–35 ms transport tails with ordinary encoder completion. Later recovery
coincided with four timeout presentations and scene-spacing warnings.

These are host-clock observation cohorts with a one-second allowance for cached
receiver counters, not exact attribution of each missed presentation. Transport
completion includes assembly/repair and protocol clock-conversion uncertainty;
the encoder-output interval includes waiting/copy/unmap and scheduling, not just
GPU hardware execution. The failures cannot all be assigned to one radio ceiling.

The earlier asynchronous-sender comparison retained aggressive quality offsets.
Because those offsets later showed a rate-control floor, a new exact-mode-verified
asynchronous comparison started with Valve's map. Its restoration journals owned
both the temporary map and connection-scoped sender request. The 08:35:01 run
failed final validation: 110 Mbps quarter-pass failed after 39.66 seconds and
110 Mbps single-pass after 7.28 seconds, each with one Frame timeout presentation.
At 90 Mbps, quarter-pass failed after 197.00 seconds with one timeout and
single-pass after 58.40 seconds with six. The 45 Mbps quarter-pass fallback failed
the encoded-cadence guard after 80.30 seconds; its single-pass counterpart also
failed. No preset was saved. Both original quality and the actual synchronous
sender were restored and verified after reconnecting. Async sending alone did
not resolve these failures.

The PC observer now separately captures `m_nNumTimedOut` at native offset 44
(timeout-state entries) and `m_nNumFramePresentsTimedOut` at offset 48 (presentations
while in that state). Reports retain both; neither is silently substituted for
the other. The evaluator still rejects a new timeout-state entry and additionally
rejects a new timed-out presentation within an already entered state. Regression
checks cover independent increments and counter rollback.

## Router association comparison and practical scoring

The 08:51:23 control temporarily disconnected only the Frame's router association
(`wlan0`); the dedicated `wlanap` route stayed active. A detached, bounded watchdog
owned restoration. At 100 Mbps/single-pass/Valve quality map, 1664 pixels and 90 Hz,
180.32 seconds yielded 57.06 Mbps and 90.00 encoded FPS, with zero new PC drops,
timeout entries, timeout presentations, late frames or history gaps, and zero
receiver drops/timeouts. Six compositor handoff-warning log lines were retained;
they were not counted as dropped frames. Router association restoration was verified.

The matched 08:55:18 control with the router connected also had zero increments
in those presentation counters across 180.75 seconds, at 60.03 Mbps / 89.98 FPS.
Neither control logged receiver repairs. These finite windows do not demonstrate
that router disconnection is necessary or sufficient; no such option was shipped.

The user subsequently clarified that the tuner should optimize relative quality
before major problems, tolerate occasional hitches, and fall back to Valve defaults
instead of returning nothing. Version 2 evaluation follows that goal: complete
performance windows, explicit scored delivery estimates, repeated low-load controls,
and equal-duration comparisons with Valve's encoding policies at fixed geometry.
Invalid measurements remain unusable as evidence but still lead to the default
recommendation. Earlier strict failures remain historical; they are not relabeled
as newly validated results. See [the current tuning policy](../auto-tune.md).

## Mode-transition interruption and progress (October 15, 2026)

The 12:15:26 comparison completed nine mode/pass screens and stopped while moving
from 1280 pixels / 120 Hz / quarter multipass to 2048 pixels / 90 Hz / quarter
multipass. The recorded exception was `RuntimeError: One wireless route required`
during headset counter startup, before the next measurement window. The PC driver
also logged repeated unacknowledged-frame resets around 12:19:04–09. This was a
stream-transition/measurement startup failure, not rejection for an isolated hitch.
The old generic message cannot distinguish missing route information from zero or
multiple active transports. No precise lower-level cause is inferred from it.

The old observer allowed only eight seconds for startup and had no reconnect retry.
It now allows twenty seconds on the receiver, bounded by thirty seconds on the PC,
and requires three consistent receiver/scene/route observations before publishing.
Missing information or no active wireless route has a specific transient exception;
multiple or wired transports retain distinct non-retryable errors. A transient
startup failure resumes the same captured PC once and starts a fresh monitor.
After sampling begins, identity changes and stale counters remain measurement errors.

Hz changes no longer unconditionally restart SteamVR. The Frame's actual rate is
verified first, then the tuner waits briefly for the PC rate and accepted width.
If both agree it recreates only the test scene and observers. Multipass changes,
confirmation trials and mode updates that fail verification still use a fresh session.

Structured progress records mode counts, bounded adaptive work estimates, actual
measurement seconds, completed tests, elapsed time and restoration state. Reconnect
waits do not advance measured time. Partial runs never display 100% completion.
The UI distinguishes preparation, mode screens, choice refinement and restoration.

The 12:34:02 live verification completed all eighteen mode screens. The six
single-pass screens shared one encoder generation, the six quarter-pass screens
shared a second, and the six full-pass screens shared a third. Thus resolution
and both directions of Hz switching completed without full SteamVR restarts;
pass transitions still restarted. For example, single-pass 1280/120 and 1664/120
measured 119.94 and 119.98 encoded fps, with receiver submit rates near 120.

That run exposed a separate closing-sample defect: the final encoder sample could
arrive 0.41–0.43 seconds after the previous one, before the scene's half-second
telemetry update. The copied scene timestamp then produced `sceneDt == 0` and an
invalid-counter result despite advancing encoder and receiver counters. This
affected one mode screen and two early confirmations. The closing measurement now
waits for fresh scene telemetry as well as fresh headset counters; real resets and
missing telemetry still fail. Historical invalid observations are left unchanged.

The 12:34:02 run completed in 13:08 with 29 trials, three saved choices, no terminal
error and 100% progress after restoration. Its balanced choice was a confirmed
2048-pixel / 90 Hz / single-pass configuration with a 200 Mbps target, 123.89 actual
Mbps, 89.94 encoded fps and a 0.03% affected-frame estimate over about 37 seconds.
Other choices retain their own confidence/issue labels; this is not a global or
long-term maximum claim. Readback confirmed restoration of HEVC Main10, 1664 pixels,
90 Hz, 400 Mbps manual target, quarter multipass, synchronous sending, gaze -20 and
periphery -12, with no pending recovery journal.

Final-build verification at 12:50:13 held the original 1664-pixel / 90 Hz mode and
compared all three passes, quality and bitrate. All seven trials were valid, including
the 36.81-second confirmation; every closing scene timestamp advanced. It finished
in 3:29 at 100%, saved a choice and restored the starting configuration without a
recovery journal. This targeted run verifies the closing-sample fix and UI completion,
not a repeat of the full display-mode search. The final build passed 106 managed tests,
six Python route tests and syntax checks of both composed receiver scripts.
