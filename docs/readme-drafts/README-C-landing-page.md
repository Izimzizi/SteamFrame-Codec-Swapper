<h1 align="center">SteamFrame Codec Swapper</h1>

<p align="center"><b>More bitrate. A choice of codec. Live stats. For PC VR on the Steam Frame.</b></p>

<p align="center">
<a href="https://github.com/Izimzizi/SteamFrame-Codec-Swapper/releases">⬇ Download</a>
</p>

<!-- screenshot or short GIF of the tray menu + VR panel goes here -->

---

## In 20 seconds

Your PC sends VR video to your Steam Frame. Steam caps that stream at roughly **230 Mbps** and uses a single codec (HEVC).

This tray app lifts the cap to **500 Mbps**, lets you **switch to H.264** and back **while you play**, and shows you **live stream stats** on your desktop or on your controller in VR.

|   | Stock Steam | With this app |
|---|---|---|
| Max bitrate | ~230 Mbps | **500 Mbps** |
| Codec | HEVC | **H.264 or HEVC, your choice** |
| Stream stats | none | **monitor window + in-VR panel** |
| Tuning | none | **encoder options + built-in benchmark** |
| Undo | | **one click, back to stock** |

## What you need

- [ ] Windows 11
- [ ] NVIDIA graphics card
- [ ] SteamVR streaming to your Steam Frame
- [ ] Steam Frame with **Developer Mode** on

## Install in 3 steps

**1. Prepare the Frame.** Turn on Developer Mode and set a user password (Developer → Set User Password).

**2. Run the app.** [Download](https://github.com/Izimzizi/SteamFrame-Codec-Swapper/releases) `CodecSwapper.zip`, unzip it, run `CodecSwapper.exe` (Windows warning? *More info → Run anyway*). The setup window connects to your Frame: type `frame` (Wi-Fi) or `10.35.78.1` (dongle), then the password once.

**3. Install the driver and pick a codec.** Right-click the tray icon → **SteamVR driver → Install**, then choose **H.264**. Start SteamVR as usual.

To go back to normal at any time: **Default (HEVC)**, or exit the app.

## Good to know

<details>
<summary><b>Going above 350 Mbps</b></summary>

Tray menu → **Bitrate limit patch → Install**, with SteamVR closed. Repeat after each SteamVR update.
</details>

<details>
<summary><b>Why is H.264 limited to 1024 px wide?</b></summary>

It's a limit of NVIDIA's H.264 encoder. The app lowers the width while H.264 runs and restores yours when you switch back.
</details>

<details>
<summary><b>Is it official? Will updates break it?</b></summary>

No, it's an unofficial hobby tool. A SteamVR or Steam Link update can break it. Default (HEVC) always restores the stock stream.
</details>

## Dig deeper

[How it works](docs/) · [All options](docs/advanced-controls.md) · [Build from source](docs/building.md) · [Changelog](CHANGELOG.md)

## Credits

Forked from [SteamFrame-Advanced-Options](https://github.com/elliotttate/SteamFrame-Advanced-Options) by elliotttate (MIT). See [LICENSE](LICENSE).
