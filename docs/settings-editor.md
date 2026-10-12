# Additional settings

The **Additional settings** page edits less common `driver_vrlink` settings, including
ones SteamVR doesn't expose in its own UI. Guided controls have one home in **Stream settings**
or **Advanced**, and are excluded here. Search by display name, key or description.
Settings are grouped into Streaming Quality,
Network, Display, Tracking & Input, and Advanced.

## Reading values

- Values come from your `Steam\config\steamvr.vrsettings`, on top of the driver's
  `default.vrsettings`.
- An amber marker shows settings that differ from SteamVR's default.
- If a stored value can't be read, that one setting shows its default with a warning, and the
  rest of the page still loads. Saving a change to it replaces the bad value.
- A value that isn't in a dropdown's list, for example a hand-edited timeout, appears as
  **Custom (value)** rather than being shown as the first option.
- If the defaults file is missing, your values still load; reset buttons are unavailable.

## Saving

- **SteamVR running:** changes go through SteamVR's live settings API with readback.
- **SteamVR stopped:** the file is rewritten atomically, keeping
  `steamvr.vrsettings.SteamLinkVRSettings.bak` (previous revision) and
  `steamvr.vrsettings.SteamLinkVRSettings.original.bak` (your file before the first save, never overwritten).

Unrelated sections, unknown keys and structured values are preserved exactly. Settings marked
*requires SteamVR restart* take effect next session.

## Resetting

**Reset** on a setting, or **Reset additional settings**, returns those settings to SteamVR's default. Saving a value equal
to the default **removes the override** instead of writing the number. That way, a future
SteamVR update that changes a default still applies to you.

## Unsaved changes

Switching pages or closing the window with unsaved changes asks whether to **Save**,
**Discard** or **Cancel**.

## Experimental options

Some Advanced toggles are developer switches that can disrupt or break streaming:
**Force Open Mode**, **Watch For Shader Changes**, **Spoof At Start** and **Enable 8814 AP**.
They're labelled *Experimental*, and saving a change to any of them asks for confirmation.
Reset them to the default if streaming misbehaves.

## Value types

SteamVR reads some numbers as floats and others as integers. The editor keeps them consistent
with SteamVR; for example `targetBandwidth` is always written as a float, and
`recommendedBandwidthMbit` as an integer.

## Complete presets and recovery

Save Additional settings edits before using **Stream settings → Save preset…**.
The preset includes all known saved driver values plus the current stream and advanced
controls, filtering, requested codec/refresh and verified bitrate-patch state. Each
dropdown entry is one complete setup. Selecting or reloading it fills the editor;
**Apply** activates it, including settings absent from the preset being returned to defaults.

**Back to stock** saves the current applied tuning first, closes the experimental codec
session, restores a verified original driver, and removes known app tuning overrides.
It retains pairing data and unrelated SteamVR settings. If headset SSH is unavailable,
the app reports that resetting the Frame display preference is pending. Reconnect and retry.
**Load previous tuning** stages the saved setup for review and Apply. Experimental codecs
may require the Frame user password again; presets never store it.
