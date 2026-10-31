# SteamFrame Tray

The tray app of SteamFrame Advanced Options: an H.264 / Default (HEVC) switch for the Steam Frame with up to 500 Mbps, live encoder options, a stream monitor and an in-VR HUD.

The full, current documentation — what it does, first-time setup, the menu, the HUD, where files live, build and package steps, known limitations and credits — is in the [project README](../README.md). This project is heavily work in progress.

Quick reference:

- Build: `dotnet build SteamFrameTray -c Release`
- Package (with `CodecLab`): `.\Package-Tray.ps1 -CodecLabSource <a CodecLab folder with runtime\, Assets\ and Backend\>`
- Data folder: `%LOCALAPPDATA%\SteamFrameTray`
