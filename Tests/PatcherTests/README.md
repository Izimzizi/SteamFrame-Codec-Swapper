Run the patcher tests with .NET 9 or newer. Diagnostic tests need no driver binaries;
patch round trips require local, unmodified driver fixtures:

```powershell
$env:SLVR_PATCHER_FIXTURE = 'C:\path\to\legacy-original\driver_vrlink.dll' # optional legacy harness
$env:SLVR_PATCHER_CURRENT_FIXTURE = 'C:\path\to\beta-original\driver_vrlink.dll'
$env:SLVR_PATCHER_STABLE_FIXTURE = 'C:\path\to\stable-original\driver_vrlink.dll'
dotnet test Tests/PatcherTests
```

The legacy fixture must match SHA256 `5f6f063f1ece088491f572db8feb2f2433a5621d0adf7013de77b91604d146b5`, beta must match `d415ba30fab129b4737ae19a3c71d7628c27e450dd8bf0002a1d4dd98ed58a05`, and stable must match `1e846be041039798580a91452371f6288c614fb4c152126b137e2c693fb77eef`. No Valve binary is distributed with the app or tests. All mutations occur in copies in a unique temporary directory; fixtures are read only. The harness links production patcher sources without WinUI dependencies and injects simulated vrserver state only through the internal test constructor.

Stable and beta tests run independently of the legacy harness. They include branch
switches at the same path, separate preserved backups, known patched files without
recovery files, unlisted compatible hashes, and exact restore. Diagnostic tests
verify that a known build number cannot authorize unknown bytes and that pending
branch changes cannot mislabel the installed version.

Checks cover exact output and original backup for both builds, restore/idempotency, rejected layouts and mixed/missing backups, segment preimages, cancellation, and SteamVR starting immediately before replacement. Adaptive cases shift raw section offsets or relocate section RVAs with adjusted imports and function tables, then verify patch receipts reproduce and restore the exact source. Ambiguous signatures, changed NVENC contracts, changed import targets and corrupt receipts are refused. A simulated Steam update confirms previous backups/receipts remain intact.
