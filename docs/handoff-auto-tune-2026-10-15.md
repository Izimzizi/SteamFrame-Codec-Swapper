# Handoff: SteamFrame Advanced Options auto tune and presentation timeouts

> Historical snapshot. Later on October 15 the user explicitly replaced the
> zero-hitch/five-minute acceptance goal below with a practical comparison:
> tolerate isolated hitches, find a quality improvement before major problems,
> and offer explicit choices even when none are flawless. A subsequent instruction
> replaced the Valve-default fallback with concrete, labeled starting options. Neither
> 400 Mbps nor a flawless run is required. The current implementation and policy
> are documented in [auto-tune.md](auto-tune.md); do not reinstate the historical
> strict acceptance rules from this handoff.

Snapshot: **October 15, 2026, 08:49 EDT (America/New_York)**.

This document records the parent investigation for a future session. Writing it
did not resume that investigation, launch tests, restart devices, change stream
settings, or commit/push anything. Existing background work continued independently.
Live state can change after this snapshot; inspect it before taking action.

## Status and objective

The user wants auto tune to find the highest reliably usable wireless VR preset
for their setup, with a moving scene displayed in the headset while it is not
being worn. It must use and verify the actual refresh rate, detect real
presentation failures, restore starting settings, and save only a validated
preset. The immediate complaint is repeated presentation timeouts and the message
`No candidate passed final validation`.

**The task is not complete.** Measurement defects and several benchmark behaviors
have been corrected, but genuine PC and Frame presentation failures remain.
Recent five-minute validation attempts have not certified a stable preset, and
smooth sustained 400+ Mbps has not been established. Earlier two-minute passes
did not consistently reproduce. Do not describe them as a reliable maximum.

The user confirmed that the Frame is already close to the dongle with a clear
view, in the normal play area. Placement is not an unanswered prerequisite.
They want unattended testing and have previously said to test things ourselves.

## Repository and workspace

| Item | Snapshot |
| --- | --- |
| Workspace | `E:\Github\SteamLinkVRSettings-SteamFrame` |
| Remote | `https://github.com/elliotttate/SteamFrame-Advanced-Options.git` |
| Branch / HEAD | `main` / `c1e173e` |
| HEAD subject | `Release v1.6.0 with Frame reconnect and tuned HEVC presets` |
| Working tree | Many modified and untracked files; preserve all existing work |
| Last known release | v1.6.0; remote release/visibility state was not rechecked for this handoff |
| Last app build location | `artifacts\BenchmarkApp\SteamLinkVRSettings.exe` |
| Local reports | `%LOCALAPPDATA%\SteamLinkVRSettings\Benchmarks` |

The desktop latest-build shortcut previously resolved to the BenchmarkApp output.
The app was closed for isolated CLI measurements. Neither current app process
state nor shortcut resolution was reverified here.

No commit, push, or release has been made for the current auto-tune investigation.
Do not reset the working tree or assume untracked source is disposable.
`artifacts/BenchmarkLab/Program.cs` is Git-ignored: a clean checkout will not
contain the local diagnostic harness or its evidence. Preserve necessary evidence
separately before cleanup or transferring to another machine.

## First check: completion of the existing restoration

The latest experiment ran the real BenchmarkRunner with a verified asynchronous
sender and Valve's unmodified quality map. Its launch command was:

```powershell
dotnet run --project artifacts/BenchmarkLab/BenchmarkLab.csproj -c Release --no-build -- run-send --neutral-quality *> artifacts/BenchmarkLab/send-neutral-search.log
```

This is a record of the existing command, **not a command to start again**. The
parent tool session identifier was `40338`; it may not be accessible in another
session. Do not start a second benchmark while the first owner is still active.

At 08:49:10 EDT, the saved report said **No passing setting**, with 16 completed
trials, no error, no winner, and no preset. The log had reached the outer
restoration reconnect:

```text
Restarting SteamVR once to reconnect the Frame. A running VR game may close…
Waiting for the new SteamVR streaming driver…
Restoring applied encoder tuning and waiting for fresh video…
```

The final `Original settings and actual sender mode restored.` message had **not
yet been observed**. All three journals below were absent at this snapshot;
that alone does not prove the final reconnect and active-mode verification
completed. Check the existing log/process and read back actual state before a
new experiment.

| Resource | Path |
| --- | --- |
| Existing run log | `artifacts\BenchmarkLab\send-neutral-search.log` |
| Run report | `%LOCALAPPDATA%\SteamLinkVRSettings\Benchmarks\benchmark-20261015-083501-28743550.json` |
| Outer sender journal | `artifacts\BenchmarkLab\send-mode-recovery.json` |
| Outer quality journal | `artifacts\BenchmarkLab\send-quality-recovery.json` |
| Inner benchmark journal | `%LOCALAPPDATA%\SteamLinkVRSettings\benchmark-recovery.json` |

The harness first enables/persists asynchronous sending, reconnects, and verifies
the actual FEC object. It then disables the negative quality boost and runs the
benchmark. Its `finally` restores quality, restores original stream settings,
reconnects, and verifies the original actual sender mode. The inner report's
“starting settings restored” refers to the benchmark's own lease; it does not
by itself certify this outer restoration.

Do not blindly delete recovery journals or overwrite later user edits. Read the
lease ownership/recovery logic if restoration is needed.

## Original configuration to preserve

The last verified pre-experiment settings were:

| Setting | Value |
| --- | --- |
| Codec | HEVC Main10 |
| Actual refresh | 90 Hz |
| Stream width / encoded geometry | 1664 / 1664 × 6656 |
| Manual requested target | 400 Mbps |
| Encoder preset | P2 |
| Rate control | CBR without filler |
| Multipass | Quarter resolution |
| Actual sender mode | Synchronous (`asyncSend=false`) |
| Quality override | Enabled, gaze −20 / periphery −12 |
| Advanced encoding overrides | Original/default/null values; use journal/readback rather than inventing explicit defaults |
| Connection | Dedicated wireless dongle, USB 3 |

These are restoration values, **not a newly certified stable preset**. During the
last test the temporary values differed, particularly sender mode and quality.

The paired headset diagnostic endpoint is `10.35.78.1`, user `steamos`. Existing
trusted SSH uses `%LOCALAPPDATA%\steamos-devkit\devkit_rsa` and the app's
`%LOCALAPPDATA%\SteamLinkVRSettings\ssh-known-hosts`. Use the existing access
mechanism; do not print or copy passwords/private keys into reports.

## What is established

### 1. Timing-history ordering was wrong

Two consecutive frame IDs had slightly reversed predicted-vsync timestamps.
Sorting/deduplicating by those timestamps invented missing-history gaps. The
observer now follows unsigned frame IDs, handles wraparound, and ignores already
processed entries. Regression coverage includes reversed/equal timestamps.

One affected historical frame also had a genuine drop. Fixing the false gaps
does not make that entire historical interval clean.

### 2. Strong negative quality offsets can defeat the requested budget

A matched diagnostic at the original width and 90 Hz showed:

| Quality configuration | Request | Actual useful video | Encoded cadence | Window |
| --- | ---: | ---: | ---: | ---: |
| Gaze −20 / periphery −12 enabled | 50 Mbps | 403.04 Mbps | 52.39 fps | 15.14 s |
| Override disabled; Valve map | 50 Mbps | 33.78 Mbps | 89.94 fps | 15.18 s |

In the first case, sampled accepted encoder budgets had already backed down to
1.51–3.27 Mbps. This was not merely a stale 400 Mbps request. Returned whole-frame
QP stayed at 39, versus 44–47 with the override disabled. The helper confirmed
map modification was disabled in the second diagnostic.

This establishes a concrete overshoot mechanism: strong negative offsets can
keep difficult pictures from shrinking sufficiently. A CBR request is not proof
of constant delivered video or a hard output cap. These short windows establish
neither stability nor matched perceptual quality. Original values were restored.

### 3. `asyncSend` is connection-scoped

The FEC constructor at `0x180A48170` reads the setting when constructing the
connection. Changing the saved/live setting without reconnecting did not switch
the running sender. An earlier trial labeled asynchronous was actually synchronous.

The new observer checks the exact driver hash, relevant live instruction bytes,
RTTI, owner links, and stable actual mode. The mode stays fixed within a benchmark.
A mismatch requires reconnecting. Preset application verifies the mode after a
required reconnect. Do not use settings readback alone as evidence of active mode.

The old 23:47:30 asynchronous-labeled generated preset was invalidated/removed;
its underlying measurements remain as historical evidence, not an async comparison.

### 4. Monitoring overhead was reduced, but is not the whole explanation

The benchmark now retains PC/headset OpenVR connections, the headset SSH/Python
observer and Steam UI debugger connection. Native encoder telemetry is a passive
144-byte versioned sequence-checked buffer published by the existing completion
hook; each sample no longer creates a diagnostic thread or status file.

PC timing history is polled every 250 ms because this runtime returns 128 entries.
The observer checks freshness, process identities, generation/configuration and
path stability. Its own Python priority is reduced; it does not change stream
process priority. The scene uses the scoped Games multimedia scheduling class.

A five-minute endpoint-only control, with no periodic PC/encoder/receiver/route/
settings queries, still produced four PC drops, one PC timeout-state entry and
three Frame timeout presentations. Regular monitoring is therefore not required
to reproduce the problem. That control lacks complete PC history and is not a
valid auto-tune pass.

### 5. There are multiple distinct delay sources

The approximately three-minute 08:30 timing diagnostic used 100 Mbps, single pass,
synchronous sending, Valve quality, original width and 90 Hz. It measured
73.60 useful Mbps, one PC timeout-state entry, no counted PC drops and nine Frame
timeout presentations. Its 16,687 captured AFTM records include overlap outside
the exact counter window.

| Observed interval | Median | p99 | Maximum |
| --- | ---: | ---: | ---: |
| Encoder-output completion | 4.898 ms | 6.480 ms | 69.640 ms |
| Transport completion | 2.014 ms | 4.819 ms | 34.785 ms |
| Receiver-clock decoder turnaround | 7.208 ms | 7.963 ms | 15.730 ms |

A host warning independently logged `GetEncodedPacket` taking 69.631 ms. Some
failure cohorts had long encoder completion, others had 33–35 ms transport tails,
and another overlapped stream recovery. One PC timeout-state entry had no
corresponding Frame timeout or obvious captured delay.

These are host-clock observation cohorts with a one-second allowance for cached
receiver samples, **not exact attribution of each failed presentation**. Transport
includes assembly/repair and protocol clock conversion uncertainty. Encoder-output
time includes waiting/copy/unmap/scheduling, not just GPU execution. Do not add
overlapping stages as independent latency or assign all failures to the radio.

## Latest asynchronous comparison: completed trial results

The sender was verified truly asynchronous after reconnecting. Width 1664,
90 Hz and bit depth stayed fixed; negative quality boost was disabled. The run
used 25-second screening and 300-second final validation, with a 450 Mbps ceiling.

Single-pass screens at 50/100/125 Mbps passed short windows; actual output was
41.35/74.43/89.14 Mbps. Quarter-resolution screens at the same requests also
passed, producing 34.48/81.13/105.90 Mbps. Both attempts at 150 Mbps failed for
each strategy with Frame timeout presentations.

All six final candidates failed:

| Request / multipass | Measured seconds | Actual Mbps | Failure |
| --- | ---: | ---: | --- |
| 110 / quarter | 39.66 | 90.93 | 1 Frame timeout presentation |
| 110 / single | 7.28 | 79.47 | 1 Frame timeout presentation |
| 90 / quarter | 197.00 | 71.41 | 1 Frame timeout presentation |
| 90 / single | 58.39 | 67.82 | 6 Frame timeout presentations |
| 45 / quarter | 80.26 | 29.30 | Encoded cadence below 97% |
| 45 / single | 84.39 | 37.63 | 1 PC dropped presentation |

No preset was saved. This comparison does not support making asynchronous sending
the fix or treating 400 Mbps as established. Final outer restoration verification
was still pending at the snapshot described above.

## Benchmark behavior now implemented

- Starts at 50 Mbps, ramps in 50 Mbps steps, and refines a passing/failing bracket
  with a 25 Mbps midpoint. A failed screen is repeated once; both results remain.
- Compares single and quarter multipass by default; full-resolution is optional.
  A prior full-pass transition produced NVENC status 4 (`INVALID_DEVICE`) and
  queue recovery failure. This is an encoder/driver failure, not an RF ceiling
  or proof full pass never works.
- Ranks measured useful video within a quality strategy. Within 5%, prefers a
  lower requested target and then lighter multipass. Stops at a passing quality
  level rather than ranking throughput across unlike quality maps.
- Applies headroom (default 10%, editable 5–25%) and validates that exact reduced
  setting for 300 seconds by default. Tries the top four candidates and the
  lowest passing setting per strategy if needed.
- Searches starting quality first. With the enabled-by-default quality fallback,
  then tries gentler negative offsets (no stronger than −8 gaze / −4 periphery),
  then removes negative boosts while preserving positive offsets. Labels disclose
  the map, and resolution, active Hz and bit depth remain fixed.
- Requires source/encoded cadence at least 97% and headset submission cadence
  within ±3% of the active Hz. Any observed actual drop, late presentation,
  timeout, submit error, invalid/reset/stale measurement or missing PC history
  still fails. Test-scene GPU p99 must stay below 80% of the frame budget.
- Treats scene spacing over 1.75 frame periods as a warning when presentation and
  cadence are otherwise clean. Submission spacing alone is not a presentation
  deadline; it no longer invents a failure from that signal alone.
- Keeps the unattended scene active through bounded 60-second activity requests,
  renewed every 20 seconds on the existing connection and explicitly released.
- Journals original/expected/previous values before writes, including quality.
  Restoration preserves differing newer edits; old journals do not acquire
  ownership of fields they never recorded.
- Reports PC versus Frame failures and bounded host/receiver log evidence.
  Missing log coverage is explicit. Retry requests are not unique lost-packet
  totals, and logs never replace actual presentation counters.

## Latest code edits still need validation

The last source change separates two native PC compositor counters:

- Offset 44, `m_nNumTimedOut`: entries into timeout state.
- Offset 48, `m_nNumFramePresentsTimedOut`: presentations made during that state.

Previously the PC benchmark only read the first; Frame monitoring already counted
timeout presentations. The new code retains the strict PC timeout-entry check
and adds separate presentation-delta checking. It does **not** declare timeout
entries harmless or weaken failure criteria.

Affected source and tests:

| File | Change |
| --- | --- |
| `SteamLinkVRSettings/Frame/CompositorTiming.cs` | Reads/validates offset 48, accumulates separate presentations, handles rollback, retains events when only that count advances |
| `SteamLinkVRSettings/Frame/WirelessBenchmark.cs` | Nullable `PcTimeoutPresentations` for older-report compatibility; rejects increments, reset and changing measurement availability |
| `SteamLinkVRSettings/Frame/BenchmarkRunner.cs` | Copies the new PC counter into samples |
| `Tests/CompositorTimingTests/Program.cs` | Timeout entry without presentation, presentation advance within existing state, rollback coverage |
| `Tests/BenchmarkTests/BenchmarkTests.cs` | Presentation increment within existing timeout state and rollback coverage |

These changes were present in source at handoff, but **had not been built or
tested**. The CLI run above used the earlier binary. Do not interpret missing
`PcTimeoutPresentations` in older JSON as zero.

Prior parent-reported verification, before these edits: 74 Benchmark tests,
27 Compositor assertions, 95 Core tests and seven native tests passed; the app
Release build succeeded with existing MVVMTK0045 warnings. Those are historical
results, not verification of the latest working tree. This handoff ran no tests.

After the existing run is fully restored and when resuming implementation,
targeted commands are:

```powershell
dotnet test Tests/BenchmarkTests/BenchmarkTests.csproj -c Release --nologo
dotnet run --project Tests/CompositorTimingTests/CompositorTimingTests.csproj -c Release
```

For full application validation, `Build.ps1` builds native code, uses Visual
Studio MSBuild for WinUI XAML, and runs the configured test suites:

```powershell
.\Build.ps1 -Configuration Release -OutputDirectory artifacts\BenchmarkApp
```

Check output/process ownership before replacing files in use. A rebuilt helper
may require the existing helper-version/reconnect flow. Do not assume a running
SteamVR process loaded the newly built helper merely because the file changed.

## Evidence and source index

Start with these repository documents:

- `docs/auto-tune.md`: user-facing behavior, options, passing criteria and recovery.
- `docs/research/auto-tune-presentation-diagnostics.md`: investigation narrative.
  Its last paragraph still says the latest async comparison is pending; this
  handoff has the newer completed trial results.
- `docs/research/host-stream-map.md`: encoder budget, admission, FEC, timing and
  exact-build host evidence.
- `docs/research/receiver-stream-map.md`: repair, decoder, compositor, timing,
  multidecoder measurements and remaining boundaries.
- `docs/research/radio-stream-map.md`: USB, radio, scheduling and observability.

Primary implementation:

- `Native/benchmark_scene.cpp`, `Native/probe_telemetry.h`, `Native/encoder_probe.cpp`.
- `SteamLinkVRSettings/Frame/BenchmarkRunner.cs`, `WirelessBenchmark.cs`,
  `BenchmarkSettingsLease.cs`, `BenchmarkReceiverMonitor.cs`, `BenchmarkDevices.cs`.
- `SteamLinkVRSettings/Frame/EncoderProbe.Telemetry.cs`, `EncoderProbe.SendMode.cs`,
  `CompositorTiming.cs`, `FrameDisplayRate.cs`, `FrameSsh.cs`.
- `SteamLinkVRSettings/Views/FramePage.Benchmark.cs` and `.xaml` for benchmark UI.
- `BitrateSetup.cs`, `Views/FramePage.Setup.cs` and display-rate code are also
  part of the dirty working tree for earlier onboarding/Hz work; do not discard
  them or infer that this handoff independently verified their full behavior.

Key local evidence (not all present in Git):

| Artifact | Use |
| --- | --- |
| `Benchmarks\benchmark-20261015-020602-9d3f6553.json` under app local data | Full three-quality search, 34 trials, no winner; 135 Mbps single failed at 213.62 s with one Frame timeout |
| `Benchmarks\benchmark-20261015-023520-6867d424.json` under app local data | Fresh-session neutral map, ceiling 100; nine trials, no winner; low-rate repair/recovery failures |
| `Benchmarks\benchmark-20261015-083501-28743550.json` under app local data | Latest verified async/neutral comparison; 16 trials, no winner |
| `artifacts\BenchmarkLab\quiet-control.json` | Endpoint-only five-minute control: 37.84 Mbps / 89.97 fps, genuine failures remain |
| `artifacts\BenchmarkLab\home-control.json` | SteamVR Home at original 400 target: 280.80 Mbps / 89.71 fps, 76 PC drops / 488 Frame timeout presentations over about five minutes; different workload |
| `artifacts\BenchmarkLab\timing-control-20261015-083056.json` | Three-minute same-content counter capture |
| `artifacts\BenchmarkLab\timing-control-20261015-083056-frames.json` | Raw AFTM timing records |
| `artifacts\BenchmarkLab\timing-control-20261015-083056-analysis.json` | Timing cohorts and tail statistics |
| `artifacts\BenchmarkLab\capture_timing.py`, `analyze_timing_control.py` | Read-only capture and offline analysis |
| `artifacts\BenchmarkLab\Program.cs` | Local harness; inspect ownership/restoration before using modes |
| `artifacts\RadioTrace\read_wifi_sdk.py` | Guarded read-only dongle SDK observation |

Last recorded helper SHA-256:
`8f74206caeed4138d730f0df73476cc37ba46354ec758a085588992716bf7705`.
Last recorded patched-driver SHA-256:
`34265101d56235e2f87e5b0e2f3fc2340ed1513a9b6d26f03c4fe12bafcbbc03`.
These are prior observations, not hashes remeasured during this handoff.

## Hardware observations and unproven hypotheses

The dongle was identified as Wi-Fi 2, Realtek 8832CU Wireless LAN WiFi 6 USB NIC
For Valve, GUID `DFC418A5-58BF-48F2-8A38-921A1E859406`, PnP ID
`USB\VID_28DE&PID_2432\00e04c000001`. Prior guarded samples were around −68 dBm,
TX 2SS MCS6/MCS7, RX HE_NSS2_MCS6. Windows' 2.4 Gbps adapter label is not useful
video throughput. The PC's default internet route was Ethernet.

A targeted adapter restart previously returned access denied: **no reset
occurred**. An unsupported power-management query is not evidence of a hardware
fault. The user has previously recovered a stuck link by unplugging/replugging
the dongle, even on the same USB 3 hub. No physical replug was performed for this
handoff, and placement should not be blamed again without new evidence.

Earlier reverse engineering identified receiver retry arithmetic/capacity
problems and a possible reused-slot initialization race. Guarded temporary
canaries did not eliminate all failures. No permanent new receiver/radio firmware
patch is established as the solution. Consult the maps for confidence and exact
build boundaries; reverse engineering does not give complete control of every
firmware/scheduling decision.

Router-Wi-Fi coexistence was only a proposed next discriminator in the latest
parent turn. `artifacts/RadioTrace/wifi_coexistence_trial.py` exists from prior
work, but it was not run in that turn. Do not assume `wlan0` state, disconnect it
without checking the active path, or reuse a temporary-disconnect experiment
without bounded restoration. Scene projection caching, encoder P1/HAGS and
buffering changes were also ideas, not implemented fixes.

## Recommended continuation

1. Confirm the existing harness exited and completed the outer reconnect; verify
   original quality, controls and actual synchronous sender mode. Preserve any
   newer user changes. Inspect the exact run report rather than trusting a stale
   progress message or missing journal alone.
2. Validate the new timeout-presentation source/tests, then rebuild the app/CLI
   only when they are no longer in use. Keep timeout-state entries and timeout
   presentations separately visible; do not silently relax pass criteria.
3. Update the investigation narrative with the completed async comparison. It
   failed even with the quality confound removed, so avoid repeating that search
   unchanged or presenting async sending as the answer.
4. Use low-overhead, matched captures to distinguish encoder-output scheduling
   stalls, transport/repair tails and compositor deadline behavior. Correlate
   frame IDs and properly bounded clocks where possible. A coarse cohort is a
   lead, not causal proof. Address failures at low actual bitrate before treating
   increased targets as a solution.
5. Run controlled changes one at a time with verified active state and restoration.
   Keep content, Hz, geometry and quality fixed when comparing transport or
   scheduling. A coexistence comparison remains a hypothesis to test, not a fix.
6. After a concrete improvement, run full five-minute validation and a fresh-session
   repeat. Save a preset only from the actual passing configuration, disclose any
   quality reduction, and follow with gameplay at the same Hz. Report actual
   useful video separately from target, repair/parity and adapter traffic.

Completion means the observed failure mechanism is addressed with supporting
evidence, the application finds and saves a genuinely passing preset at the
verified refresh rate, and the starting configuration restores correctly. If
the setup still fails, retain the failure evidence and describe the remaining
boundary precisely; do not manufacture a successful preset.
