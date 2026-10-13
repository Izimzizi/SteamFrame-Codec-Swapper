# High-quality HEVC

Select **Stream settings → High quality HEVC (built-in)**. This fills the editor;
use Apply after reviewing the values. Loading the preset does not change the stream.
Every setting remains editable, and **Save preset…** saves your own version without
replacing the built-in. Existing personal presets remain available.

## Included settings

| Setting | Value | Where to edit |
| --- | --- | --- |
| Codec / bitrate target / refresh preference | HEVC / 400 Mbps / 90 Hz | Stream settings |
| Automatic bitrate / width | Off / off | Stream settings |
| Stream width | 2048 pixels | Stream settings → Picture quality |
| Custom gaze quality | On; center −2, outside gaze −6 | Stream settings → Picture quality |
| Advanced Supersample Filtering | Off | Stream settings → Picture quality |
| Additional video connections / AMD encoder | Off / off | Stream settings → More stream options |
| Rate control / encoding passes | CBR without padding / quarter-resolution multipass | Advanced → Encoding method |
| Reapply encoder and gaze settings | On | Advanced → Encoding method |
| NVENC preset / buffer / AQ overrides | Preserve Valve values; P2 was observed in testing | Advanced → Encoder quality and buffering |
| 10-bit request / asynchronous sending | On / on | Advanced → Driver and network settings |
| Sharpening / strength / QoS | On / 0.2 / on | Advanced → Driver and network settings |
| Recommended bandwidth | 400 Mbps | Additional settings |
| Debug region coloring / advanced graphs | Off / on | Additional settings |
| Driver bitrate ceiling | Compatible 500 Mbps override | Patches & Recovery |

Advanced rows have an **Override** checkbox. Clear it to restore Valve's value.
Other available controls include CQ, maximum QP, peak bitrate, VBV, chroma/AQ,
resource pooling, asynchronous encoder start, retries/FEC, backoff and width bounds.
Temporary keep-awake behavior is under **Diagnostics → Unattended testing** and
is not part of the preset.

When a complete preset is loaded, Apply on either Stream settings or Advanced
applies that complete edited setup. Refresh requests SteamVR's display preference;
refresh, sender and encoder initialization changes can require a reconnect.
Installing the bitrate override restarts SteamVR when required and may close a game.
Unsupported driver layouts are refused.

The gaze offsets change compression quality, not the physical gaze-region size.
Multipass changes encoder analysis. Native HEVC still uses one decoder; the
multistream research prototype is separate from the app's game-streaming path.

## What was measured

On the tested NVIDIA/Steam Frame setup, the accepted HEVC image was 2048×8192 at
10-bit and 90 Hz. A 270-second Galactic Racer observation after startup averaged
274.71 Mbps of coded video and 89.70 encoded fps. Headset interface traffic averaged
369.94 Mbps including overhead. There were zero encoder errors and zero reported
Frame dropped frames, but 21 Frame presentation timeouts and 49 PC application
drops. Racing versus menus was not independently classified.

These are observations, not a guarantee of sustained 400 Mbps video or perfect
frame pacing. Content, rendering load and link conditions affect the actual rate.
Equal-workload HEVC decoder experiments found no useful gain from two decoders;
four were slower. The preset keeps the stock single-decoder path.

Check **Connection** for the dongle's actual negotiated USB speed. USB 2 can limit
high-bitrate streaming; USB 3 signaling, Wi-Fi speed and actual coded bitrate are
different measurements. Unknown or stale USB results are shown explicitly.
