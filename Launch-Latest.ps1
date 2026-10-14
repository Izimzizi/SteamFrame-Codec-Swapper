<#
Launch the newest complete local app output, ordered by the app assembly's
timestamp (the .NET apphost EXE may not change when application code changes).
Use -ResolveOnly to inspect selection without starting the app.
#>
[CmdletBinding()]
param([switch]$ResolveOnly, [string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
# Windows PowerShell's -File parameter binding can evaluate defaults before
# $PSScriptRoot is populated. Resolve the default after binding, from this file.
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSCommandPath
}

function Get-AppBuildDirectories([string]$Root) {
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    foreach ($relative in @('SteamLinkVRSettings\bin', 'artifacts')) {
        $path = Join-Path $Root $relative
        if (Test-Path -LiteralPath $path -PathType Container) { $pending.Push($path) }
    }
    while ($pending.Count) {
        $directory = $pending.Pop()
        $item = Get-Item -LiteralPath $directory -Force -ErrorAction SilentlyContinue
        if (-not $item -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        if (Test-Path -LiteralPath (Join-Path $directory 'SteamLinkVRSettings.dll') -PathType Leaf) { $directory }
        foreach ($child in Get-ChildItem -LiteralPath $directory -Directory -Force -ErrorAction SilentlyContinue) {
            # Never pick intermediate, backed-up or package-staging output.
            if ($child.Name.StartsWith('.') -or $child.Name -match '(?i)^(obj|ref|refint|intermediatexaml|temp|tmp|node_modules|packages|runtimes|Native|CodecLab|Assets|docs|Licenses)$|(^|[-_])(backup|previous|staging|partial|temp|tmp)([-_]|$)') { continue }
            if (-not ($child.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $pending.Push($child.FullName) }
        }
    }
}

function Get-AppBuildSnapshot([string]$Directory) {
    # Reading metadata never loads or executes the app assembly.
    $name = 'SteamLinkVRSettings'
    $required = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @("$name.exe", "$name.dll", "$name.runtimeconfig.json", "$name.deps.json", "$name.pri", 'App.xbf', 'MainWindow.xbf', 'Views\FramePage.xbf')) { [void]$required.Add($file) }
    try {
        $config = Get-Content -LiteralPath (Join-Path $Directory "$name.runtimeconfig.json") -Raw | ConvertFrom-Json
        $deps = Get-Content -LiteralPath (Join-Path $Directory "$name.deps.json") -Raw | ConvertFrom-Json
        if (-not $config.runtimeOptions.tfm -or -not $deps.runtimeTarget.name) { return }
        $target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
        if (-not $target -or -not ($target.PSObject.Properties.Name -match '^SteamLinkVRSettings/')) { return }
        foreach ($library in $target.PSObject.Properties.Value) {
            foreach ($kind in @('runtime', 'native')) {
                foreach ($asset in $library.$kind.PSObject.Properties) {
                    if ($asset.Name -and [IO.Path]::GetFileName($asset.Name) -ne '_._') { [void]$required.Add([IO.Path]::GetFileName($asset.Name)) }
                }
            }
            foreach ($asset in $library.runtimeTargets.PSObject.Properties) {
                if ($asset.Value.rid -ne 'win-x64' -or $asset.Value.assetType -notin @('runtime', 'native')) { continue }
                $leaf = [IO.Path]::GetFileName($asset.Name)
                if ($leaf -eq '_._') { continue }
                if (Test-Path -LiteralPath (Join-Path $Directory $leaf) -PathType Leaf) { [void]$required.Add($leaf) }
                else { [void]$required.Add($asset.Name) }
            }
        }
        if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $Directory "$name.dll")).Name -ne $name) { return }
        # Validate the x64 PE apphost without loading it.
        $stream = [IO.File]::Open((Join-Path $Directory "$name.exe"), 'Open', 'Read', 'Read')
        try {
            $binary = New-Object IO.BinaryReader($stream)
            if ($binary.ReadUInt16() -ne 0x5a4d -or $stream.Length -lt 64) { return }
            $stream.Position = 0x3c; $offset = $binary.ReadUInt32()
            if ($offset -gt $stream.Length - 6) { return }
            $stream.Position = $offset
            if ($binary.ReadUInt32() -ne 0x4550 -or $binary.ReadUInt16() -ne 0x8664) { return }
        } finally { $stream.Dispose() }
        $signature = New-Object 'System.Collections.Generic.List[string]'
        foreach ($relative in ($required | Sort-Object)) {
            $path = [IO.Path]::GetFullPath((Join-Path $Directory $relative))
            if (-not $path.StartsWith($Directory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { return }
            $file = Get-Item -LiteralPath $path -Force -ErrorAction Stop
            if ($file.PSIsContainer -or $file.Length -le 0 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return }
            # Refuse a file being written/copied; never lock it beyond this check.
            $read = [IO.File]::Open($path, 'Open', 'Read', 'Read')
            $read.Dispose()
            $signature.Add("$relative|$($file.Length)|$($file.LastWriteTimeUtc.Ticks)")
        }
        $assembly = Get-Item -LiteralPath (Join-Path $Directory "$name.dll")
        [pscustomobject]@{
            Executable = Join-Path $Directory "$name.exe"
            Directory = $Directory
            AssemblyLastWriteTimeUtc = $assembly.LastWriteTimeUtc
            CheckedFiles = $required.Count
            Signature = $signature -join "`n"
        }
    } catch { return } # An incomplete, changing or unreadable output is ineligible.
}

function Resolve-LatestAppBuild([string]$Root) {
    $Root = [IO.Path]::GetFullPath($Root)
    $candidates = @(Get-AppBuildDirectories $Root | ForEach-Object {
        $assembly = Get-Item -LiteralPath (Join-Path $_ 'SteamLinkVRSettings.dll') -ErrorAction SilentlyContinue
        if ($assembly) { [pscustomobject]@{Directory=$_; Executable=(Join-Path $_ 'SteamLinkVRSettings.exe'); AssemblyLastWriteTimeUtc=$assembly.LastWriteTimeUtc} }
    } |
        Sort-Object @{Expression='AssemblyLastWriteTimeUtc';Descending=$true}, @{Expression='Executable';Descending=$false})
    if (-not $candidates.Count) { throw "No complete SteamFrame Advanced Options build was found under $Root. Run Build.ps1 first." }
    # Two matching snapshots avoid selecting an output currently being copied.
    foreach ($candidate in $candidates) {
        $snapshot = Get-AppBuildSnapshot $candidate.Directory
        if (-not $snapshot) { continue }
        Start-Sleep -Milliseconds 500
        $again = Get-AppBuildSnapshot $candidate.Directory
        if ($again -and $again.Signature -eq $snapshot.Signature) { return $again }
    }
    throw 'App outputs are changing or incomplete. Wait for the build to finish and try again.'
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $selected = Resolve-LatestAppBuild $RepositoryRoot
        if ($ResolveOnly) { $selected | Select-Object Executable, Directory, AssemblyLastWriteTimeUtc, CheckedFiles | ConvertTo-Json }
        else { Start-Process -FilePath $selected.Executable -WorkingDirectory $selected.Directory }
    } catch {
        if ($ResolveOnly) { throw }
        Add-Type -AssemblyName System.Windows.Forms
        [void][Windows.Forms.MessageBox]::Show($_.Exception.Message, 'SteamFrame Advanced Options', 'OK', 'Error')
        exit 1
    }
}
