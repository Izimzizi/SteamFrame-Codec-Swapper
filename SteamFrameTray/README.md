# SteamFrame Tray

A small notification-area app that keeps H.264 applied for the Steam Frame while it is **armed**.
The tray only writes the bitrate (and the 1024 stream width H.264 needs). Refresh rate, multiple links, Automatic bandwidth and everything else stay under SteamVR's control; if Automatic bandwidth is on the tray says so, since SteamVR may then lower the bitrate.

- **Arm H.264**: starts the paired codec supervisor in keep mode. With SteamVR closed it waits ("armed, waiting for SteamVR") and never launches SteamVR; with SteamVR running it asks before restarting it once. H.264 comes back by itself after every
  SteamVR restart. The Frame password is stored in Windows Credential Manager (only after the Frame accepts it).
- **Double-click the icon** (left button) arms or disarms; a single click does nothing.
- **Disarm**: restores stock HEVC and the original SteamVR streaming settings (bitrate, bandwidth mode, stream width).
- **Bitrate**: a slider inside the menu (applies when you let go), 25-500 Mbps. The tray never keeps a connection to SteamVR open, so it follows SteamVR's slider from its settings file. SteamVR's own slider is
  the source of truth: the tray follows it, and a change made here is applied live (no SteamVR restart) in every
  mode, armed H.264 or stock HEVC; with SteamVR closed it is written into SteamVR's settings file. Above 232 Mbps
  the bitrate patch for SteamVR's Steam Link driver is required; the Bitrate patch menu installs or removes it
  (SteamVR must be closed; if Steam is under Program Files, use "Restart as administrator" once).
  Re-arm once after updating so the new supervisor is in use.
- **Stream stats window** (menu): encoded Mbps with a 60-second average beside it, FPS, the rate the encoder was configured with, dropped frames (sender held back: total and last 60 s), verified active codec, 60 s graph.
  Stock HEVC monitoring injects the existing read-only probe into vrserver only while this window is open.
- **Start with Windows**: re-arms at login if it was armed when it last ran.
- **Encoder experiment** (menu): VBR or CBR and the NVENC preset for the next arm (default VBR, P2). Logged in the stats log header.
- **Exit** disarms and ends the supervisor. If the Frame cannot be reached, the supervisor notices the tray is gone and keeps retrying the restore for 10 minutes. Signing out or shutting Windows down does the same.

## First-time setup

1. On the Frame: turn on Developer Mode and set a user password (Developer -> Set User Password).
2. Extract the whole zip and run `SteamFrameTray.exe` (it is unsigned, so Windows SmartScreen may warn: More info -> Run anyway).
3. The **Setup / connection check** opens by itself. Address is `frame` over Wi-Fi, or `10.35.78.1` with the dongle.
   It shows the Frame's host key fingerprint for you to trust once, creates this app's own SSH key, and asks for the
   Frame password once to authorize that key on the Frame (one line added to `~/.ssh/authorized_keys`). The password is
   stored in Windows Credential Manager only after the Frame accepts it. Valve's SteamOS Devkit Client is **not** needed;
   if you already paired with it, that key is used as before.
4. Right-click the icon -> **Arm H.264**.

Windows' optional "OpenSSH Client" feature is required (the check says so if it is missing). Wi-Fi-only use has not been tested on real hardware yet.

**Record stats log** (menu) writes a CSV of the run (one row per second, with the setup in `#` header lines) to
`%LOCALAPPDATA%\SteamFrameTray\Logs`; use it to compare bitrates or to find out where bitrate goes missing.
"Open logs folder" is in the menu too.

The tray keeps all its files in `%LOCALAPPDATA%\SteamFrameTray` (settings, pinned host key, SSH key, session and logs)
and does not touch the earlier app's `SteamLinkVRSettings` folder.

Build: `dotnet build SteamFrameTray -c Release`. Package (with `CodecLab`): `.\Package-Tray.ps1 -CodecLabSource <a CodecLab folder with runtime\, Assets\ and Backend\>`.
