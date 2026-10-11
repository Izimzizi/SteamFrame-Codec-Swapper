# All streaming settings

The **All streaming settings** tab edits every known `driver_vrlink` setting, including
ones SteamVR doesn't expose in its own UI. Settings are grouped into Streaming Quality,
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

**Reset** on a setting, or **Reset All**, returns it to SteamVR's default. Saving a value equal
to the default **removes the override** instead of writing the number. That way, a future
SteamVR update that changes a default still applies to you.

## Unsaved changes

Switching tabs or closing the window with unsaved changes asks whether to **Save**,
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
