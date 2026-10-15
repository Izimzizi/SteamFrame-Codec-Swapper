param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$SkipNativeTests, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & (Join-Path $PSScriptRoot 'Native\build.ps1') -SkipTests:$SkipNativeTests
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $taskMsbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $taskMsbuild) { throw 'Visual Studio MSBuild is required to build WinUI XAML.' }
    $buildArguments = @('SteamLinkVRSettings\SteamLinkVRSettings.csproj', '-restore', "-p:Configuration=$Configuration", '-p:Platform=x64', '-nologo', '-v:minimal')
    if ($OutputDirectory) {
        $taskOutputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $PSScriptRoot $OutputDirectory }
        $buildArguments += "-p:OutputPath=$([System.IO.Path]::GetFullPath($taskOutputPath))\"
    }
    & $taskMsbuild @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
    # xUnit projects. PatcherTests skips unless SLVR_PATCHER_FIXTURE names a verified original driver.
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Write-Warning 'Python 3 is not on PATH; Frame receiver diagnostics tests will be skipped.' }
    foreach ($testProject in @('CoreTests', 'BenchmarkTests', 'StreamingHealthTests', 'StreamingFrameTraceTests', 'FrameReceiverDiagnosticsTests', 'PatcherTests')) {
        & dotnet test (Join-Path 'Tests' $testProject) --configuration $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "$testProject failed." }
    }
    & dotnet run --project 'Tests\CompositorTimingTests' --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Compositor timing tests failed.' }
} finally { Pop-Location }
