# SteamFrame Codec Swapper

A small Windows tray app that lets your Steam Frame use a **higher-bitrate, optionally H.264** video stream from your PC.

Not sure if this is for you? Read the questions below.

## What is this, in plain words?

Playing PC VR on the Steam Frame means your PC films your game, squashes the video, and sends it to the headset. Steam decides *how* it squashes it (a method called HEVC) and *how much* data it sends (about 230 Mbps at most).

This app lets you change both. You can send a lot more data (up to 500 Mbps), and you can switch to a different squashing method (H.264). It also shows live numbers about how your stream is doing.

## Why would I want that?

- You have a strong PC and a good connection and want to **squeeze more picture quality** out of them.
- You want to **try H.264 vs HEVC** and see which looks or feels better to you.
- You like **tweaking and measuring**. The app has a built-in test that finds which settings work best on *your* setup.

## Why might I not?

- If the stock stream already looks great to you, you don't need it.
- It's an **unofficial** tool. Valve doesn't support it and an update to SteamVR or Steam Link may break it until it's updated.
- It needs an **NVIDIA** graphics card and **Windows 11**.

## Is it safe? Can I undo it?

Nothing is permanently changed. Choosing **Default (HEVC)** or exiting the app puts the stock stream back, on both the PC and the headset.

## How do I install it?

1. On your Frame, turn on **Developer Mode** and set a user password.
2. Download `CodecSwapper.zip` from [Releases](https://github.com/Izimzizi/SteamFrame-Codec-Swapper/releases) and unzip it (keep all the folders together).
3. Run `CodecSwapper.exe`. If Windows warns you, click *More info → Run anyway*.
4. In the setup window, enter `frame` (Wi-Fi) or `10.35.78.1` (dongle) and the Frame password.
5. Right-click the tray icon → **SteamVR driver → Install**.
6. Choose **H.264** in the tray menu and start SteamVR.

## How do I use it day to day?

Right-click the tray icon (near your clock). You can switch codec, drag the bitrate slider, change encoder options and open the stream monitor. Inside VR, a small round icon on your controller opens the same controls: point the laser at it and pull the trigger.

## I want above 350 Mbps

Tray menu → **Bitrate limit patch → Install** (with SteamVR closed). Repeat after SteamVR updates.

## Something's wrong

- Switch back to **Default (HEVC)**. That's the stock stream.
- H.264 is limited to a 1024 px stream width. That's an NVIDIA limit, not a bug.
- Check [Releases](https://github.com/Izimzizi/SteamFrame-Codec-Swapper/releases) for a newer version.

## I want the technical details

See the [docs folder](docs/): how it works, all options, the benchmark, the VR panel, building from source.

## Credits

Forked from [SteamFrame-Advanced-Options](https://github.com/elliotttate/SteamFrame-Advanced-Options) by elliotttate (MIT). See [LICENSE](LICENSE).
