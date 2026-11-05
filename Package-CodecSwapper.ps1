param(
    # A CodecLab folder that supplies runtime\ (isolated Python; Tools\Prepare-CodecLab.ps1 builds it) and Backend\ (the PC hook binaries, or use -BuildBackend).
    [string]$CodecLabSource = (Join-Path $PSScriptRoot 'artifacts\CodecLab'),
    # Leave empty to let the script choose: artifacts\CodecSwapper, or when a CodecSwapper.exe is running from there (a test in progress) the next free
    # slot, artifacts\CodecSwapper-2, -3 ... The first slot nothing is running from is replaced, so folders only pile up while older builds are still in use.
    [string]$OutputDirectory = '',
    # Rebuild the PC hook binaries from Native\H264Hook instead of taking them from CodecLabSource.
    [switch]$BuildBackend,
    # Bundle the .NET runtime so users need nothing installed (larger download). Pass -SelfContained:$false for a small build.
    [bool]$SelfContained = $true,
    # Also write CodecSwapper.zip next to the output folder.
    [bool]$Zip = $true,
    # Overrides the version in CodecSwapper.csproj (the release workflow passes the release tag's number, so the exe and the tag match).
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    function Test-InUse([string]$directory) {
        $prefix = [IO.Path]::GetFullPath($directory).TrimEnd([char]92) + [char]92
        [bool](Get-Process -Name CodecSwapper -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    }
    if ($OutputDirectory) { $out = [IO.Path]::GetFullPath($OutputDirectory) }
    else {
        $out = $null
        foreach ($slot in 1..9) {
            $candidate = Join-Path $PSScriptRoot ('artifacts' + [char]92 + $(if ($slot -eq 1) { 'CodecSwapper' } else { "CodecSwapper-$slot" }))
            if (-not (Test-InUse $candidate)) { $out = [IO.Path]::GetFullPath($candidate); break }
        }
        if (-not $out) { throw 'Nine builds are running; exit one of them first.' }
    }
    $source = [IO.Path]::GetFullPath($CodecLabSource)
    foreach ($required in @('runtime\python.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "CodecLab source is missing $required ($source). Run Tools\Prepare-CodecLab.ps1 or point -CodecLabSource at a CodecLab folder." }
    }
    if (Test-InUse $out) { throw 'That CodecSwapper is running; exit it (right-click the icon, Exit) or leave -OutputDirectory out so a free folder is chosen.' }

    $probe = Join-Path $PSScriptRoot 'Native\build\SteamFrameEncoderProbe.dll'
    if (-not ((Test-Path -LiteralPath $probe) -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Native\build\SteamFrameBenchmark.exe')))) { & (Join-Path $PSScriptRoot 'Native\build.ps1') -SkipTests }

    # Start clean so files from an earlier layout (loose runtime DLLs, language folders) cannot linger.
    if (Test-Path -LiteralPath $out) { Get-ChildItem -LiteralPath $out -Force | Remove-Item -Recurse -Force }

    # CodecLabSource is pointed at nothing so publish does not copy a second, partial CodecLab.
    # Single-file keeps the .NET runtime inside CodecSwapper.exe, so the package root is just the exe, CodecLab and Native.
    & dotnet publish (Join-Path $PSScriptRoot 'CodecSwapper\CodecSwapper.csproj') -c Release -r win-x64 --self-contained $($SelfContained.ToString().ToLower()) "-p:CodecLabSource=$(Join-Path $out '_none_')" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=none $(if ($Version) { "-p:Version=$Version" }) -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    # Single-file publish bundles the probe DLL into the exe; the tray needs it as a real file under Native\.
    $native = Join-Path $out 'Native'
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    Copy-Item -LiteralPath $probe -Destination $native -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Native\build\SteamFrameBenchmark.exe') -Destination $native -Force

    $lab = Join-Path $out 'CodecLab'
    if (Test-Path -LiteralPath $lab) { Remove-Item -LiteralPath $lab -Recurse -Force }
    New-Item -ItemType Directory -Path $lab, (Join-Path $lab 'Backend') | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'runtime') -Destination $lab -Recurse
    if (Test-Path -LiteralPath (Join-Path $source 'Assets')) { Copy-Item -LiteralPath (Join-Path $source 'Assets') -Destination $lab -Recurse }
    foreach ($helper in @('session_supervisor.py', 'root_session.py', 'frame_cef_stdio.py', 'frame_cef_server.py', 'start_stream.js', 'stop_stream.js')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Frame\Codec\$helper") -Destination $lab
    }
    if ($BuildBackend) {
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ('av1lab-' + [guid]::NewGuid().ToString('N'))
        try {
            & (Join-Path $PSScriptRoot 'Native\H264Hook\build.ps1') -OutputDirectory $scratch
            foreach ($file in @('SteamFrameH264Hook.dll', 'hook_control.exe')) { Copy-Item -LiteralPath (Join-Path $scratch $file) -Destination (Join-Path $lab 'Backend') }
        } finally { if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force } }
    } else {
        foreach ($file in @('SteamFrameH264Hook.dll', 'hook_control.exe')) {
            $path = Join-Path $source "Backend\$file"
            if (-not (Test-Path -LiteralPath $path)) { throw "Backend\$file is missing from the CodecLab source; pass -BuildBackend to compile it." }
            Copy-Item -LiteralPath $path -Destination (Join-Path $lab 'Backend')
        }
    }
    # The same hook DLL doubles as the optional SteamVR driver (the tray's "SteamVR driver" menu installs it).
    $driverPackage = Join-Path $lab 'Backend\Driver'
    New-Item -ItemType Directory -Path (Join-Path $driverPackage 'bin\win64') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $lab 'Backend\SteamFrameH264Hook.dll') -Destination (Join-Path $driverPackage 'bin\win64\driver_steamframe.dll')
    Set-Content -LiteralPath (Join-Path $driverPackage 'driver.vrdrivermanifest') -Encoding ASCII -Value '{"alwaysActivate": true, "name": "steamframe", "directory": "", "resourceOnly": false, "hmd_presence": []}'
    foreach ($file in @('CodecSwapper.exe', 'CodecLab\session_supervisor.py', 'CodecLab\runtime\python.exe', 'CodecLab\Backend\hook_control.exe', 'CodecLab\Backend\SteamFrameH264Hook.dll', 'Native\SteamFrameEncoderProbe.dll', 'Native\SteamFrameBenchmark.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $out $file))) { throw "Package is incomplete: $file is missing." }
    }
    if ($Zip) {
        $archive = "$out.zip"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $archive -CompressionLevel Optimal
        Write-Host "Zip: $archive"
    }
    Write-Host "SteamFrame Codec Swapper is ready: $(Join-Path $out 'CodecSwapper.exe')"
} finally { Pop-Location }
