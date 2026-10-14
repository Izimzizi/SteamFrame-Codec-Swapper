# Standalone filesystem tests. Never starts the app or builds any executable.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $repo 'Launch-Latest.ps1')
$fixtureParent = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\LauncherTests'))
$fixtureRoot = Join-Path $fixtureParent ('case-' + [guid]::NewGuid().ToString('N'))
$realBuild = Resolve-LatestAppBuild $repo
$checks = 0
function Assert-Launcher($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Invoke-LauncherEntryPoint([string]$WorkingDirectory, [string]$ExplicitRoot) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $repo 'Launch-Latest.ps1') + '" -ResolveOnly'
    if ($ExplicitRoot) { $start.Arguments += ' -RepositoryRoot "' + $ExplicitRoot + '"' }
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            $process.WaitForExit()
            throw 'The owned dry-run PowerShell process exceeded 30 seconds.'
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Launcher -File exited $($process.ExitCode): $errors" }
        return ($output | ConvertFrom-Json)
    } finally { $process.Dispose() }
}
function New-LauncherFixture([string]$Relative, [datetime]$AssemblyTime) {
    $folder = Join-Path $fixtureRoot $Relative
    [void](New-Item -ItemType Directory -Path (Join-Path $folder 'Views') -Force)
    foreach ($file in @('SteamLinkVRSettings.exe','SteamLinkVRSettings.dll')) {
        Copy-Item -LiteralPath (Join-Path $realBuild.Directory $file) -Destination (Join-Path $folder $file)
    }
    foreach ($file in @('SteamLinkVRSettings.pri','App.xbf','MainWindow.xbf','Views\FramePage.xbf','Dependency.dll')) {
        [IO.File]::WriteAllBytes((Join-Path $folder $file), [byte[]]@(1,2,3))
    }
    [IO.File]::WriteAllText((Join-Path $folder 'SteamLinkVRSettings.runtimeconfig.json'), '{"runtimeOptions":{"tfm":"net9.0"}}')
    [IO.File]::WriteAllText((Join-Path $folder 'SteamLinkVRSettings.deps.json'), '{"runtimeTarget":{"name":"test"},"targets":{"test":{"SteamLinkVRSettings/1":{"runtime":{"SteamLinkVRSettings.dll":{},"Dependency.dll":{}}}}}}')
    (Get-Item -LiteralPath (Join-Path $folder 'SteamLinkVRSettings.dll')).LastWriteTimeUtc = $AssemblyTime
    return $folder
}
try {
    $epoch = [DateTime]::UtcNow.AddDays(-1)
    $older = New-LauncherFixture 'artifacts\Older' $epoch
    $newer = New-LauncherFixture 'artifacts\Newer' $epoch.AddMinutes(1)
    (Get-Item -LiteralPath (Join-Path $older 'SteamLinkVRSettings.exe')).LastWriteTimeUtc = $epoch.AddMinutes(10)
    (Get-Item -LiteralPath (Join-Path $newer 'SteamLinkVRSettings.exe')).LastWriteTimeUtc = $epoch
    Assert-Launcher ((Resolve-LatestAppBuild $fixtureRoot).Directory -eq $newer) 'Assembly timestamp must outrank apphost timestamp.'
    $partial = New-LauncherFixture 'artifacts\Incomplete' $epoch.AddMinutes(2)
    Remove-Item -LiteralPath (Join-Path $partial 'Views\FramePage.xbf')
    Assert-Launcher ((Resolve-LatestAppBuild $fixtureRoot).Directory -eq $newer) 'A partial UI copy must not win.'
    [IO.File]::WriteAllBytes((Join-Path $partial 'Views\FramePage.xbf'), [byte[]]@(1))
    Remove-Item -LiteralPath (Join-Path $partial 'Dependency.dll')
    Assert-Launcher (-not (Get-AppBuildSnapshot $partial)) 'Missing runtime dependency must be refused.'
    foreach ($hidden in @('artifacts\.publish-pending','artifacts\installed-backup-1','SteamLinkVRSettings\bin\obj')) {
        [void](New-LauncherFixture $hidden $epoch.AddMinutes(5))
    }
    Assert-Launcher ((Resolve-LatestAppBuild $fixtureRoot).Directory -eq $newer) 'Staging, backups and intermediate outputs must be excluded.'
    $locked = [IO.File]::Open((Join-Path $newer 'SteamLinkVRSettings.dll'), 'Open', 'ReadWrite', 'None')
    try { Assert-Launcher ((Resolve-LatestAppBuild $fixtureRoot).Directory -eq $older) 'An assembly being written must not be selected.' }
    finally { $locked.Dispose() }
    $standard = New-LauncherFixture 'SteamLinkVRSettings\bin\x64\Release\net9.0' $epoch.AddMinutes(6)
    Assert-Launcher ((Resolve-LatestAppBuild $fixtureRoot).Directory -eq $standard) 'Standard build output must participate in fresh selection.'
    [IO.File]::WriteAllBytes((Join-Path $standard 'SteamLinkVRSettings.exe'), [byte[]]@(1,2,3))
    Assert-Launcher (-not (Get-AppBuildSnapshot $standard)) 'A corrupt apphost must be refused.'
    Assert-Launcher ((Invoke-LauncherEntryPoint $repo).Executable -eq $realBuild.Executable) 'Windows PowerShell -File must resolve the default repository root.'
    Assert-Launcher ((Invoke-LauncherEntryPoint $fixtureRoot).Executable -eq $realBuild.Executable) 'Default repository resolution must not depend on the working directory.'
    Assert-Launcher ((Invoke-LauncherEntryPoint $repo $fixtureRoot).Directory -eq $newer) 'Windows PowerShell -File must preserve an explicit repository root.'
    [pscustomobject]@{Passed=$true; Checks=$checks; AppLaunched=$false; BuiltAnything=$false} | ConvertTo-Json
} finally {
    # Only this invocation's explicitly created fixture directory is removed.
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolved.StartsWith($fixtureParent + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup left its root.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
