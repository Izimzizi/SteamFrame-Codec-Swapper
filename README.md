# SteamFrame Codec Swapper

**Live H.264/HEVC switching, up to 500 Mbps, NVENC tuning, stream monitor and in-VR HUD.**

Out of the box, SteamVR sends at most 232 Mbps and only uses one compression method (HEVC). This small Windows app removes those limits:

- **Up to 500 Mbps** instead of the 232 Mbps SteamVR streams at most.
- **Switch to H.264**, and back to the normal one (HEVC) at any time. SteamVR keeps running; only the Frame's stream restarts for a few seconds.
- **See what your stream is doing** on a small window on desktop or on a VR HUD attached to your controller.

It sits in your Windows system tray. Exiting it puts the stock HEVC stream back.

> **Heads up:** this is an unofficial hobby tool, not made or supported by Valve. A SteamVR or Steam Link update can break it. That's why switching back to normal is always one click away.

## What you need

- Windows built-in **OpenSSH Client** (the app uses it to talk to your Frame)
- An NVIDIA graphics card
- SteamVR, set up to stream to your Steam Frame
- Your Steam Frame in **Developer Mode** with a user password set

## Install

1. **On the Frame:** turn on Developer Mode and set a user password (Developer → Set User Password).
2. **Download** `CodecSwapper.zip` from the [Releases page](https://github.com/Izimzizi/SteamFrame-Codec-Swapper/releases) and unzip the **whole** zip somewhere. `CodecSwapper.exe` needs the `CodecLab` and `Native` folders beside it.
3. **Run `CodecSwapper.exe`.** The app is not signed, so Windows may warn: click *More info → Run anyway*.
4. **Follow the setup window.** The app shows the Frame's fingerprint to trust once, creates its own SSH key and asks for the Frame password once. The password goes to Windows Credential Manager only after the Frame accepts it; it is never written to a file.
5. **Right-click the tray icon → SteamVR driver → Install.** Required for H.264 and Encoder settings. (If you pick H.264 first, the app offers to install it. SteamVR is never restarted by the app.)
6. **Pick H.264** or stay on Default (HEVC) from the tray menu. Start SteamVR and put your headset on. The app never starts SteamVR itself; with SteamVR closed, your choice is remembered and applied when it starts.

To go back to normal, choose **Default (HEVC)** or exit the app.

## Going above 232 Mbps

SteamVR's own driver never streams more than 232 Mbps, and its slider stops at 350. This app's slider goes up to 500, but anything above 232 only takes effect with the bitrate patch.

1. Close SteamVR.
2. Tray menu → **Bitrate limit patch → Install**.  If access is denied for patching a driver in a protected folder, choose *Restart as administrator* once.
3. Set the bitrate you want. The app asks for the patch when you go above 350.

The patch keeps one backup of the original file. A SteamVR update overwrites the patch, so install it again after each update.

## Troubleshooting

| What you see | What to do |
|---|---|
| Windows warns about the app | It is unsigned. *More info → Run anyway*. |
| The app can't reach the Frame | Check the address (`frame` on Wi-Fi, `10.35.78.1` with the dongle), that Developer Mode is on and the password is set. Your codec choice is applied when the Frame comes back. |
| H.264 is refused | The Frame's client changed in a way the app doesn't recognise. Default (HEVC) keeps working; look for a new version on the Releases page. |
| After a SteamVR update H.264 or the high bitrate stops working | Install the SteamVR driver and the bitrate patch again, or choose Default (HEVC). |
| The streaming width doesn't affect H.264 | Expected: H.264 is limited to 1024 px width. HEVC can be set up to 2048 px. |

Logs are in `%LOCALAPPDATA%\CodecSwapper` (tray menu → **Open logs folder**). Attach `tray.log` when you report an issue.

## Learn more

How it works, every setting, the benchmark, the VR panel and the build instructions are in the [technical guide](docs/technical-guide.md) and the [docs folder](docs/).

## Credits

This project was **forked** from **[SteamFrame-Advanced-Options](https://github.com/elliotttate/SteamFrame-Advanced-Options)** by **[elliotttate](https://github.com/elliotttate)**, released under the MIT license. The original project provided the foundation this work started from: the SteamVR/Steam Link settings access, the bitrate patcher, the NVENC encoder probe, the streaming-health readers and the research notes under `docs/`.

After forking it was **heavily modified**: the tray app, the H.264 hook and Frame client patcher, the supervisor, the SteamVR driver, live H.264 ↔ HEVC switching with a kept session, the shared Frame debugger channel, dongle / Wi-Fi / multilink support, the encoder-option plumbing (including the HEVC overrides), the stream monitor changes and the in-VR HUD were built on top of it. Bugs in this fork are not the original author's.

Third-party components keep their own licenses: MinHook/HDE and the NVIDIA Video Codec SDK header under `Native/vendor`, and OpenVR's headers/API. The MIT license in [LICENSE](LICENSE) carries the original copyright notice.
