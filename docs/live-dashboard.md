# Live dashboard and diagnostics

The **Overview** shows what actually reaches the encoder and the network, and
records streaming faults with timestamps.

## Starting the monitor

Click **Start monitoring** during an NVIDIA-encoded stream. Encoded bitrate appears after
two samples. **Stop live monitor** restores Valve's encoder policy and foveation before stopping.

The monitor is opt-in: opening the app never loads anything into SteamVR. It checks the
running driver's encoder layout first and refuses incompatible builds. If an identical
helper is already loaded, the dashboard reuses it without restarting it or changing your
quality offsets. See [Encoder probe internals](../Native/README.md) for how it works.

If **Start monitoring** keeps failing while SteamVR is running, the status bar offers
**Restart as administrator**.

The **Finish the app update** notice means SteamVR loaded a helper with different
contents from this app's bundled helper. **Reconnect Frame** or **Restart SteamVR
and resume** replaces it once. Reopening the same app afterward reuses the matching
helper. If the notice returns, another app build or an older research/recovery
tool may be loading its own helper; use the same current build for both. A DLL with
different executable code is not treated as compatible just because its protocol
number matches.

## Reconnecting after a game closes

Use **Reconnect Frame** on Overview if the headset stops streaming after leaving a
game. Keep the Frame awake. The button restarts SteamVR once (or launches it if it
has stopped), restores the previous helper's encoder requests, and waits for new
HEVC frames and bytes. A running VR game may close. If the old helper cannot be
read, saved tuning is used only when **Reapply after restart** is enabled; unapplied
presets and editor changes are not activated.

The app separately checks the headset's active link when connection diagnostics
are configured. A successful restart alone is not reported as a restored stream.
If video does not return, **unplug the dedicated dongle and plug it back in**, then
wait for it to reconnect. The same USB port or hub is fine; moving it is not
required for this step. Keep the Frame awake and select this PC from its streaming
menu if needed. Check **Dongle USB** afterward to confirm the negotiated USB speed.
In the October 14 recovery, the user replugged the dongle when streaming returned;
a SteamVR restart had also occurred, so that incident does not establish that a
software restart alone fixes the failure.

Recovery does not repeat the restart automatically. This does not change Wi-Fi profiles,
reset network adapters, require a Frame password, or restart the headset's USB
daemon. Paired H.264/AV1 sessions must return to HEVC through **Apply and reconnect**
first.

## Readouts

| Readout | Source and meaning |
| --- | --- |
| Encoded video / frames per second | Successful NVENC output sizes from cumulative counters and a monotonic clock. No video payload is read or saved. |
| Codec | HEVC, AV1 or H.264 verified from encoder metadata and recent encoded output. A requested codec or loaded adapter alone is not confirmation. The status shows Waiting when output stops, Unknown when monitoring stops or evidence is unavailable, and Unavailable when SteamVR exits. |
| Requested target | Read live from running SteamVR. Shows unavailable when SteamVR is stopped. |
| Active adapter send / stream adapters | Windows counters for the Valve wireless dongle and USB NCM adapters. Includes overhead, FEC and other traffic, so it is not the encoded video bitrate. The graph only uses an adapter that uniquely matches a headset-confirmed active link. |
| Video path / headset links | The Frame's own active-link status over paired wireless SSH, or ADB when no wireless host is set. Distinguishes dedicated wireless, local network and USB. |
| Dongle USB | The Valve wireless dongle's currently negotiated PC USB connection, read from Windows hub IOCTLs every five seconds. USB 2 is High-Speed (480 Mbps signaling); USB 3 is SuperSpeed (5 Gbps signaling) or faster. This is separate from the wireless connection and actual video throughput. Unknown means Windows could not verify it; not connected means a complete scan found no dongle. |
| Foveation | The helper's current offsets and how many frames it changed. |
| Requested NVENC policy / last accepted configuration | Requested hook settings versus the last successfully accepted, exact-ABI initialize/reconfigure call. Shows actual rate-control mode, pass mode, preset, encoder target, dimensions, fps, VBV and padding. A pending request is not labeled applied. |
| Whole-frame QP | Interval mean and last-frame NVENC QP metadata, not the gaze region's absolute QP. Missing/new-generation samples are unavailable. |
| Gaze map deltas | Last verified center changed/total tiles, effective delta range and clamp counts; outside-gaze saturation and protected-tile mismatches. These are relative quality-map deltas. |
| Advanced encoder configuration | Requested/applied/pending/rejected flags and last successful submitted CQ, maximum QPs, chroma/AQ, preset, peak/VBV and bit depth. This is API submission metadata, not an NVIDIA effective-config getter. |
| Encoder API durations | Mean/max and calls over 2 ms since monitor start for reconfigure/encode. Submit-to-output completion includes encoder/queue/lock time, excludes network and headset decoder. |
| PC presentation / GPU timing | Public OpenVR compositor counters and recent frame timings: dropped, reprojected and timed-out presentations on the PC. These are separate from headset receiver faults and reset with each SteamVR or scene session. |

## Wireless link card

SteamVR's own link measurements are written to `driver_vrlink.txt` while a stream runs. The
**Wireless link** card on Overview reads them as they appear, together with the dongle's radio
association from Windows. Nothing here needs headset pairing, a test scene or a closed game, so
it works while you play; [Auto tune](auto-tune.md) remains the full comparison of complete presets.

| Readout | Source and meaning |
| --- | --- |
| SteamVR link estimate | The driver's `Link BW: Est` line, logged every 30 seconds with its automatic/manual flag. With automatic bitrate on this is the rate SteamVR has settled on; with it off the line mirrors the manual target. When a bad-link event lowered the stream, the card also shows the rate SteamVR is actually sending at. |
| Held frames | `THROTTLE EVENT` lines: frames the sender held back because the link could not take them yet, in microseconds. They cluster at one or more frame times. The card counts them per minute; a clean link holds back almost none. A hold of a second or more, or one logged while the video link is down, is a stall or reconnect: it is listed as an event and counted separately, never as a held frame that would blame the bitrate. |
| Link capacity | The driver's `Link BW hist` line: a histogram of per-frame link-capacity measurements in 5 Mbps buckets up to 350 Mbps, plus an open-ended top bucket. The card shows the share at or above that top bucket, the 10th percentile and median (shown as "≥350" when they fall in the open-ended bucket), and the share below 100 Mbps. The bars use a log scale; the dashed line marks the requested target. |
| Radio | The Valve dongle as a Windows Wi-Fi interface: band, channel, channel width when the driver reports it, PHY type, PHY receive rate, signal and RSSI, and retry counters when the driver advances them. PHY rate is the radio's modulation rate, not video throughput; real throughput is roughly half. |
| Advisor | One recommended manual target with reasons, derived from the readouts above plus headset loss and latency, USB speed and the driver ceiling. **Use N Mbps** loads it into Stream settings with automatic bitrate off; nothing is applied until you click Apply. |

The graph's dashed line follows the driver's latest stated rate (an estimate line or a bad-link
step-down) once it is newer than the last target change, so an automatic-mode stream or a
stream SteamVR throttled shows where the bitrate really is.

### Capacity sweep

**Find my link's limit** raises the manual target in 50 Mbps steps during a stream, starting at
about half the current target and ending at the driver ceiling (232 Mbps on a stock driver, 500
with the bitrate override). Each step settles for 6 s and measures for 24 s: held frames per
minute, bad-link step-downs, headset packet loss and latency, stream recovery requests, receiver
retries, PC presentation drops and encoded video rate when the monitor is running. A step is
clean, marginal or failed: held frames under 0.5% of the refresh rate's frames per minute are
clean, under 2% marginal, more is failed, with the same split for held time (0.5% and 2% of the
minute), plus headset loss (0.2% / 1%), worst-case link latency above 50 ms, stream recovery
requests, and any SteamVR step-down below the step's target. The sweep stops after a failure or
two marginal steps in a row and reports the highest clean target. Your original target and automatic setting are restored when
the sweep ends, is stopped, or the app closes. Keep playing during the sweep so the encoder has
real work; an idle headset produces little video and tells you nothing about the limit. The sweep
and Auto tune refuse to run at the same time.

On the tested setup (6 GHz dongle at −65 dBm, 120 Hz, Auto tune's moving test scene, 500 Mbps
override installed) the sweep scored 150, 200, 250 and 300 Mbps clean with 132 to 229 Mbps of
actual video and about 2.5 held frames per minute, then failed 350 Mbps: a one-second link stall,
SteamVR lowering the stream to 270 Mbps and 39 recovery requests. It reported 300 Mbps as the
highest clean target and restored the original 270 Mbps manual target. That is a property of that
link and scene on that day, not a general Frame limit.

### Restart Frame stream

When the driver reports the video link inactive (the card's first line turns amber), the usual
fix on the headset is to stop streaming and stream again. **Restart Frame stream** does exactly
that through the paired wireless SSH connection: it asks the Frame's own Steam client to end its
PC VR stream, waits for the driver to log the link inactive, then asks it to stream to this PC
again and waits for the link to come back. SteamVR is not restarted. The sweep runs the same
recovery before it starts and up to twice when a step loses the link, and **Reconnect Frame**
uses the start half after its SteamVR restart when no fresh video appears. Nothing here changes
settings; it needs the paired SSH connection and a running SteamVR.

Bad-link events that changed the bitrate now say so in the event list ("SteamVR lowered the
stream from 200 to 150 Mbps after an 86.7 ms link stall") and carry the driver's holdoff count
and automatic flag in the capture file. Estimates, histograms and throttle events are written to
`streaming-health.jsonl` as synthesized records too. Auto tune records held frames and bitrate
changes for each trial, and a trial in which SteamVR lowered the stream below the candidate's
target counts as a serious issue: the link did not carry that rate.

The graph keeps 60 seconds. Missing, stopped, unsupported or stale samples appear as gaps,
never as zero. Links and adapters are never summed, because traffic can be duplicated across them.

An already-loaded paired AV1 trial is monitored through its guarded statistics interface;
the app does not install HEVC hooks alongside it or reapply saved encoder/foveation overrides.
Its first cumulative counter sample is a baseline. AV1 is shown as active only after fresh
output advances, and the readout clears after SteamVR restarts or a statistics read fails.
Stopping AV1 monitoring leaves the codec trial running and labels its codec as last verified.

## Switching codecs

Under **Stream settings**, select HEVC, AV1 or H.264, choose the codec-session target and
90/120 Hz, then click **Apply and reconnect**. The selection is the requested mode; **Current encoder**
and the dashboard's codec readout confirm encoder identity and fresh output separately.
Switching restarts SteamVR and reconnects the stream. Keep the Frame awake, Wi-Fi enabled,
and the dedicated dongle connected.

Starting a new codec session uses the private Frame user-password field. The password is
sent only through the owned supervisor's input pipe and is never written to preferences,
configuration files, requests or logs. An existing live codec session switches without
another authentication. Its heartbeat and process identity must validate before the app
sends a request; stale or incompatible sessions cannot start duplicate switches.

AV1 and H.264 use the paired runtime and disable ordinary encoder/foveation overrides.
H.264 currently uses a smaller packed image, 1024 × 4096, compared with AV1's 1536 × 6144.
Targets remain requests rather than guaranteed output rates. Choosing **HEVC** and applying
requests the supervised rollback. Closing the app leaves the session running; the supervisor
ends it after its session limit and restores HEVC.

## Streaming issue capture

While the dashboard is open, the app watches new log events and saves bounded JSONL files to
`%LOCALAPPDATA%\SteamLinkVRSettings\Diagnostics`. **Open capture folder** opens it.

| File | Contents |
| --- | --- |
| `streaming-health.jsonl` | PC-side SteamVR events: client recovery requests and frame-ID stream resets, bad-link delays, encoder stalls (including SubmitFrame, submit-to-start and reconfigure delays), compositor handoff warnings (including shared-texture `AcquireSync` timeouts in the compositor and server), encoder reconfigurations. |
| `frame-receiver-health.jsonl` | Headset receiver events over paired wireless SSH: retry requests, backtrack-window resets, unrecoverable FEC errors, late metadata, missing mapped buffers, buffer index mismatches, decoder queue failures. |
| `compositor-timing.jsonl` | PC presentation anomalies: drop, reprojection and timeout deltas with observation timestamps. |

`WaitForAcquire` reports a separate driver handoff-event wait. It can repeat
during 90 Hz streaming and does not itself establish failed texture ownership
or a dropped frame. `AcquireSync FAILED with WAIT_TIMEOUT` is the distinct
shared-texture acquisition failure; the captured message identifies which
condition was observed.

Each file rotates at 1 MiB and keeps two archives. Only synthesized event text and validated
numbers are saved, never raw log lines. Existing logs are read from their end, so old warnings
don't appear as new faults. Unavailable sources and capture gaps are reported.

Retry totals count requests, including repeats, so they are not unique packet loss or exact
dropped-frame counts. Wireless polling never stalls the graph if the link drops.

## Recovery traces

When SteamVR saves a recovery trace (`vrlink_aftm_*.csv`), the dashboard summarizes per-frame
encode, sender-to-receiver and headset decode timing for it. Traces are only written around
recovery events, not continuously. Missing client feedback and rows without a transmitted frame
are reported separately and are not counted as drops. Decode times use the headset clock, and
transport times use Valve's clock-mapped timestamps.

Encoder completion starts after submission returns and includes output handling.
Decoder turnaround starts after the coded input is queued and ends when userspace
observes decoded output; it includes driver and scheduling delays. Overlapping
frames mean a decoder interval longer than one display period is not by itself
proof that the decoder cannot sustain the refresh rate.

Valve's CSV field named **Submit Illumination** contains the receiver's chosen
future display target, sent before image submission. The dashboard therefore
reports receiver targets later than the original host prediction, rather than
calling these proven late submissions. Neither timestamp proves when pixels
actually appeared. See the [streaming system map](research/README.md) for the
traced event boundaries.
