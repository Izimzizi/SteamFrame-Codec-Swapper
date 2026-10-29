param(
    # Where the CodecLab folder (runtime\ and, with -BuildBackend in Package-Tray.ps1, Backend\) is assembled.
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\CodecLab'),
    # Packages from before the Assets folder was dropped still require it to exist.
    [switch]$LegacyAssets
)
# Assembles the isolated Python the codec supervisor runs on from the official 3.13.5 embeddable package,
# keeping only what the supervisor needs (ctypes, the standard library zip, the C runtime).
$ErrorActionPreference = 'Stop'
$version = '3.13.5'
$sha256 = '7d2650fd9d1b9d002d4a315d5f354247fd6a44f30517c7ef577b08f57a0fb6d9'
$url = "https://www.python.org/ftp/python/$version/python-$version-embed-amd64.zip"
$out = [IO.Path]::GetFullPath($OutputDirectory)
$work = Join-Path ([IO.Path]::GetTempPath()) ('codeclab-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $archive = Join-Path $work 'python.zip'
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) { throw "Python $version download has an unexpected hash ($actual)." }
    $extracted = Join-Path $work 'embed'
    Expand-Archive -LiteralPath $archive -DestinationPath $extracted
    $runtime = Join-Path $out 'runtime'
    if (Test-Path -LiteralPath $runtime) { Remove-Item -LiteralPath $runtime -Recurse -Force }
    New-Item -ItemType Directory -Path $runtime, (Join-Path $runtime 'DLLs') | Out-Null
    foreach ($file in @('LICENSE.txt', 'python.exe', 'python3.dll', 'python313.dll', 'python313.zip', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
        Copy-Item -LiteralPath (Join-Path $extracted $file) -Destination $runtime
    }
    foreach ($file in @('_ctypes.pyd', 'libffi-8.dll')) { Copy-Item -LiteralPath (Join-Path $extracted $file) -Destination (Join-Path $runtime 'DLLs') }
    [IO.File]::WriteAllText((Join-Path $runtime 'python313._pth'), "python313.zip`n.`nDLLs`n..`n")
    if ($LegacyAssets) { New-Item -ItemType Directory -Path (Join-Path $out 'Assets') -Force | Out-Null }
    $check = & (Join-Path $runtime 'python.exe') -I -c "import ctypes, hashlib, json, queue, subprocess, threading, uuid; print('ok')"
    if ($LASTEXITCODE -ne 0 -or $check -ne 'ok') { throw 'The assembled Python runtime failed its import check.' }
    Write-Host "Python $version runtime ready: $runtime"
} finally { if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force } }
