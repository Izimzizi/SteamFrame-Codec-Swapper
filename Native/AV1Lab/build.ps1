param([switch]$CapsOnly,[string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$labVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$labVs = & $labVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$labTools = Get-ChildItem -LiteralPath (Join-Path $labVs 'VC\Tools\MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$labSdkRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$labSdk = Get-ChildItem -LiteralPath (Join-Path $labSdkRoot 'Include') -Directory | Where-Object {
 (Test-Path (Join-Path $_.FullName 'um\Windows.h')) -and
 (Test-Path (Join-Path $_.FullName 'ucrt\stdio.h')) -and
 (Test-Path (Join-Path $labSdkRoot "Lib\$($_.Name)\um\x64\d3d11.lib"))
} | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
$labCompiler = Join-Path $labTools.FullName 'bin\Hostx64\x64'
$labOutput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\artifacts\AV1Lab\backend'))
if($OutputDirectory){$labOutput=[IO.Path]::GetFullPath($OutputDirectory)}
New-Item -ItemType Directory -Path $labOutput -Force | Out-Null
$labOldPath=$env:PATH;$labOldInclude=$env:INCLUDE;$labOldLib=$env:LIB
try {
 $env:PATH="$labCompiler;$labOldPath"
 $env:INCLUDE="$($labTools.FullName)\include;$($labSdk.FullName)\ucrt;$($labSdk.FullName)\shared;$($labSdk.FullName)\um;$($labSdk.FullName)\winrt"
 $env:LIB="$($labTools.FullName)\lib\x64;$labSdkRoot\Lib\$($labSdk.Name)\ucrt\x64;$labSdkRoot\Lib\$($labSdk.Name)\um\x64"
 & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot\..\vendor" "/Fo:$labOutput\codec_caps.obj" "/Fe:$labOutput\codec_caps.exe" "$PSScriptRoot\codec_caps.cpp" d3d11.lib
 if($LASTEXITCODE){throw 'Codec capability query compilation failed'}
 if($CapsOnly){return}
 & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 /LD "/I$PSScriptRoot\..\vendor" "/I$PSScriptRoot\..\vendor\minhook\include" "/Fo:$labOutput\\" "/Fe:$labOutput\SteamFrameAV1Backend.dll" "$PSScriptRoot\av1_backend.cpp" "$PSScriptRoot\legacy_picture.cpp" "$PSScriptRoot\legacy_api_bridge.cpp" "$PSScriptRoot\steamvr_trial.cpp" "$PSScriptRoot\..\vendor\minhook\src\hook.c" "$PSScriptRoot\..\vendor\minhook\src\buffer.c" "$PSScriptRoot\..\vendor\minhook\src\trampoline.c" "$PSScriptRoot\..\vendor\minhook\src\hde\hde64.c" d3d11.lib /link "/IMPLIB:$labOutput\SteamFrameAV1Backend.lib"
 if($LASTEXITCODE){throw 'AV1 backend compilation failed'}
 & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot" "/Fo:$labOutput\trial_control.obj" "/Fe:$labOutput\trial_control.exe" "$PSScriptRoot\trial_control.cpp" psapi.lib
 if($LASTEXITCODE){throw 'Trial controller compilation failed'}
 if(Test-Path -LiteralPath "$PSScriptRoot\backend_test.cpp") {
  & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot" "/Fo:$labOutput\backend_test.obj" "/Fe:$labOutput\backend_test.exe" "$PSScriptRoot\backend_test.cpp" "$labOutput\SteamFrameAV1Backend.lib" d3d11.lib d3dcompiler.lib winmm.lib ws2_32.lib avrt.lib
  if($LASTEXITCODE){throw 'AV1 backend test compilation failed'}
 }
 & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot\..\vendor" "/Fo:$labOutput\legacy_picture_test.obj" "/Fe:$labOutput\legacy_picture_test.exe" "$PSScriptRoot\legacy_picture_test.cpp" "$labOutput\SteamFrameAV1Backend.lib"
 if($LASTEXITCODE){throw 'Legacy picture test compilation failed'}
 & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot" "/Fo:$labOutput\surface_test.obj" "/Fe:$labOutput\surface_test.exe" "$PSScriptRoot\surface_test.cpp" "$labOutput\SteamFrameAV1Backend.lib" d3d11.lib
 if($LASTEXITCODE){throw 'Surface test compilation failed'}
 if(Test-Path -LiteralPath "$PSScriptRoot\legacy_gpu_test.cpp") {
  & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot\..\vendor" "/Fo:$labOutput\legacy_gpu_test.obj" "/Fe:$labOutput\legacy_gpu_test.exe" "$PSScriptRoot\legacy_gpu_test.cpp" "$labOutput\SteamFrameAV1Backend.lib" d3d11.lib
  if($LASTEXITCODE){throw 'Legacy GPU bridge test compilation failed'}
 }
 foreach($labRevision in @(11,12,13)) {
  if($labRevision -eq 13 -and !(Test-Path -LiteralPath "$labOutput\nvEncodeAPI13.h")){continue}
  & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /Od /EHsc /MT /W4 "/DAV1_ABI_REV=$labRevision" "/I$PSScriptRoot\..\vendor" "/I$labOutput" "/Fo:$labOutput\abi$labRevision.obj" "/Fe:$labOutput\abi$labRevision.exe" "$PSScriptRoot\abi_probe.cpp"
  if($LASTEXITCODE){throw 'AV1 ABI probe compilation failed'}
 }
} finally {$env:PATH=$labOldPath;$env:INCLUDE=$labOldInclude;$env:LIB=$labOldLib}
