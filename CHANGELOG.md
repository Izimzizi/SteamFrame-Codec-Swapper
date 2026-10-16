# Changelog

## 1.8.2 — Driver compatibility and a lower-latency HEVC option

- Added an optional Lower-latency HEVC preset: 200 Mbps, single pass and synchronous
  sending, retaining HQ's 2048-pixel width, 90 Hz, 10-bit and gaze offsets. Repeated
  tests measured 25.1 ms versus HQ's 29.6 ms from PC submit to headset decode
  completion on one setup. This is not motion-to-photon; equal image quality is
  unverified. Existing presets and saved settings keep their values.
- Stream and encoder guidance explains the measured width, bitrate and multipass
  costs. Auto tune now explicitly distinguishes its hitch-based smoothness score
  from end-to-end latency and visual quality. The HEVC guide records the 35-run
  investigation, matched controls and measurement limits.
- The HEVC bitrate override now supports public SteamVR build 25330290 alongside
  beta build 25839986 and the earlier verified beta driver. The app selects the
  patch from the installed driver and retains separate original backups when
  switching versions. Compatible unlisted builds still require all layout checks.
- Patches & Recovery shows the installed build and branch, the recognized driver,
  and verified bitrate-patch versions. A pending branch download cannot label the
  installed driver as the requested version, and version numbers never authorize a patch.
- Unsupported drivers, missing originals, and damaged recovery files have distinct
  explanations. Preset errors include the underlying reason. Copy driver details
  collects the build, file hashes, backup status and precise verification failure
  for support reports; a missing backup before first installation is explained.
- Known patched stable drivers remain recognizable even without a receipt or backup;
  restoration still requires the matching verified original.
- Validation: exact install/restore/reinstall through the app on stable and beta;
  regression tests cover switching versions at one path, preserved backups, unknown
  compatible hashes, unsupported layouts, recovery failures and the new preset's
  saved/apply behavior. All 344 managed tests,
  seven native tests and 30 compositor assertions passed. One optional legacy fixture
  test was skipped.
- This release expands the disk bitrate/HEVC-level patch. Codec and custom quality
  compatibility remain separate checks; this is not a new live streaming validation
  of those features on stable SteamVR.

## 1.8.1 — Auto tune recovery and clearer streaming controls

- Auto tune detects stopped video between trials, reconnects the same captured PC with
  at most two recovery attempts per run, and requires fresh output before measuring again.
  Reconnection stops a stale headset streaming client before restarting it, and restores
  the connection before using SteamVR settings APIs. Cleanup errors retain the original cause.
- A brief low-output interval no longer ends a trial after only a few seconds. Completed
  windows still require enough encoded video; invalid counters and changed settings remain errors.
- Wireless advice now requires fresh measurements and avoids recommending more bandwidth
  from an underloaded scene. Current link problems take priority over an older sweep result.
- A measured ceiling must have adequate video output and a completed clean confirmation
  before it can be offered, applied or saved. If confirmation fails, a lower clean candidate
  receives its own confirmation instead of being assumed safe.
- Pending gaze-quality edits survive background polling and navigation. Irrelevant controls
  are disabled, and scrolling no longer accidentally changes dropdown selections.
- Back to stock no longer mistakes an exited SteamVR process for a running driver during
  shutdown. After saving the recovery profile, it disables automatic reapply so a later
  restoration error cannot re-arm the old encoder override on the next restart.
- Compact windows have wrapping action buttons, readable Overview metrics, a more accessible
  live graph and Auto tune start button, and grouped radio/capacity details.
- Live verification completed 64 valid Auto tune trials in 25:05, produced three labeled
  recommendations and restored the starting settings. A separate deliberate stream-drop test
  verified automatic recovery and continued measurement. Short screens retain limited-confidence
  labels, and observed streaming hitches remain visible.
- Live Back to stock verification restored the exact original driver, removed saved tuning
  overrides, and resumed Valve-controlled VBR/single-pass encoding with no pending-policy warning.
- Validation: 334 managed tests, seven native tests and 30 compositor assertions passed.
  One legacy patcher test was skipped because its external driver fixture was not provided.

## 1.8.0 — Headless sweeps and the measured ceiling

- **Keep Frame awake** on the Overview card renews the headset's own 60-second activity
  extension every 20 seconds over the paired SSH link, like Auto tune's unattended mode, so a
  Frame on a desk keeps streaming. The capacity sweep and Restart Frame stream hold the same
  extension while they run; it ends when switched off or when the app closes.
- The sweep's highest clean target is kept across restarts and saved as a personal preset
  ("Measured ceiling N Mbps …") built from the current setup with only the target changed. The
  card compares it with Valve's 200 Mbps automatic starting rate, and **Apply N Mbps now** puts
  the measured ceiling on the running stream in one click with automatic bitrate off.
- The sweep measures a marginal or failed step a second time before it counts (twice per sweep
  at most) and confirms the winning step with another pass before saving it as the measured
  ceiling, so one burst of interference neither ends the sweep early nor becomes the recommended
  rate. A failure that does not repeat counts as marginal: the sweep goes on, but that rate is
  not recommended on its own.
  Two-pass steps are marked in the results and the summary names every marginal target. Each
  sweep replaces the measured-ceiling preset the app saved before instead of adding another.
- The card keeps the last 20 sweep results and, once two fall within a week, says whether they
  agreed within a step or ranged, with advice for a link that varies: settle on the lower value,
  or use automatic bitrate starting at the higher one so SteamVR backs off when the link dips.
  For the first 90 seconds after a sweep the card says the live counters still include the
  sweep's highest steps instead of arguing with the result.
- The sweep's start dialog offers **Apply the measured ceiling when the sweep finishes** (on by
  default, remembered), so one click measures the link and puts the result on the stream. The
  summary now states how much video the winning step actually averaged and calls the result
  lightly tested when that was under 85% of the target, since a static scene never asks the link
  for the full rate.
- Reading steamvr.vrsettings right after a change no longer fails with "being used by another
  process": SteamVR holds the file exclusively for a moment while it saves, and the read now waits
  it out. The sweep's measured-ceiling preset was the first victim.
- The sweep can show Auto tune's moving test scene in the headset while it measures (ticked by
  default when nothing is playing), so a headset on the desk tests the link at the full rate
  instead of a still scene's third of it. Receiver retry requests are now listed per step and
  count against a step only at 60 or more per minute; held frames and the headset loss ratio
  remain the judges, since a full-rate scene logs a few retries per step on a healthy link.
- A sweep that finds no clean step now leads the card (nothing above its best marginal rate is
  worth trying now), retires the saved ceiling instead of leaving an "Apply N Mbps" button for a
  rate the link just refused, and is kept in the history as such.
- **Apply N Mbps now** is no longer a permanent button: it appears for 20 minutes after a sweep
  whose winning step the encoder actually filled, and older or lightly tested ceilings stay in
  the text and the Measured ceiling preset. Lightly tested results are left out of the history
  range and of the Automatic from N Mbps suggestion.
- Before the first step the sweep and the check wait for ten quiet seconds without a stream
  recovery request (up to 40 s), so a stream that has just restarted is not scored as failing.
- **Check this rate (30 s)** measures the live target once, as one sweep step with the same
  rules, re-check and test scene, and reports whether that rate is clean today without changing
  or saving anything.
- **Automatic from N Mbps** appears once the week's clean ceilings disagree by a step or more:
  one click turns automatic bitrate on with SteamVR starting at the highest ceiling measured
  instead of Valve's 200, with the manual target raised so it does not cap the start.

## 1.7.0 — Wireless auto tune, link visibility and guided setup

- Overview's new **Wireless link** card reads SteamVR's own adaptive-bitrate output from the
  driver log: the 30-second link estimate with its automatic/manual flag, the per-frame
  link-capacity histogram (share at or above 350 Mbps, 10th percentile, median, share below
  100 Mbps, drawn on a log scale) and sender throttle events counted as held frames per minute.
- Bad-link events that changed the bitrate are described as such ("SteamVR lowered the stream
  from 200 to 150 Mbps after an 86.7 ms link stall") with holdoff and automatic flags captured.
  The graph's dashed target line follows the driver's latest stated rate once it is newer than
  the last target change, so automatic mode and throttled streams show where the bitrate is.
- Reads the Valve dongle's radio association from the Windows WLAN service with no pairing:
  band, channel, channel width when reported, PHY type and rate, signal, RSSI and retry
  counters when the driver advances them. Weak signal, 2.4 GHz, a narrow channel or a slow PHY
  are highlighted on Overview and Connection.
- A wireless advisor combines these with headset loss and latency, dongle USB speed and the
  driver ceiling into one recommended manual target with reasons and a confidence level.
  **Use N Mbps** loads it into Stream settings with automatic bitrate off; Apply activates it.
- **Find my link's limit** sweeps the manual target in 50 Mbps steps while you play, scores each
  step on held frames, step-downs, headset loss, recovery requests, receiver retries and encoded
  rate, stops after a failure or two marginal steps, reports the highest clean target and restores
  the original target and automatic setting afterwards, on Stop, or when the app closes. The sweep
  and Auto tune refuse to run at the same time.
- **Restart Frame stream** tells the headset's own Steam client to stop its PC VR stream and
  stream again over the paired SSH link, without restarting SteamVR, and waits for the driver to
  report the video link active. The sweep runs it before starting and when a step loses the
  link; Reconnect Frame uses the start half when SteamVR restarts without fresh video. The
  Overview card shows when the video link last became active or inactive.
- Auto tune trials now record held frames and bitrate changes from the driver log; a trial in
  which SteamVR lowered the stream below the candidate's target is a serious issue, and the
  recommendation notes say so.
- The automatic starting bandwidth (`recommendedBandwidthMbit`, Valve's default 200) is edited
  beside the Automatic bitrate toggle, saved in presets and applied with the other stream
  controls instead of under Additional settings.
- Added the built-in **Balanced wireless (stock driver)** preset: a fixed 200 Mbps target,
  90 Hz, 1536-pixel width, 10-bit, dongle-only links and Valve encoder defaults, needing no
  driver patch.
- Auto tune now screens at neutral 0 / 0, then independently compares positive,
  neutral and negative gaze/periphery offsets on shortlisted modes. Starting offsets
  and measured combinations are included; each saved choice retains its tested map.
- Ranking prioritizes the serious-issue threshold and balances smoothness, resolution,
  Hz and regional detail. Higher Mbps alone adds no score. Bitrate probes use available
  whole-frame QP evidence only at otherwise identical settings. Reports show the
  measurements and progress reserves the additional quality comparisons.
- Added mode/test counts, estimated overall progress, current measurement seconds,
  elapsed time and separate preparation/comparison/tuning/restoration stages.
  Interrupted runs retain their actual partial progress and diagnostic reason.
- Waits for headset telemetry to settle after mode changes and retries a transient
  missing wireless route or receiver once against the same PC. Mid-trial measurement
  failures remain visible and are never treated as clean performance.
- Skips unnecessary Hz restarts when the Frame and PC already report the new rate
  and the encoder accepts the requested width. Required pass and confirmation
  restarts now explain their purpose in the progress panel.
- Closing a measurement now waits for fresh test-scene telemetry, preventing a
  repeated half-second scene sample from falsely invalidating an otherwise live trial.
- Fixed Frame Hz switching to use the PC-streaming app's refresh preference,
  which overrides the headset's global setting. Both actual rates are verified.
- Multipass changes now wait for a fresh encoder session after live testing
  exposed a GPU fault during an in-place full-pass transition. Screens are grouped
  by pass to reduce restarts, and receiver failures retain their diagnostic reason.
- Auto tune now returns multiple explicit presets with stream width, Hz, bitrate,
  encoder pass and measured tradeoffs. It no longer gates recommendations on beating
  Valve defaults or substitutes stock settings when the comparison is inconclusive.
- Screens 1280/1664/2048-pixel widths plus the current width, at 90 and 120 Hz.
  Single, quarter and full multipass are compared before refining three leading modes.
- Default screens take 10 seconds and confirmations 35 seconds. Bitrate headroom,
  durations, mode comparisons and serious-issue thresholds are configurable.
- Final confirmations start with a fresh encoder. A poor confirmation can try one
  previously measured alternative; detail and refresh choices prioritize those modes
  among settings below the user's serious-issue threshold.
- Saves each choice separately. Partial runs retain completed measurements; an
  unavailable connection produces an explicit unmeasured starting option.
- Added a separate durable display journal: tests change and verify actual Frame/PC
  Hz, then restore the original active rate and refresh preferences, including recovery
  after interruption. Width changes are verified against the accepted encoder state.
- Unattended mode renders an animated per-eye SteamVR scene with the headset off
  the user's head, using bounded temporary activity/power overrides.
- Scores complete windows, keeping isolated hitches as observations. Strong stalls
  and sustained delivery problems receive greater penalties. Valid bad results remain
  visible; options below the configured serious-issue thresholds are preferred.
- Records actual Hz, affected-frame estimates, burst windows and smoothness scores.
  PC timeout-state entries and timed-out presentations remain separate counters.
  Missing telemetry, session changes, reset counters and encoder errors cannot establish an improvement.
- Restores starting settings using a durable before-write journal that preserves newer
  external edits. Quality alternatives are optional and retained in the saved preset.
- Restarts SteamVR when a benchmark rate-control transition needs a new encoder session,
  resumes the same host, rebuilds observers, and verifies the active settings before scoring.
  Valve's adaptive target updates no longer prevent restoration of the original manual target.
- Fixed the encoder helper retaining CBR during Valve-owned rate-control transitions even
  when the app had never overridden that session's rate control. Default and multipass-only
  requests now preserve Valve's choices; explicit app overrides still use reconnect guards.
- Fixed false timing-history gaps from corrected vsync timestamps, and stopped treating
  uneven scene submission spacing alone as proof of a missed presentation.
- Uses passive encoder metadata and persistent PC/headset OpenVR, SSH and UI observers.
  The owned scene uses multimedia scheduling; the remote observer runs at lower priority.
  Boundary logs retain repair, recovery and encoder warnings separately from frame counters.
- Compares gentler quality offsets on promising modes and stops increasing bitrate
  when its measured tradeoff worsens or crosses the serious-issue threshold.
- Removed invalid live send-mode comparison: `asyncSend` takes effect on reconnect.
  Auto tune checks the running sender and rejects pending setting mismatches.
  Applying a preset on the verified driver reconnects and verifies a changed sender mode.
- HEVC refresh changes now set the Frame's display mode through paired SSH,
  verify its display-frequency property, and confirm the active PC headset rate.
  SteamVR reconnects if needed. Stream settings distinguish selected and active Hz.
- Added a first-run bitrate walkthrough and a way to reopen it in Patches & Recovery.
  It reports verified original, existing patch, missing or unsupported driver states;
  explains preset-installed patches; and installs with backup and verification.
  Patch classification requires an exact original or patched hash.


## 1.6.0 — Frame reconnect and tuned HEVC

- Added **Reconnect Frame** on Overview. It restarts SteamVR once (or starts a
  stopped runtime), restores prior HEVC tuning requests, and checks for fresh
  encoded frames and the headset's active link. It does not restart repeatedly
  or take over experimental paired codec sessions.
- Added dongle unplug/replug guidance beside the button, in recovery messages,
  and in the help. The same port or hub can be used. A restart alone is not
  presented as proof that the headset reconnected.
- Added the editable **Tuned HEVC** preset: 400 Mbps target, 90 Hz, 10-bit,
  1664-pixel width, quarter-resolution multipass, synchronous sending, and
  center/outside-gaze quality offsets of -20/-12. **Smooth HEVC**, **High quality
  HEVC**, and **Full-resolution HEVC — Experimental** remain bundled. Targets
  are requests; steady 400 Mbps gameplay is not established.
- Added clearer requested-versus-accepted encoder policy status, including
  rate-control changes that require a new encoder session. Maximum-QP changes
  now wait for encoder reinitialization instead of claiming a live change.
- Corrected recovery-trace timing labels and distinguished compositor handoff
  warnings from failed shared-texture acquisition.
- Added a source-tree launcher for the latest complete local app build, with
  Windows PowerShell entry-point checks. Added a matching-helper diagnostic
  build workflow so historical research tools do not reintroduce the app's
  helper-version mismatch during normal recovery.

## 1.5.1 — Automatic encoder helper upgrade recovery

- Detect a previous encoder helper still loaded in SteamVR after an app update
  and show a direct Restart SteamVR and resume action.
- Starting monitoring handles this condition by restarting SteamVR once, waiting
  for the replacement streaming driver, loading the updated helper and resuming
  saved encoder tuning when Reapply is enabled. Unapplied edits remain staged.
- Background monitoring never restarts SteamVR; startup has a bounded wait and
  cannot loop through restarts or take over a paired codec session.
- The upgrade message explains that restarting disconnects the headset and may
  close a running game. A helper mismatch no longer suggests administrator access.
- Clear a previous monitoring error once monitoring recovers, including after a
  SteamVR restart performed outside the app.
- A closed codec session no longer blocks monitoring as "reconnecting" when
  Windows reuses its process ID and no codec supervisor holds the owner lock.

## 1.5.0 — SteamFrame Advanced Options and HEVC presets

- Renamed the project and app display name to SteamFrame Advanced Options.
- Bundled Smooth HEVC (300 Mbps, single-pass VBR, original gaze quality) and
  Full-resolution HEVC — Experimental (400 Mbps, full-resolution multipass).
  Both are editable and keep GOP/keyframe-ratio overrides unset.
- Added experimental full-resolution multipass alongside Valve default, single
  pass and quarter-resolution multipass, with analysis/latency guidance.
- Added opt-in GOP bitrate consistency and keyframe I/P bit ratio controls,
  saved in personal presets and reported separately as requested/pending/accepted.
  Both require a new encoder session; the keyframe ratio is intended for CBR with
  single-frame VBV. The measured High quality HEVC preset is unchanged.

## 1.4.1 — Streaming reliability fixes

### H.264 and AV1 sessions
- SteamVR's rate control can lower a codec session's bitrate after bad-link events
  instead of the session pinning the requested rate. The request is now a ceiling.
- H.264 above 232 Mbps is refused on a verified original driver, like HEVC; an
  unrecognized driver produces a warning.
- Returning to HEVC restores the stream settings from before the session (bitrate,
  automatic bandwidth, link joining, stream width), including after an interrupted session.
- Quitting SteamVR restores the Frame and settings without relaunching SteamVR. The
  90-minute limit no longer stops a running game; HEVC is restored when SteamVR exits or
  before the Frame administrator session expires.
- The companion checks SteamVR with Windows process handles instead of starting
  PowerShell about twice a second for the whole session.
- The PC adapter reuses its codec preset instead of querying it on every frame, and
  protects the whole H.264 timing tile.

### HEVC encoder controls
- A rate-control policy that NVENC rejects at initialization falls back to Valve's
  configuration and is not retried on every reconnect.
- Conflicting advanced fields are rejected individually; a VBV-only override clamps
  Valve's initial delay rather than rejecting every advanced setting.

### Presets, Back to stock and Apply
- Back to stock and presets leave tracking, OSC, eye-tracking sharing, NAT, dashboard,
  shutdown and volume preferences alone. Older presets with those values still load.
- Out-of-range overrides from older releases no longer block Back to stock or Save preset.
- Apply validates the whole request before a preset stops SteamVR or changes the patch.
- A codec session that has just closed is no longer treated as live.

### Diagnostics
- Encoder-stall capture recognizes SubmitFrame, reconfigure and submit-to-start
  warnings, and frame-ID stream resets are recorded.
- One failing source no longer stops the whole dashboard refresh or headset capture.
  OpenVR reads stop waiting after three seconds if SteamVR hangs.
- An unreadable codec status or driver backup no longer blocks actions or closing the app
  when no codec service is running.
- Captures repair a record cut short by a crash; a mid-read log error no longer replays
  the log; an empty newest recovery trace keeps the previous summary.

## 1.4.0 — High-quality HEVC and USB connection status

- Added an editable built-in High quality HEVC preset: 400 Mbps target, 90 Hz,
  10-bit, 2048-pixel width, CBR without padding, quarter-resolution multipass,
  asynchronous sending and center/peripheral quality offsets of −2/−6.
- Preserved personal presets separately from built-ins. Added HEVC refresh-rate
  editing and complete-preset application from the Advanced page.
- Fixed NVENC rate-control capability checks and preserved the last accepted
  mode when a runtime cannot change it until encoder reinitialization.
- Added negotiated USB 2/USB 3 dongle status to Connection and Diagnostics,
  including unknown/stale states and USB 2 throughput guidance.
- Improved H.264 high-bitrate bitstream restrictions and entropy selection, and
  preserved encoder initialization diagnostics before codec recovery.
- Made companion heartbeat writes tolerate transient Windows file-reader races.
- Added standalone SteamVR multistream and HEVC decoder/timing research tools.
  The app's normal HEVC/H.264 paths remain single stream. Synthetic 400+ Mbps
  throughput is not a claim of smooth 400+ Mbps gameplay.
- Documented observed HEVC gameplay rates and every preset control. Release
  packages include the high-quality HEVC guide; local captures and personal
  configuration are excluded.

## 1.3.0 — Complete presets, stock recovery and H.264 bitrate controls

- Added complete named presets, stock recovery with previous-tuning capture,
  and unified navigation for stream settings, diagnostics and advanced controls.
- Extended experimental H.264 requests to 25–500 Mbps; AV1 retains 25–200 Mbps.
- Improved monitoring-button stability, display scaling and settings availability
  after codec rollback. Actual bitrate and frame cadence remain workload-dependent.

## 1.2.0 — Experimental AV1 and H.264 sessions

- Added a codec dropdown for HEVC, AV1 and H.264, with a 25–200 Mbps request and
  90/120 Hz selection, private Frame password entry and an explicit HEVC restore action.
- Added an Advanced Supersample Filtering toggle with live SteamVR readback and
  safe saved-setting fallback when SteamVR is stopped.
- Bundled the supervised codec companion, isolated PC runtime, PC NVENC adapter and
  exact-build AV1 decoder with corresponding source. Supported users can apply the
  temporary Frame/client changes through the app rather than building them manually.
- Added verified active-codec readouts from fresh encoder output, including waiting,
  stopped and unknown states. The selected dropdown and old session state are not
  treated as proof of the codec in use.
- Kept the ordinary HEVC monitor hooks separate from the paired AV1/H.264 adapter.
- Guarded headset changes by kernel, module, client and firmware fingerprints, and
  authenticated service ownership by process identity and heartbeat.
- Added automatic stock HEVC restoration on session stop/expiry. Installed Frame
  kernel, firmware, original client and boot module files are retained.
- Documented the supported exact Frame build, SSH pairing, administrator
  authentication, bitrate/FPS limits, H.264's smaller image and experimental status.

## 1.1.0 — Advanced streaming controls

- Added advanced encoder controls for fractional VBR quality target, maximum QP,
  peak bitrate, VBV buffer/delay, chroma offsets, spatial/temporal AQ and P1–P7 presets.
- Added typed driver controls for stream width bounds, foveation mode, pooling,
  asynchronous start/send, 10-bit, sharpening, timed retry, FEC, QoS and backoff recovery.
- Added requested, accepted, pending and rejected encoder readbacks, bit depth,
  whole-frame QP and encoder callback timing to the live dashboard.
- Added live VBR/CBR and encoding-pass experiments, wider gaze-bias controls and
  temporary test power settings with restoration.
- Preserved Valve defaults for unchecked overrides, validated capabilities and
  buffer/width limits, and kept explicit retries separate from monitor polling.
- Improved initialization-only reapplication after SteamVR restart and preserved
  edits made while Apply is running.
- Removed the codec research notice from the app. Portable releases include user
  guides and omit development investigation documents.

## 1.0.0 — Steam Frame edition

First release of the Steam Frame edition.

### Steam Frame tools
- **Stream controls** for the bitrate target, automatic bandwidth, AMD AMF and USB link
  joining, applied live to running SteamVR or saved for its next start.
- **Foveation bias** on top of Valve's gaze-tracked quality map: gaze −12 to +12, periphery −12 to +31.
- **Live dashboard** with actual NVENC encoded bitrate and FPS, the requested target, and
  adapter traffic over 60 seconds.
- **Streaming issue capture** for PC log events, headset receiver faults over paired wireless SSH,
  and PC presentation counters, saved as rotating JSONL.
- **Recovery trace** summaries of per-frame encode, transport and decode timing.
- **Driver patcher** raising the 232/350 Mbps ceilings to 500 Mbps with HEVC level 6.2, using
  adaptive layout discovery, verified backups, receipts and one-click restore.
- **Connection tools** for headset link status over SSH or ADB, and USB connection recovery.

### All streaming settings
- One unreadable value no longer prevents the page from loading; that setting shows its default with a warning.
- Values outside a dropdown's list show as **Custom (value)**.
- Resetting a setting removes its override, so future SteamVR default changes still apply.
- The first save keeps your original `steamvr.vrsettings` as a separate backup.
- Experimental developer toggles are labelled and need confirmation to save.
- Leaving the page or closing the window with unsaved changes asks to save or discard.
- `targetBandwidth` is written as a float everywhere, matching SteamVR.

### App
- Runs as the signed-in user; access-denied operations offer **Restart as administrator**.
- Closing waits for running operations to finish and releases connection resources.
- The window fits the current screen and remembers its size.

### Development
- Builds in Visual Studio before the native helper exists, with a warning.
- Test projects are xUnit projects in the solution.
