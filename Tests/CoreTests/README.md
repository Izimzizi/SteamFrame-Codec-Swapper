Run `dotnet test Tests/CoreTests` from the repository root on Windows. No headset, SteamVR process, or Valve DLL fixture is required.

The tests compile everything under `Core/` directly and cover the shared code behind the tray and the kept extras: codec controller, stream codec status, advanced encoder controls, runtime process state, the wireless link tools (status, advisor, USB dongle) and the capacity sweep. Tests that touch the disk use a unique directory beneath the system temporary directory and never the user's real preferences.
