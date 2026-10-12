# Experimental codec modes

The codec selector in **Stream settings** switches the PC encoder and
Frame decoder together. Selecting a dropdown item alone does not change the stream.
Use **Apply and reconnect** to apply the selected mode. Switching restarts SteamVR and
reconnects the Frame; a running game may close.

## Supported setup

The codec-enabled portable release includes the companion's isolated Python
runtime, the PC encoder adapter and the experimental AV1 decoder. It does not
require a PC Python installation or a manually patched headset.

The current experiment requires:

- Windows 11 x64 with SteamVR and a NVIDIA GPU whose NVENC supports the selected
  codec. AV1 was tested on an RTX 5090; older GPUs without AV1 encoding cannot use it.
- Steam Frame kernel **`6.18.0-ge66bc2ca6f8c`** and the stock ARM64 VRLink client with
  SHA256 **`31addef0c9553d3770b5570487699e96d70271d4073cd6c03b080fdee6ae2153`**.
  Module, client and firmware fingerprints are checked before changing them.
- The dedicated Valve dongle, with the Frame reachable at **`10.35.78.1`**.
  Keep the Frame's Wi-Fi enabled for discovery and authentication.
- Developer Mode and a Frame user password set under **Developer → Set User Password**.
- Trusted SteamOS Devkit SSH pairing and Windows OpenSSH. Configure the private-key
  and known-hosts paths under **Connection** as described in the
  [connection guide](connection-setup.md).

An unknown kernel, client, firmware or unsupported encoder is rejected. This is
an exact-build prototype, not support for every future driver or headset update.
The ordinary HEVC controls and monitor remain separate from these experiments.

## Start a session

1. Extract the complete codec-enabled ZIP. Keep `CodecLab` beside the app executable.
2. Configure and save the trusted wireless SSH connection. Use `10.35.78.1` as the host.
3. Keep the Frame awake and connected to this PC through the dongle.
4. Open **Stream settings**, select **AV1**, **100 Mbps** and **90 Hz**
   for an initial viewing test.
5. Enter the Frame user password in the private password field and click **Apply and reconnect**.
6. Wait for the session status to become ready. Reconnect the Frame if needed, then
   reopen the game and check the dashboard's active codec and fresh output.

The password goes only through the owned companion process's standard input and
the authenticated SSH session. It is not saved in preferences, files or command
arguments. Switching modes within the same authenticated session needs no further
password entry.

## Modes and measurement

| Mode | Encoder/decoder | Current scope |
| --- | --- | --- |
| HEVC | Stock SteamVR encoder and Frame decoder | Restores the ordinary client/decoder path. |
| AV1 | NVIDIA NVENC and Frame Qualcomm hardware decoder through the experimental Iris module | 8-bit Main, packed image up to 1536 × 6144, 90 or 120 Hz. |
| H.264 | NVIDIA NVENC and the Frame's stock Qualcomm hardware decoder | Packed image 1024 × 4096, 90 or 120 Hz; the smaller image is an explicit current limitation. |

H.264 accepts 25–500 Mbps requests; AV1 retains its 25–200 Mbps range.
The field is the encoder's requested budget. Actual compressed output
depends on content, and no filler is added to force a minimum transmitted rate.
A 200 Mbps request did not maintain the desired new-frame rate in the earlier AV1
trial. Display refresh, encoded FPS and transmitted Mbps measure different things.

The live dashboard verifies AV1/H.264 from the loaded adapter's codec identity,
successful initialization and advancing encoded-output counters. The HEVC monitor
uses successful NVENC codec GUID readback. A requested mode or old saved session
is insufficient evidence: while reconnecting or without fresh output, the app
shows waiting, stopped or unknown rather than asserting a live codec.

Custom foveation and advanced HEVC hooks cannot be installed on top of the paired
codec adapter. Valve's native foveation remains part of the stream. The provisional
quality-map conversion is not a calibrated comparison of HEVC and AV1 image quality.

## Restoration and recovery

Choose **HEVC** in Stream settings and apply to remove the temporary client mount, unload
the AV1 module if active, restore the stock decoder and restart ordinary HEVC.
The supervised codec session also ends after 90 minutes or when SteamVR exits.
Closing the app window does not itself end the independent companion session.

The helper changes only temporary runtime state. It does not replace the installed
kernel, firmware, original client file or boot module files. Rebooting the Frame
uses the stock installed components. **Back to stock** additionally restores a verified
bitrate patch and removes the app's saved tuning overrides, saving the previous setup first.
Host bitrate, refresh and stream-width
settings are written to SteamVR and remain configuration settings after the session.

If startup fails, read the codec status before retrying. Stale or unverified
companion ownership blocks a second session. Use the restore action while the
service is reachable; after a lost connection, reconnect the Frame and confirm
stock HEVC before opening another codec session.

## Clean-package validation (October 12, 2026)

The portable ZIP was extracted into an isolated directory and tested using its bundled
runtime. Each experimental codec started from the verified stock Frame client and
`qcom-iris` decoder, with the PC driver restored to its original hash. H.264 and AV1
both produced visibly decoded moving test patterns over the dedicated dongle.
Closing each session restored the exact original Frame client and stock decoder.
A separate pristine PC driver copy, with no patch receipt or backup, passed the
500 Mbps patch and byte-for-byte restoration round trip.

At a 100 Mbps target and 90 Hz, the 41-second moving-pattern trials measured:

| Mode | New encoded frames/s | Encoded Mbps | Dongle Mbps | Reported dropped frames | Presentation timeouts |
| --- | ---: | ---: | ---: | ---: | ---: |
| H.264 | 89.99 | 72.95 | 91.92 | 0 | 1 |
| AV1 | 82.47 | 91.66 | 117.81 | 0 | 1 |

Display presents can include reprojection; zero reported drops does not establish
that every displayed frame was newly encoded. The AV1 stress trial did not sustain
90 new encoded frames per second. These checks establish functioning patch,
stream and recovery paths on the tested kernel/client, not compatibility with
unseen future builds or a sustained-performance guarantee.

## H.264 bitrate sweep (October 12, 2026)

The 200 Mbps software limit was extended to 500 Mbps for H.264 in the app,
preset validator, supervisor and native adapter. AV1's 200 Mbps limit is retained.
An older running supervisor is detected before sending an unsupported high-rate
request; restore HEVC and start a new session to use the updated companion.

Same moving diagnostic pattern, NVIDIA hardware encoding, stock Frame hardware
decoding, dedicated dongle, 1024 × 4096 packed image and 90 Hz:

| Requested Mbps | Actual encoded Mbps | Encoded fps | Reported dropped frames | Presentation timeouts |
| ---: | ---: | ---: | ---: | ---: |
| 200 | 148.5 | 89.88 | 0 | 0 |
| 225 | 166.0 | 89.73 | 0 | 0 |
| 250 | 184.6 | 89.59 | 0 | 3 |
| 300 | 211.4 | 85.54 | 0 | 51 |
| 500 | 56.5 | 14.95 | 1 | 780 |

The first four observations lasted about 41–42 seconds; the failing 500 Mbps
observation lasted 20.6 seconds and recorded 50 PC stream recovery requests.
Receiver retry, missing-buffer and decoder queue-failure counters were zero in
the observed windows. Zero reported dropped frames alone does not establish smooth
streaming: presentation timeouts and fresh encoded cadence are separate evidence.

225 Mbps was the highest clean **short** trial on this setup, producing about
166 Mbps of video and 210 Mbps of dongle traffic. The longer confirmation test
was not run because the user needed the PC. This is a starting point for a viewing
test, not a guaranteed maximum for other content, hardware or sessions. High targets
can severely stall this stream; the 500 Mbps control range does not establish
successful 500 Mbps throughput. The H.264 packed image remains smaller than AV1's.

## Source and licenses

`CodecLab/Assets/decoder-source.zip` contains the corresponding source for the
bundled GPL-2.0-only Iris decoder module. The codec package includes its source
provenance, port changes, license and build instructions. The PC adapter and
supervisor source are in `Native/AV1Lab` and `Frame/AV1` in this repository.
The bundled CPython runtime has its own license under `CodecLab/runtime`.

Valve's original VRLink binary and Qualcomm firmware are not distributed. The
client patch is generated from the verified original file on the user's Frame.
