Run `dotnet run --project Tests/CoreTests` from the repository root on Windows. No NuGet test framework, WinUI, headset, SteamVR process, or Valve DLL fixture is required.

The console harness links the production core classes. It checks preservation of unknown SteamVR JSON, complete atomic saves with exact prior-revision backups, malformed-file refusal, concurrent independent setting saves, portable Steam/runtime path separation, preference defaults/round trips, and encoded bitrate measurement across timing changes, resets and stalls. It deliberately verifies that a 300 Mbps requested target can coexist with 20 Mbps measured output.

Disk tests write only a unique directory beneath the system temporary directory and remove that verified directory on exit. Preference file tests use explicit paths in that directory, never the user's Frame preferences.
