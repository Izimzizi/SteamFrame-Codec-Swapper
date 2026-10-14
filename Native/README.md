# Steam Frame encoder probe

Build on Windows x64 with `powershell -ExecutionPolicy Bypass -File Native/build.ps1`.

For normal-play diagnostics or recovery, build `HevcTune` against the app currently
in use with `Native/HevcTune/Build-ForApp.ps1 -AppDirectory <app-output-directory>`.
This copies and verifies that app's exact helper and writes a matching-build receipt.
Do not restore normal play with an old experiment's `HevcTune.exe`: it can install
its historical helper into a fresh SteamVR process and cause the current app's
update warning to return. Use the app's **Reconnect Frame** action for routine
recovery. Frozen experiment binaries are retained only for reproducing their trials.
Visual Studio C++ Build Tools, Windows SDK, CMake and Ninja are required. The script
discovers Visual Studio and its bundled CMake/Ninja; optional parameters override the
Visual Studio or SDK installation root. Output: `Native/build/SteamFrameEncoderProbe.dll`.
The app packages it at `Native/SteamFrameEncoderProbe.dll` beside its executable.

Compatibility is discovered from the driver instead of a whole-file hash allowlist.
Read-only inspection validates a bounded x64 DLL with NVENC loader markers. At Start,
the helper validates the actual loaded module and accepts submissions only when their
return address belongs to that module's executable sections. It hooks NVENC API 11.1's
successful `NvEncLockBitstream` calls for sessions learned from those submissions.
Exact public structure-version checks reject a changed ABI and expose a rejection
counter in the status. It counts `bitstreamSizeInBytes`;
it never accesses, stores, or copies video payload. Video counters exclude UDP,
FEC, encryption padding and duplicate transport links. The current generation and
QPC snapshot timestamps permit rate calculation from cumulative counter differences.
Other encoders do not have an actual-video-byte measurement here.

Quality support additionally requires unique normalized whole-function fingerprints
for five analyzed Valve encoder functions. Only verified instruction-relative
addresses and PE relocations are masked; private field offsets remain part of the
fingerprint. The Start function's address is discovered dynamically. Unrelated driver
changes or moved functions can therefore work without an app update. A changed or
ambiguous private layout leaves telemetry available and rejects quality activation;
future NVENC ABI changes can require an update.

Optional encoding-policy controls hook public `NvEncInitializeEncoder` and
`NvEncReconfigureEncoder` function pointers discovered through the API 11.1 function
list. The caller must belong to Valve's executable sections and parameter/config
versions must match the vendored public declarations exactly. Synchronous calls use
copied parameter/config structs; caller memory is preserved. Rate-control and multipass
change only when selected. Custom CBR disables codec filler and picture-timing SEI that
can request HRD padding. The separate advanced controls selectively override CQ,
maximum QP, peak bitrate, VBV, chroma-QP offsets, AQ, GOP bitrate consistency and
low-delay keyframe bit ratio. An explicit peak also bounds
Valve's transient per-frame average so the submitted average never exceeds that peak.
GOP length, frame rate, lookahead, references and resource formats remain preserved. Default calls pass through unchanged. Unknown ABI
and unrelated callers pass through without modification. Failed calls never become
accepted-policy telemetry.

Multipass choices are inherited, disabled, quarter-resolution two-pass and
full-resolution two-pass (experimental). Packed values 0–2 keep their existing
meanings; pass value 3 maps to `NV_ENC_TWO_PASS_FULL_RESOLUTION`.

`SetAdvancedEncoding` uses a fixed 64-byte/version-2 little-endian contract. Words
14/15 carry strict GOP target (0/1) and keyframe I/P bit ratio (1–4 when overridden),
with flags 512/1024. Version-1 requests are accepted only with their original flags
and zero reserved words. App/helper identity checks require a SteamVR restart when
the loaded helper differs from the bundled build. Missing
overrides preserve Valve values; zero can be an explicit setting such as automatic CQ.
Custom VBV, temporal AQ and dynamic bitrate/RC changes are capability checked. Chroma,
AQ, presets, GOP consistency and keyframe ratio are initialization-only; pending changes never mutate an existing
session's initialized bundle. Presets use a matching codec/tuning query and GUID, a
small whole-field quality whitelist, and preserve Valve's codec/layout/latency policy.
The active preset snapshot is immutable across later pending preset queries.
Rejected experiments retain the original caller configuration as fallback and block
that request until a new explicit Apply. Accepted metadata is the successful submitted
configuration, not an NVIDIA effective-config query or proof of visual improvement.
The keyframe ratio is documented for CBR with a single-frame VBV; NVENC may ignore
it in other configurations. The app limits this experiment to ratios 1–4. These
settings do not add padding, impose a minimum bitrate or change GOP length.

Explicit Start can arm callbacks before the first session by loading the installed
NVIDIA API from System32 only. Obtaining its public function table creates no encode
session or GPU resource. Initialization-only overrides must be armed before reconnect.
Stop retains restoration hooks while an initialized custom bundle remains active;
disconnect/reconnect restores that bundle when the request has been cleared.
Callback timing counters include reconfiguration, encoding and submission-to-bitstream
completion. Completion includes encoder/queue/lock work, not network or headset decode.

Policy readback reports a request separately from the last successful accepted
configuration. SetEncodingPolicy does not itself call NVENC or guarantee a minimum
video rate. Stop clears the request and refuses to disable hooks until all tracked
live custom-policy encoders have accepted a default configuration or been destroyed.
The managed explicit Apply/Stop path asks Valve to reconfigure through its settings
API while preserving the latest bitrate target. Read-only polling does not do so.

Quality changes use Valve's fresh map on every submission. Consecutive map quarters
are gaze/periphery/gaze/periphery. Values of 31 and the timing tile remain unchanged.
Gaze offsets are limited to -20 through 12, peripheral offsets to -12 through 31,
and the resulting ordinary-tile deltas are clamped to [-20, 31]. Invalid maps pass through unchanged, and
unexpected encoding errors disable quality adjustments. No fixed gaze position is cached.

Status includes a bounded snapshot of the last valid map: the offsets used, map time
and size, ordinary peripheral tile count, changed/saturated/clamped counts, effective
peripheral delta minimum/maximum, and protected tile count/mismatches. Ordinary tiles
exclude source mask values of 31 and the timing tile. Saturated means an ordinary
output delta is 31; clamped means the requested delta exceeded a bound. A +31 offset
therefore reports all ordinary peripheral tiles at 31, with zero protected mismatches.
Center metadata includes ordinary tile counts, changed/clamped counts and effective
delta range. Whole-frame NVENC average QP and last output size are metadata only;
the frame average is not the gaze region's absolute QP. Cumulative quantizer sample
count and sum allow a comparison window to exclude earlier frames. Strict bitrate
padding is not added: padding would not improve the center image.
Invalid maps retain the previous valid snapshot; compare its timestamp and offsets to
the requested settings. `last_map_nvenc_status` records the submission result, and the
encoded-byte counters separately confirm that output continues. No map or video payload
is included in this metadata.

Monitoring requires explicit Start. Read-only polling does not inject or start SteamVR.
The C# controller uses the helper's local read-only driver inspection and checks
process creation time, x64 architecture, helper cache hash, exported protocol and
loaded helper path. It does not load Valve's DLL during local inspection. An older quality helper
requires SteamVR to restart before this one can load. Stop restores original functions
and maps; the DLL and trampolines remain loaded until SteamVR exits so callbacks cannot
execute freed memory. Status JSON contains counters/settings only, written atomically
to an application-owned per-process path. No authentication keys or process arguments
are collected. Do not distribute SteamVR binaries or user/device credentials.

Bundled third-party code: MinHook and its HDE components (`vendor/minhook/LICENSE.txt`),
and NVIDIA's API 11 declarations (`vendor/NVIDIA-LICENSE.txt`, also retained in the header).
The helper uses a static MSVC runtime. Tests exercise map preservation, absent-driver
guards, metadata-only encoded-byte counting and adaptive discovery in isolated hosts;
they do not inject into the user's SteamVR process. The separate smoke executable is
inert without `--run`; its explicit live test samples metadata for eight seconds and
stops monitoring in `finally`, without changing stream or quality settings.
