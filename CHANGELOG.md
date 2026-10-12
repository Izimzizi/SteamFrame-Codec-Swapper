# Changelog

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
