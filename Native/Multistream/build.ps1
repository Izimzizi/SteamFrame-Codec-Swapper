param([string]$OutputDirectory = "$PSScriptRoot\..\..\artifacts\Multistream\bin")
$ErrorActionPreference = 'Stop'
$protoVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$protoVs = & $protoVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$protoTools = Get-ChildItem -LiteralPath (Join-Path $protoVs 'VC\Tools\MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$protoSdkRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$protoSdk = Get-ChildItem -LiteralPath (Join-Path $protoSdkRoot 'Include') -Directory | Where-Object {
 (Test-Path (Join-Path $_.FullName 'um\Windows.h')) -and (Test-Path (Join-Path $_.FullName 'ucrt\stdio.h'))
} | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
$protoCompiler = Join-Path $protoTools.FullName 'bin\Hostx64\x64'
$protoOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $protoOutput -Force | Out-Null
$protoOldPath=$env:PATH; $protoOldInclude=$env:INCLUDE; $protoOldLib=$env:LIB
try {
 $env:PATH="$protoCompiler;$protoOldPath"
 $env:INCLUDE="$($protoTools.FullName)\include;$($protoSdk.FullName)\ucrt;$($protoSdk.FullName)\shared;$($protoSdk.FullName)\um;$($protoSdk.FullName)\winrt"
 $env:LIB="$($protoTools.FullName)\lib\x64;$protoSdkRoot\Lib\$($protoSdk.Name)\ucrt\x64;$protoSdkRoot\Lib\$($protoSdk.Name)\um\x64"
 & (Join-Path $protoCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/I$PSScriptRoot\..\vendor" "/Fo:$protoOutput\sender.obj" "/Fe:$protoOutput\sender.exe" "$PSScriptRoot\sender.cpp" d3d11.lib dxgi.lib d3dcompiler.lib ws2_32.lib winmm.lib avrt.lib
 if($LASTEXITCODE){throw 'Multistream sender build failed'}
 & (Join-Path $protoCompiler 'cl.exe') /nologo /std:c++17 /O2 /EHsc /MT /W4 "/Fo:$protoOutput\contract_test.obj" "/Fe:$protoOutput\contract_test.exe" "$PSScriptRoot\..\..\Frame\Multistream\contract_test.cpp"
 if($LASTEXITCODE){throw 'Multistream contract build failed'}
 & "$protoOutput\contract_test.exe"
 if($LASTEXITCODE){throw 'Multistream contract tests failed'}
} finally {$env:PATH=$protoOldPath; $env:INCLUDE=$protoOldInclude; $env:LIB=$protoOldLib}
