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
$labOutput = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\artifacts\H264Hook'))
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
 & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 /LD "/I$PSScriptRoot\..\vendor" "/I$PSScriptRoot\..\vendor\minhook\include" "/Fo:$labOutput\\" "/Fe:$labOutput\SteamFrameH264Hook.dll" "$PSScriptRoot\legacy_picture.cpp" "$PSScriptRoot\stock_forward.cpp" "$PSScriptRoot\legacy_api_bridge.cpp" "$PSScriptRoot\hook_entry.cpp" "$PSScriptRoot\..\vendor\minhook\src\hook.c" "$PSScriptRoot\..\vendor\minhook\src\buffer.c" "$PSScriptRoot\..\vendor\minhook\src\trampoline.c" "$PSScriptRoot\..\vendor\minhook\src\hde\hde64.c" d3d11.lib /link "/IMPLIB:$labOutput\SteamFrameH264Hook.lib"
 if($LASTEXITCODE){throw 'Hook DLL compilation failed'}
 & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot" "/Fo:$labOutput\hook_control.obj" "/Fe:$labOutput\hook_control.exe" "$PSScriptRoot\hook_control.cpp" psapi.lib
 if($LASTEXITCODE){throw 'Hook controller compilation failed'}
 & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot\..\vendor" "/Fo:$labOutput\picture_abi_test.obj" "/Fe:$labOutput\picture_abi_test.exe" "$PSScriptRoot\picture_abi_test.cpp" "$labOutput\SteamFrameH264Hook.lib"
 if($LASTEXITCODE){throw 'Picture ABI test compilation failed'}
 if(Test-Path -LiteralPath "$PSScriptRoot\bridge_gpu_test.cpp") {
  & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot\..\vendor" "/Fo:$labOutput\bridge_gpu_test.obj" "/Fe:$labOutput\bridge_gpu_test.exe" "$PSScriptRoot\bridge_gpu_test.cpp" "$labOutput\SteamFrameH264Hook.lib" d3d11.lib
  if($LASTEXITCODE){throw 'Bridge GPU test compilation failed'}
 }
 if(Test-Path -LiteralPath "$PSScriptRoot\mode_switch_gpu_test.cpp") {
  & (Join-Path $labCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot\..\vendor" "/Fo:$labOutput\mode_switch_gpu_test.obj" "/Fe:$labOutput\mode_switch_gpu_test.exe" "$PSScriptRoot\mode_switch_gpu_test.cpp" "$labOutput\SteamFrameH264Hook.lib" d3d11.lib
  if($LASTEXITCODE){throw 'Mode switch GPU test compilation failed'}
 }
} finally {$env:PATH=$labOldPath;$env:INCLUDE=$labOldInclude;$env:LIB=$labOldLib}
