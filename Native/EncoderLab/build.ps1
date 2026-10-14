$ErrorActionPreference = 'Stop'
$labVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$labVs = & $labVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $labVs) { throw 'Visual Studio C++ tools are required.' }
$labToolset = Get-ChildItem -LiteralPath (Join-Path $labVs 'VC/Tools/MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$labSdkRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$labSdk = Get-ChildItem -LiteralPath (Join-Path $labSdkRoot 'Include') -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um/Windows.h') } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $labToolset -or -not $labSdk) { throw 'C++ toolset and Windows SDK are required.' }
$labCompiler = Join-Path $labToolset.FullName 'bin/Hostx64/x64'
$labOutput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts/EncoderLab'))
New-Item -ItemType Directory -Path $labOutput -Force | Out-Null
$labOldPath = $env:PATH; $labOldInclude = $env:INCLUDE; $labOldLib = $env:LIB
try {
    $env:PATH = "$labCompiler;$labOldPath"
    $env:INCLUDE = "$($labToolset.FullName)\include;$($labSdk.FullName)\ucrt;$($labSdk.FullName)\shared;$($labSdk.FullName)\um;$($labSdk.FullName)\winrt"
    $env:LIB = "$($labToolset.FullName)\lib\x64;$labSdkRoot\Lib\$($labSdk.Name)\ucrt\x64;$labSdkRoot\Lib\$($labSdk.Name)\um\x64"
    & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot/../vendor" "/Fo:$labOutput/hevc_probe.obj" "/Fe:$labOutput/hevc_probe.exe" "$PSScriptRoot/hevc_probe.cpp" d3d11.lib
    if ($LASTEXITCODE) { throw 'HEVC lab compilation failed.' }
} finally { $env:PATH = $labOldPath; $env:INCLUDE = $labOldInclude; $env:LIB = $labOldLib }
