# Experimental codec modes

The **Streaming codec** card under **Steam controls** switches the PC encoder and
Frame decoder together. Selecting a dropdown item alone does not change the stream.
Use **Switch codec** to apply the selected mode. Switching restarts SteamVR and
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
  and known-hosts paths under **Connection setup** as described in the
  [connection guide](connection-setup.md).

An unknown kernel, client, firmware or unsupported encoder is rejected. This is
an exact-build prototype, not support for every future driver or headset update.
The ordinary HEVC controls and monitor remain separate from these experiments.

## Start a session

1. Extract the complete codec-enabled ZIP. Keep `CodecLab` beside the app executable.
2. Configure and save the trusted wireless SSH connection. Use `10.35.78.1` as the host.
3. Keep the Frame awake and connected to this PC through the dongle.
4. Open **Steam controls → Streaming codec**, select **AV1**, **100 Mbps** and **90 Hz**
   for an initial viewing test.
5. Enter the Frame user password in the private password field and click **Switch codec**.
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

The 25–200 Mbps field is the encoder's requested budget. Actual compressed output
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

Click **Restore HEVC and end session** to remove the temporary client mount, unload
the AV1 module if active, restore the stock decoder and restart ordinary HEVC.
The supervised codec session also ends after 90 minutes or when SteamVR exits.
Closing the app window does not itself end the independent companion session.

The helper changes only temporary runtime state. It does not replace the installed
kernel, firmware, original client file or boot module files. Rebooting the Frame
uses the stock installed components. Host bitrate, refresh and stream-width
settings are written to SteamVR and remain configuration settings after the session.

If startup fails, read the codec status before retrying. Stale or unverified
companion ownership blocks a second session. Use the restore action while the
service is reachable; after a lost connection, reconnect the Frame and confirm
stock HEVC before opening another codec session.

## Source and licenses

`CodecLab/Assets/decoder-source.zip` contains the corresponding source for the
bundled GPL-2.0-only Iris decoder module. The codec package includes its source
provenance, port changes, license and build instructions. The PC adapter and
supervisor source are in `Native/AV1Lab` and `Frame/AV1` in this repository.
The bundled CPython runtime has its own license under `CodecLab/runtime`.

Valve's original VRLink binary and Qualcomm firmware are not distributed. The
client patch is generated from the verified original file on the user's Frame.
