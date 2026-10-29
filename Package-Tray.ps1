param(
    # A CodecLab folder that supplies runtime\ (isolated Python; Tools\Prepare-CodecLab.ps1 builds it) and Backend\ (the PC hook binaries, or use -BuildBackend).
    [string]$CodecLabSource = (Join-Path $PSScriptRoot 'artifacts\CodecLab'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\SteamFrameTray'),
    # Rebuild the PC hook binaries from Native\H264Hook instead of taking them from CodecLabSource.
    [switch]$BuildBackend,
    # Bundle the .NET runtime so users need nothing installed (larger download). Pass -SelfContained:$false for a small build.
    [bool]$SelfContained = $true,
    # Also write SteamFrameTray.zip next to the output folder.
    [bool]$Zip = $true
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $out = [IO.Path]::GetFullPath($OutputDirectory)
    $source = [IO.Path]::GetFullPath($CodecLabSource)
    foreach ($required in @('runtime\python.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "CodecLab source is missing $required ($source). Run Tools\Prepare-CodecLab.ps1 or point -CodecLabSource at a CodecLab folder." }
    }
    $running = Get-Process -Name SteamFrameTray -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($out, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw 'Exit SteamFrameTray (right-click the icon, Exit) before packaging over it.' }

    $probe = Join-Path $PSScriptRoot 'Native\build\SteamFrameEncoderProbe.dll'
    if (-not (Test-Path -LiteralPath $probe)) { & (Join-Path $PSScriptRoot 'Native\build.ps1') -SkipTests }

    # Start clean so files from an earlier layout (loose runtime DLLs, language folders) cannot linger.
    if (Test-Path -LiteralPath $out) { Get-ChildItem -LiteralPath $out -Force | Remove-Item -Recurse -Force }

    # CodecLabSource is pointed at nothing so publish does not copy a second, partial CodecLab.
    # Single-file keeps the .NET runtime inside SteamFrameTray.exe, so the package root is just the exe, CodecLab and Native.
    & dotnet publish (Join-Path $PSScriptRoot 'SteamFrameTray\SteamFrameTray.csproj') -c Release -r win-x64 --self-contained $($SelfContained.ToString().ToLower()) "-p:CodecLabSource=$(Join-Path $out '_none_')" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=none -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    # Single-file publish bundles the probe DLL into the exe; the tray needs it as a real file under Native\.
    $native = Join-Path $out 'Native'
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    Copy-Item -LiteralPath $probe -Destination $native -Force

    $lab = Join-Path $out 'CodecLab'
    if (Test-Path -LiteralPath $lab) { Remove-Item -LiteralPath $lab -Recurse -Force }
    New-Item -ItemType Directory -Path $lab, (Join-Path $lab 'Backend') | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'runtime') -Destination $lab -Recurse
    if (Test-Path -LiteralPath (Join-Path $source 'Assets')) { Copy-Item -LiteralPath (Join-Path $source 'Assets') -Destination $lab -Recurse }
    foreach ($helper in @('codec_supervisor.py', 'root_session.py', 'frame_cef_stdio.py', 'start_stream.js', 'stop_stream.js')) {
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
    foreach ($file in @('SteamFrameTray.exe', 'CodecLab\codec_supervisor.py', 'CodecLab\runtime\python.exe', 'CodecLab\Backend\hook_control.exe', 'CodecLab\Backend\SteamFrameH264Hook.dll', 'Native\SteamFrameEncoderProbe.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $out $file))) { throw "Package is incomplete: $file is missing." }
    }
    if ($Zip) {
        $archive = "$out.zip"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $archive -CompressionLevel Optimal
        Write-Host "Zip: $archive"
    }
    Write-Host "SteamFrame Tray is ready: $(Join-Path $out 'SteamFrameTray.exe')"
} finally { Pop-Location }
