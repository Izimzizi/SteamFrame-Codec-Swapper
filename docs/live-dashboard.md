# Live dashboard and diagnostics

The **Live dashboard** shows what actually reaches the encoder and the network, and
records streaming faults with timestamps.

## Starting the monitor

Click **Start live monitor** during an NVIDIA-encoded stream. Encoded bitrate appears after
two samples. **Stop live monitor** restores Valve's encoder policy and foveation before stopping.

The monitor is opt-in: opening the app never loads anything into SteamVR. It checks the
running driver's encoder layout first and refuses incompatible builds. If an identical
helper is already loaded, the dashboard reuses it without restarting it or changing your
quality offsets. See [Encoder probe internals](../Native/README.md) for how it works.

If **Start live monitor** keeps failing while SteamVR is running, the status bar offers
**Restart as administrator**.

## Readouts

| Readout | Source and meaning |
| --- | --- |
| Encoded video / frames per second | Successful NVENC output sizes from cumulative counters and a monotonic clock. No video payload is read or saved. |
| Requested target | Read live from running SteamVR. Shows unavailable when SteamVR is stopped. |
| Active adapter send / stream adapters | Windows counters for the Valve wireless dongle and USB NCM adapters. Includes overhead, FEC and other traffic, so it is not the encoded video bitrate. The graph only uses an adapter that uniquely matches a headset-confirmed active link. |
| Video path / headset links | The Frame's own active-link status over paired wireless SSH, or ADB when no wireless host is set. Distinguishes dedicated wireless, local network and USB. |
| Foveation | The helper's current offsets and how many frames it changed. |
| Requested NVENC policy / last accepted configuration | Requested hook settings versus the last successfully accepted, exact-ABI initialize/reconfigure call. Shows actual rate-control mode, pass mode, preset, encoder target, dimensions, fps, VBV and padding. A pending request is not labeled applied. |
| Whole-frame QP | Interval mean and last-frame NVENC QP metadata, not the gaze region's absolute QP. Missing/new-generation samples are unavailable. |
| Gaze map deltas | Last verified center changed/total tiles, effective delta range and clamp counts; outside-gaze saturation and protected-tile mismatches. These are relative quality-map deltas. |
| Advanced encoder configuration | Requested/applied/pending/rejected flags and last successful submitted CQ, maximum QPs, chroma/AQ, preset, peak/VBV and bit depth. This is API submission metadata, not an NVIDIA effective-config getter. |
| Encoder API durations | Mean/max and calls over 2 ms since monitor start for reconfigure/encode. Submit-to-output completion includes encoder/queue/lock time, excludes network and headset decoder. |
| PC presentation / GPU timing | Public OpenVR compositor counters and recent frame timings: dropped, reprojected and timed-out presentations on the PC. These are separate from headset receiver faults and reset with each SteamVR or scene session. |

The graph keeps 60 seconds. Missing, stopped, unsupported or stale samples appear as gaps,
never as zero. Links and adapters are never summed, because traffic can be duplicated across them.

## Streaming issue capture

While the dashboard is open, the app watches new log events and saves bounded JSONL files to
`%LOCALAPPDATA%\SteamLinkVRSettings\Diagnostics`. **Open capture folder** opens it.

| File | Contents |
| --- | --- |
| `streaming-health.jsonl` | PC-side SteamVR events: client recovery requests, bad-link delays, encoder stalls, compositor handoff warnings, encoder reconfigurations. |
| `frame-receiver-health.jsonl` | Headset receiver events over paired wireless SSH: retry requests, backtrack-window resets, unrecoverable FEC errors, late metadata, missing mapped buffers, buffer index mismatches, decoder queue failures. |
| `compositor-timing.jsonl` | PC presentation anomalies: drop, reprojection and timeout deltas with observation timestamps. |

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
