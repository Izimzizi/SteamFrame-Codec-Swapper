# Changelog

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
