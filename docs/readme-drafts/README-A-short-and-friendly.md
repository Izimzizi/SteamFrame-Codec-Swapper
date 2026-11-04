# SteamFrame Codec Swapper

**Get a sharper, higher-quality picture from your PC on the Steam Frame.**

When you play PC VR games on the Steam Frame, your PC compresses the picture and sends it over Wi-Fi (or the dongle). Out of the box, Steam limits how much data it will send and only uses one compression method (HEVC). This small Windows app removes those limits:

- **Send up to 500 Mbps** instead of Steam's ~230 Mbps cap, so the picture has more detail to work with.
- **Switch to H.264**, an alternative way of compressing the picture, and switch back to the normal one at any time, even while you play.
- **See what your stream is doing** in a small window or in a little panel on your controller inside VR.

It sits in your Windows system tray. Closing it puts everything back to how Steam had it.

> **Heads up:** this is an unofficial hobby tool, not made or supported by Valve. A SteamVR or Steam Link update can break it. That's why switching back to normal is always one click away.

## What you need

- Windows 11
- An NVIDIA graphics card
- SteamVR, set up to stream to your Steam Frame
- Your Steam Frame in **Developer Mode** (Settings → Developer)

## Install

1. **On the Frame:** turn on Developer Mode and set a user password (Developer → Set User Password).
2. **Download** `CodecSwapper.zip` from the [Releases page](https://github.com/Izimzizi/SteamFrame-Codec-Swapper/releases) and unzip the whole thing somewhere (keep the folders together).
3. **Run `CodecSwapper.exe`.** Windows may warn that it's from an unknown publisher: click *More info → Run anyway*.
4. **Follow the setup window.** It connects to your Frame (type `frame` for Wi-Fi, or `10.35.78.1` if you use the dongle) and asks for the Frame password once.
5. **Right-click the tray icon → SteamVR driver → Install.**
6. **Pick H.264** from the tray menu. Start SteamVR and put your headset on.

That's it. To go back to normal, choose **Default (HEVC)** or just exit the app.

## Using it

Right-click the tray icon (bottom right of your screen, near the clock) for everything: the codec switch, a bitrate slider, and the stream monitor.

Want more than ~350 Mbps? Use **Bitrate limit patch → Install** in the tray menu (with SteamVR closed). You'll need to repeat this after SteamVR updates.

## Good to know

- With H.264 the picture width is limited to 1024 px (a limit of the NVIDIA encoder). Default (HEVC) can go wider, and the app switches this for you.
- If something looks off after a SteamVR update, choose Default (HEVC) and check the [Releases page](https://github.com/Izimzizi/SteamFrame-Codec-Swapper/releases) for a new version.

## Learn more

How it works, every setting, the benchmark, the VR panel and the build instructions are in the [docs folder](docs/).

## Credits

Forked from [SteamFrame-Advanced-Options](https://github.com/elliotttate/SteamFrame-Advanced-Options) by elliotttate (MIT license) and heavily extended. See [LICENSE](LICENSE).
