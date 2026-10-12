# Advanced streaming controls

The **Advanced controls** tab contains bounded experiments for the existing stream.
Every row has an **Override** checkbox. Unchecked encoder rows preserve Valve's value;
unchecked driver rows remove an override only when you explicitly change that row or
use **Restore advanced Valve defaults**. Missing driver defaults are shown as a
fallback, not as an observed zero. Ordinary Apply writes only edited driver rows.

Keep the live dashboard visible when comparing changes. Requested settings, accepted
API submission metadata, encoder timing, PC presentations and receiver recovery are
different measurements. An accepted setting does not establish better picture quality.

## Encoder experiments

These need the updated native monitor, an exact supported NVENC API ABI and a verified
driver caller. Older loaded helpers report that SteamVR must restart. Requests use a
versioned fixed-size structure; polling never starts or changes a request.

| Control | Range | Application and limits |
| --- | --- | --- |
| VBR quality target | 0–51, fractional | Live; 0 is automatic. Lower nonzero values request less quantization. VBR only and subject to bitrate/VBV limits. |
| Maximum QP | 0–51 | Live; applies the rate-control maximum to I/P/B frames. Valve's min-QP constraints and delta map still apply; this is not an absolute center-QP guarantee. |
| Peak bitrate | 25–500 Mbps | Live on an encoder advertising dynamic bitrate; VBR only. Also caps Valve's transient per-frame encoder average. An encoder budget, not an instantaneous network-traffic limit. |
| VBV buffer | 0.001–64 Mbit | Live if custom VBV is supported. A rate-control bit budget, not the headset's decoded-frame queue. |
| VBV initial delay | 0–64 Mbit | Live if supported; must fit the accepted/requested buffer. Units are bits, not measured display-delay milliseconds. |
| Cb / Cr offsets | −12 to +12 | Reconnect required. Override both together. Negative offsets improve chroma quantization at the cost of shared encoding budget. |
| Spatial AQ / strength | On/off, strength 0–15 | Reconnect required. Experimental with the external delta-QP map; no blanket claim of incompatibility or quality benefit. |
| Temporal AQ | On/off | Reconnect required and capability checked. Turning it on is refused if the encoder does not advertise support. |
| Preset | Valve default or P1–P7 | Reconnect required. Uses a matching preset query and initialization while preserving Valve's stream policy. Only a bounded quality-field whitelist is inherited; this is not the full generic preset configuration. |

The preset path preserves Valve's explicit codec/GOP/latency/resource/bitfield policy.
Earlier P3 trials did not demonstrate a useful visual benefit. Stronger presets, AQ,
quantizer limits or bursts can introduce latency or stutter; they are optional experiments.

The dashboard labels readbacks as the **last successful submitted configuration**, not
an NVIDIA effective-configuration getter. It separates applied, pending and rejected
override flags. Initialization-only changes, including removing them, remain pending
until the encoder is destroyed and initialized again. A Frame stop/reconnect can retain
the host NVENC session, leaving these requests pending. If reconnect does not apply
them, enable **Reapply my HEVC encoder and gaze settings** in Advanced and apply those controls.
Use the Frame's **Restart SteamVR** action as needed to exit its direct VR path, then
restart PC SteamVR with this app open. Wait for its monitor and requests to arm before
reconnecting the Frame. A PC-only restart or an ordinary remote stop can leave or
recreate the encoder too early; verify the applied/pending readback rather than
assuming the reconnect made a new initialization.
The helper can arm before a GPU encode session exists; no active picture is required
to prepare initialization-only requests. The app arms all native requests before
trying a live OpenVR refresh, so an unavailable pre-connection OpenVR interface does
not prevent those requests from being prepared.
Rejected experiments fall back to Valve's original configuration and retain rejection
metadata until a new Apply. The helper keeps restoration hooks installed if Stop cannot
yet restore an initialization-only override.

Explicit Advanced Apply resubmits an existing nondefault encoder request, allowing
the same values to be retried after fixing their context. It does not automatically
retry rejected experiments during dashboard polling.

## Driver settings

| Control | Setting | Notes |
| --- | --- | --- |
| Automatic foveated width bounds | `minStreamFormatWidth`, `maxStreamFormatWidth` | 512–4096 pixels, multiples of 32. Used with automatic width; minimum must not exceed maximum. Researched unset fallbacks are 512/1344. |
| Nonfoveated width and bounds | `nonFoveatedStreamFormatWidth`, `minNonFoveatedStreamFormatWidth`, `maxNonFoveatedStreamFormatWidth` | Same bounds/alignment; these are stream-format widths, not gaze-region geometry. Researched unset minimum/maximum fallbacks are 768/1920. |
| Numeric foveation mode | `foveationMode` | 0–2; researched predicate identifies 0/2 as foveated and 1 as unfoveated. Reconnect. No auto/forced labels are inferred. |
| Encoder pooling | `usePool` | Optional resource pool; reconnect. Does not shrink the frame ring. |
| Asynchronous encoder start | `asyncStartEncode` | Changes encoder-start scheduling; reconnect and compare durations. |
| 10-bit request | `10bit` | Reconnect. Researched HEVC path can use it on compatible hardware; H.264 path rejects it. Dashboard reports accepted bit depth when available. |
| Sharpening / strength | `useSharpening`, `sharpeningStrength` | Strength 0–1. Changes apparent crispness, not compression-lost detail. |
| Timed retry | `enableTimedRetry` | Integer 0/1. Compare receiver retry/recovery rather than assuming disabling it helps. |
| Baseline FEC | `forceBaselineVideoFEC` | Reconnect. Trades redundancy overhead against loss tolerance. |
| QoS | `enableQoS` | Requests packet priority marking; depends on the network. |
| Asynchronous send | `asyncSend` | Changes sender scheduling; compare actual delays and recovery. |
| Backoff recovery | `backoffRecoveryCoefficient` | 0.5–10; adaptive recovery coefficient. Compare stability. |

OpenVR readback confirms the stored/live setting value. Driver consumers can wait for a
reconnect; image, timing and recovery readbacks establish the observed effect. This tab
never changes the display refresh rate or switches headset Wi-Fi off.

Bound validation uses current OpenVR values while SteamVR is running, rather than
stale disk overrides. An unset bound uses the researched fallback during validation
without writing that fallback into the configuration. For example, requesting a
minimum of 1536 with an unset maximum of 1344 is refused; override both bounds with
minimum no greater than maximum.

## Restoration and editing

**Restore advanced Valve defaults** clears the advanced encoder request and removes
only this tab's driver keys. Basic bitrate, foveation offsets and rate-control/pass
selection remain separate. Reconnect can be necessary for complete encoder restoration.
Settings made while SteamVR is stopped are saved for the next session; native requests
must be applied after the updated monitor is running.

Edits made during Apply remain pending even if a value is changed back. Unedited
additional-link controls follow the live value independently of unrelated edits.

## Research capabilities

Foveation radius/transition packing, explicit split-frame encoding and decoder
queue/buffer controls have explanatory capability cards rather than unsupported
switches. These require matching driver and receiver support before they can become
working stream controls.
