# Driver patcher

The stock Steam Link VR driver caps the video bitrate at **232 Mbps** when a wireless
adapter is present and **350 Mbps** otherwise. The **Patches & Recovery** page raises both
ceilings to **500 Mbps** and selects **HEVC level 6.2**, which higher bitrates require.

It does not add filler data or guarantee that every scene reaches that bitrate.

## Using it

1. **Close SteamVR.** Install and restore refuse to run while `vrserver.exe` is running,
   and the check is repeated immediately before the file is replaced.
2. Click **Driver file and verification → Check driver** to see whether the installed driver is compatible, already
   patched, or unsupported.
3. Click **Install 500 Mbps override**, or **Restore original driver** to undo it.

SteamVR updates can replace the patched driver. Inspect again after updating.

## What changes

Six small byte changes:

| Change | Purpose |
| --- | --- |
| Wireless-adapter-present cap | 232 → 500 Mbps |
| General cap | 350 → 500 Mbps |
| HEVC reconfiguration default level | Level 6.2 |
| Preset level stub in unused executable padding | Applies level 6.2 to the encoder preset |
| Preset copy call | Routes the preset through that stub |
| Executable section size | Includes the stub |

Only these byte deltas ship with the app. Valve's DLL is never redistributed.

## Compatibility checks

Compatibility is discovered from the driver's own code and PE metadata, so a new file hash
alone doesn't require an app update. For an unfamiliar driver, the patcher requires all of:

- unique matching instruction contexts inside valid x64 function bounds;
- the expected NVENC structure layout;
- RIP-relative references to the original cap constants;
- a preset copy that calls an imported CRT `memcpy`;
- unused executable padding for the level stub.

Addresses are calculated from that file rather than reused from an old build. Ambiguous or
changed layouts are refused. Full source and output hashes and every byte being replaced are
checked before anything is written. A rewritten encoder can still require an app update.

Reference builds:

| Build | Original SHA-256 | Patched SHA-256 |
| --- | --- | --- |
| First analyzed driver | `5f6f063f1ece088491f572db8feb2f2433a5621d0adf7013de77b91604d146b5` | `82fd637f1d4b844ca349d2a98867cd0ba8f969a14de47c2a3192b875fb62425d` |
| Later Valve-signed driver | `d415ba30fab129b4737ae19a3c71d7628c27e450dd8bf0002a1d4dd98ed58a05` | `34265101d56235e2f87e5b0e2f3fc2340ed1513a9b6d26f03c4fe12bafcbbc03` |

More detail is in [Compatibility evidence](../Native/VERIFICATION.md).

## Backups and restore

- The first analyzed build is backed up as `driver_vrlink.dll.steam-frame-bitrate-original.bak`.
- Other builds use `driver_vrlink.dll.steam-frame-bitrate-<source sha256>.bak`, so originals
  from earlier SteamVR versions survive updates.
- A `.patch.json` receipt sits beside each backup. Its locations are re-derived from the retained
  original and checked, never trusted blindly.
- Restore needs the matching, hash-verified original backup.

A patch applied outside this app is recognized. If its backup is missing, restore stays disabled
until a verified original is available under the backup name.

## Bandwidth reality check

500 Mbps is a software ceiling, not proven link capacity. One measured USB 2 connection carried
about 318–325 Mbps of UDP payload. Real headroom depends on transport overhead, the link and the scene.
