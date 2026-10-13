# Frame codec companion, AV1 port and test tools

These tools implement the verified paired AV1 prototype. Preparation/build tools do not install packages or modify boot configuration. The explicitly authenticated root helper performs temporary driver/client swaps and rollback. Visible SteamVR streaming and a 10-minute 120-Hz dongle run have passed; see [implementation status](../../docs/av1-implementation-status.md) for exact results and limits.

The codec-enabled portable app bundles `codec_supervisor.py`, the isolated PC Python runtime, the PC adapter and the exact-build AV1 module with corresponding GPL source. Its Streaming codec card offers HEVC, AV1 and H.264, 25–200 Mbps AV1 requests, 25–500 Mbps H.264 requests and 90/120 Hz. It stages the module itself and generates client patches on the Frame from its hash-checked original file. Windows OpenSSH, trusted Devkit pairing and the Frame user password are required; [codec setup](../../docs/codec-modes.md) explains the supported kernel/client fingerprints and restoration. H.264 uses the stock hardware decoder at a smaller 1024×4096 packed layout. Unknown Frame builds are refused. A fixed 200 Mbps AV1 trial did not maintain the requested fresh-frame rate; controls are not performance guarantees.

Run `python Frame/AV1/paired_trial.py` from the repository to start a temporary AV1 session on this paired PC/Frame. Enter the Frame administrator password through the hidden terminal prompt. The launcher closes/restarts SteamVR, validates fingerprints, swaps the decoder/client temporarily, installs the PC adapter before the first encoder session and starts streaming. It restores HEVC when SteamVR exits, Ctrl+C is pressed or the session limit expires. Default session limit is 90 minutes; use `--minutes N` for 1–90 minutes. The authenticated root lifetime is capped at two hours. No password is saved or passed as an argument. Keep Wi-Fi enabled and use the dongle. The HEVC monitor app must be closed during startup; after AV1 is ready the updated app can read its counters without installing HEVC hooks.

The repository's legacy `paired_trial.py` launcher requires the exact staged lab assets; it is separate from the bundled UI supervisor. It accepts `--mbps`, `--peak-mbps`, `--fixed-target` and `--fps` for explicit AV1 trials, with the previous adaptive 100/115 Mbps, 120-fps configuration as its default. Neither path implements general codec negotiation. AV1 is Main 8-bit/P2, packed up to 1536×6144; emitted bitrate varies with content and no filler is added.

`stage_codec_package.py` stages the runtime from CPython 3.13 and the validated decoder candidate. All helper scripts now live here rather than ignored investigation directories. It requires the exact pinned module plus corresponding source and copies the decoder provenance, port patch, GPL license and [direct build recipe](decoder-build.md) into `decoder-source.zip`. It narrows the PC interpreter's native extensions to the companion's requirements and verifies isolated imports without touching SteamVR or the Frame. This is release staging, not an automatic source build or hardware test; see [building](../../docs/building.md).

`fetch_upstream_iris.py --ref v7.0 --directory OUTPUT` downloads a pinned public source tree and the public 6.18 mem-to-mem helper, recording checksums/provenance. This is not Valve's matching kernel source. The exact Valve kernel/header packages are obtained from the headset's configured package mirror and extracted separately.

`prepare_iris_candidate.py UPSTREAM OUTPUT` applies the reviewable Frame port changes and guards. It checks the AV1 path exists, retains the Frame firmware, adapts allocation/FourCC/build dependencies and disables automatic/default probing. `build_candidate.py CANDIDATE RESULTS` builds on the paired headset using the exact extracted headers. It records a successful module only when the build succeeds; a stale output after failure is not accepted.

`v4l2_av1_probe.cpp` enumerates/tries formats through its own descriptor and refuses silent codec/dimension fallback. It does not decode a frame. `dongle_receiver.py` software-decodes a paired synthetic AV1/IVF transfer to frame hashes. `run_dongle_test.py BACKEND_EXE` coordinates that receiver and the PC test and reports functional decoding separately from 120-fps pacing and VRLink integration.

`stage_client_codec.py ORIGINAL OUTPUT` changes two verified FourCC-construction instructions in a separate copy of the known Frame VRLink binary. It checks the original SHA256 and instruction bytes and refuses in-place writes or overwriting a candidate. The bundled root helper instead generates its temporary AV1/H.264 client copies from the verified installed client, so Valve binaries are not distributed. These fixed paired-trial overrides are not capability negotiation.

The current port includes AV1-specific upstream HFI and buffer paths. Qualcomm's supplied `video-driver` SM8650/pineapple and Iris33 code is an additional firmware, buffer and performance reference. It cannot be installed directly over the Frame's different binding/resource integration.

`root_session.py` is the ephemeral, fixed-action sudo test helper; it stages fingerprint-checked rollback files under `/run`, permits default-disabled and explicitly armed load tests, and restores the stock driver on orderly session close. It does not install into `/lib/modules` or modify boot files. `private_root_prompt.py` supplies private GUI entry; `root_broker_stdin.py` uses hidden terminal input. Neither stores a password. `root_ctl.py SESSION ACTION` sends nonsecret action requests and reads their receipts.

`hardware_decode.cpp` supplies a separate stateful V4L2 hardware acceptance test. It accepts 8-bit AV1/IVF or explicitly marked lab H264 access units at 90/120 fps, with linear NV12 capture and optional exact first-frame comparison. `paced` feeds at the declared frame rate; `ubwc` selects QC08C timing-only capture and rejects a linear golden image. `repeat=1..60` repeats a bounded local GOP with fresh timestamps. Source-change, coded padding, timestamp identity and final drain are checked. Short/small decode success is reported separately from pacing and from pixel verification. `hardware_decode_test.cpp` checks hardware-padding removal/crop/truncation and codec/rate/timestamp guards without opening a decoder. `test_root_session.py` checks recent diagnostic logs and decoder-number/busy safeguards without changing a real driver.

`frame_firmware_no_tz.c` ports the Frame's existing video-firmware/IOMMU/reset integration, verified with IDA against stock `iris.ko` and public Venus lifecycle code. The generator includes it in the isolated candidate and votes for 120-fps decoder power within existing OPP limits. `performance_inventory.py` only reads decoder clocks and selected thermal sensors.

## H.264 multistream prototype

`../Multistream/run_trial.py` runs a standalone real-time sender, wireless transport,
two/four stock V4L2 decoders, timestamp pairing and GPU composition. It can capture
SteamVR's undistorted left/right mirror textures with `--game`, or generate a
synthetic scene. `--overlay` displays the result on the Frame: separate per-eye
projection layers for games, a head-relative quad for synthetic content. The
existing app still sends one packed stream; multistream is an experimental CLI
path, not a released app mode. The receiver itself does not modify installed
clients, firmware, kernel modules or boot configuration.

From the repository root on this paired Windows PC:

```powershell
& Native/Multistream/build.ps1
python Frame/Multistream/run_trial.py --streams 4 --mbps 465 --minimum-mbps 400 --seconds 30 --udp --pause-steamvr --results artifacts/Multistream/four-400-check
```

The 465 Mbps value is the **encoder budget** used to obtain about 402 Mbps of actual
coded video for the deterministic noise scene. The distinct `--minimum-mbps 400`
argument requires 400 Mbps of measured delivered video; it does not relabel the
budget or insert filler. A 400 Mbps budget produced about 347 Mbps on this scene.
Use `--streams 2` for two 2048×1024 regions, or `4` for four 1024×1024 regions;
both compose a 4096×1024 image at 90 Hz. `--cabac` is an explicit comparison mode.
`--cpu-upload` compares against copying decoded NV12 instead of importing DMA buffers.
Duration is bounded to 10–60 seconds. The launcher restores streaming after a
requested pause.

For real SteamVR Home capture, first connect through the app's authenticated
codec session. The following uses a temporary 25 Mbps H.264 Valve connection for
tracking/audio/controllers, opens Home, runs the multistream test, and restores
the previous codec settings. It restarts SteamVR during those transitions.

```powershell
python Frame/Multistream/game_session.py --mbps 530 --minimum-mbps 400 --seconds 30 --udp --results artifacts/Multistream/home-400-check
```

This command defaults to headless measurement. Add `--overlay` only to display the
experimental game image. Game geometry is 4096×2048 aggregate (2048×2048 per eye),
split into four 1024×2048 decoder regions. Pose/projection metadata is paired with
the exact group ID. Projection layers use the inverse world-to-eye transform and
three output textures to avoid overwriting the currently displayed image. The
quality map uses a wide 70%-of-eye-diameter plateau and a soft peripheral transition;
when SteamVR does not provide valid gaze, every region receives uniform quality.
The public gaze API has returned invalid gaze on the tested Frame connection.

`session.json` records restoration attempts even if cleanup fails. Restoration
checks session owner and operation identity before each attempt and does not
overwrite a newer app operation. A failed switch may temporarily fall back to
stock HEVC; the bounded restoration retries its own failure once after settling.
High-bitrate Home runs currently fail the smoothness requirement; see the measured
results below before treating this as a usable game-stream mode.

The prototype uses the existing exact-pair SSH configuration from
`h264_multistream_trial.py`, the installed Frame compiler/OpenVR development files,
Windows Visual Studio C++ tools, an NVENC 12.1-capable GPU and an existing FFmpeg
executable for independent pixel checks. `--ffmpeg` overrides its local default.
The listener binds only `10.35.78.1:50124` and accepts the paired PC at `10.35.78.21`.
`--udp` adds port 50125 for interleaved video fragments with one XOR recovery
packet per sixteen data packets. TCP retains configuration/readiness control.
Windows UDP segmentation is used where available. Packet pacing spans 10.5 ms at
90 Hz; a bounded send queue fails rather than accumulating unlimited latency.
An unrecoverable UDP frame currently ends the test; retransmission and decoder
resynchronization are not yet implemented. Packet headers, tail padding and
recovery bytes are excluded from measured coded-video throughput.
This lab framing is unencrypted and is not a general authenticated streaming service.
No password is requested or stored; existing trusted SSH pairing is required.

`result.json` separates decoding/pixel correctness, delivered throughput, and
presentation-interval acceptance. Builds and nonsecret diagnostics are preserved.
Each first hardware-decoded region must match FFmpeg byte-for-byte. The final
GPU-composed image is saved as `composed-last.ppm`. DMA-buffer leases keep decoder
surfaces owned until GPU completion or deliberate discard; incomplete timestamps
are never combined. Incomplete sets expire after 50 ms so missing partners release
their surfaces. The queue is bounded, and dropped/late/duplicate sets are counted.
`contract_test.cpp` tests malformed framing, identity, reordering, bounded pairing,
surface release, projection inversion and recovery from single fragment loss.
Neither average composed FPS nor successful overlay submission
proves unique panel scanouts, motion-to-photon latency or subjective VR comfort.

`h264_multistream_trial.py` remains the lower-level capacity experiment: it
generates one GOP offline and replays it into independent TCP decoder instances,
or uses `--offline` to exclude wireless transport. It does not compose or display
the decoded streams and does not test real-time encoding. Its explicit lab H264
framing is not a standard H.264-in-IVF format. Both tools report real coded bytes
separately from requested budgets and reject unsupported codec/dimension fallback.

See [the October 13 measurements](../../docs/codec-modes.md#h264-multistream-prototype-october-13-2026)
for the verified behavior, Virtual Desktop comparison and remaining limits.
