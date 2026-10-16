# Driver patcher

The stock Steam Link VR driver caps the video bitrate at **232 Mbps** when a wireless
adapter is present and **350 Mbps** otherwise. The **Patches & Recovery** page raises both
ceilings to **500 Mbps** and selects **HEVC level 6.2**, which higher bitrates require.

It does not add filler data or guarantee that every scene reaches that bitrate.

## Using it

On first launch, **Set up higher bitrate streaming** inspects the installed driver
and walks through installation. Choosing **Install 500 Mbps override** saves and
verifies the backup, closes SteamVR, applies and verifies the patch, then restarts
SteamVR if it was running. A running game may close. **Set up later** makes no
driver changes. Reopen the walkthrough under **Patches & Recovery**.

An **Already installed** result means the file hash and patch bytes match an
existing override. Opening the app or selecting a preset does not patch it, but
applying a preset can install its required override, including in earlier app
versions. The app does not infer installation from a preset's requested settings.
Unsupported, unreadable or missing files are never shown as a verified patch.

For manual installation or restore:

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
| Beta build 25839986 | `d415ba30fab129b4737ae19a3c71d7628c27e450dd8bf0002a1d4dd98ed58a05` | `34265101d56235e2f87e5b0e2f3fc2340ed1513a9b6d26f03c4fe12bafcbbc03` |
| Public stable build 25330290 | `1e846be041039798580a91452371f6288c614fb4c152126b137e2c693fb77eef` | `d0aeec1b80d31da146365b4b65fa4adadd7230d288ebf0d65e040b3991f28c5e` |

The stable and beta drivers use different private member offsets and register selections.
The scanner recognizes each complete instruction family separately, retaining the same
NVENC version, function-boundary, import, cap-constant and executable-padding checks.
Patch compatibility does not establish support for every private encoder or codec hook.

A missing original backup is normal before the first installation. If an unmodified driver
is reported as unsupported, collect **Driver file and verification** details and the SteamVR
build/branch. File verification repairs modified files; it cannot add a layout to the app's
compatibility scanner.

**Patches & Recovery** shows the installed build and branch, the recognized driver,
and the verified bitrate-patch versions. Build information comes from the selected
installation's Steam manifest; it never authorizes a patch. A pending branch switch
reports the installed branch, not the branch still being downloaded. A standalone
DLL copy may have no installation metadata and can still be checked by its bytes.

If a driver is unsupported, update this app and check again. Use **Driver file and
verification → Copy driver details** when reporting it. The report includes the
build/branch when available, full driver and backup hashes, receipt path, and the
specific compatibility check that failed. Missing or damaged recovery files have
separate messages. Restarting SteamVR cannot make an unsupported layout compatible.

Stable and beta support are retained together. The app selects the patch from the
installed file, so changing branches does not require changing app versions.

More detail is in [Compatibility evidence](../Native/VERIFICATION.md).

## Backups and restore

- The first analyzed build is backed up as `driver_vrlink.dll.steam-frame-bitrate-original.bak`.
- Other builds use `driver_vrlink.dll.steam-frame-bitrate-<source sha256>.bak`, so originals
  from earlier SteamVR versions survive updates.
- A `.patch.json` receipt sits beside each backup. Its locations are re-derived from the retained
  original and checked, never trusted blindly.
- Restore needs the matching, hash-verified original backup.

Known reference patches can be recognized even without their receipt or backup.
For other compatible builds, recognition requires the receipt and matching original.
Restore always requires the verified original. If it is missing, restore stays
disabled; Steam's file verification can recover Valve's driver instead.

## Bandwidth reality check

500 Mbps is a software ceiling, not proven link capacity. One measured USB 2 connection carried
about 318–325 Mbps of UDP payload. Real headroom depends on transport overhead, the link and the scene.
