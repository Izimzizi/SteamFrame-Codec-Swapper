Run the standalone harness with .NET 9 or newer and a local, unmodified driver fixture:

```powershell
dotnet run --project Tests/PatcherTests -- 'C:\path\to\legacy-original\driver_vrlink.dll' 'C:\path\to\current-original\driver_vrlink.dll'
```

The first fixture must match SHA256 `5f6f063f1ece088491f572db8feb2f2433a5621d0adf7013de77b91604d146b5`. The optional second fixture must match `d415ba30fab129b4737ae19a3c71d7628c27e450dd8bf0002a1d4dd98ed58a05`. Neither binary is distributed with the app or tests. All mutations occur in copies in a unique temporary directory; fixtures are read only. The harness links production patcher sources without WinUI dependencies and injects simulated vrserver state only through the internal test constructor.

Checks cover exact output and original backup for both builds, restore/idempotency, rejected layouts and mixed/missing backups, segment preimages, cancellation, and SteamVR starting immediately before replacement. Adaptive cases shift raw section offsets or relocate section RVAs with adjusted imports and function tables, then verify patch receipts reproduce and restore the exact source. Ambiguous signatures, changed NVENC contracts, changed import targets and corrupt receipts are refused. A simulated Steam update confirms previous backups/receipts remain intact.
