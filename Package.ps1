param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $taskMsbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $taskMsbuild) { throw 'Visual Studio MSBuild is required.' }
    $artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
    function Assert-ArtifactPath([string]$TaskPath) {
        $taskResolvedPath = [System.IO.Path]::GetFullPath($TaskPath)
        if (-not $taskResolvedPath.StartsWith($artifactRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Package operation would leave the artifact directory: $taskResolvedPath"
        }
        if ((Test-Path -LiteralPath $taskResolvedPath) -and ((Get-Item -LiteralPath $taskResolvedPath -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Package directories must not be reparse points: $taskResolvedPath"
        }
    }
    $portableRoot = Join-Path $artifactRoot 'SteamLinkVRSettings-SteamFrame-win-x64'
    $stageRoot = Join-Path $artifactRoot ('.publish-' + [guid]::NewGuid().ToString('N'))
    $packageBuildRoot = Join-Path $artifactRoot ('.package-build-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    & $taskMsbuild 'SteamLinkVRSettings\SteamLinkVRSettings.csproj' -restore -t:Publish -p:Configuration=Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false "-p:OutputPath=$packageBuildRoot\" "-p:PublishDir=$stageRoot\" -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Portable publish failed; output preserved at $stageRoot" }
    # WinUI's loose compiled XAML and app resource index are generated into the
    # build output, but this unpackaged Publish target does not copy them.
    $appResourceIndex = Join-Path $packageBuildRoot 'SteamLinkVRSettings.pri'
    if (-not (Test-Path -LiteralPath $appResourceIndex)) { throw 'WinUI app resource index was not built.' }
    Copy-Item -LiteralPath $appResourceIndex -Destination $stageRoot
    foreach ($compiledXaml in Get-ChildItem -LiteralPath $packageBuildRoot -Recurse -File -Filter '*.xbf') {
        $relativeXaml = [IO.Path]::GetRelativePath($packageBuildRoot, $compiledXaml.FullName)
        $publishedXaml = [IO.Path]::GetFullPath((Join-Path $stageRoot $relativeXaml))
        Assert-ArtifactPath $publishedXaml
        if (-not $publishedXaml.StartsWith($stageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Compiled XAML destination leaves the publish directory.'
        }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($publishedXaml)) -Force | Out-Null
        Copy-Item -LiteralPath $compiledXaml.FullName -Destination $publishedXaml
    }
    foreach ($requiredXaml in @('App.xbf', 'MainWindow.xbf', 'Views\FramePage.xbf')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stageRoot $requiredXaml))) { throw "Missing compiled UI resource: $requiredXaml" }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $stageRoot
    # Ship the release guides, keeping development investigations out of the package.
    $guideRoot = Join-Path $stageRoot 'docs'
    New-Item -ItemType Directory -Path $guideRoot -Force | Out-Null
    foreach ($guide in @('advanced-controls.md', 'building.md', 'connection-setup.md', 'driver-patcher.md', 'live-dashboard.md', 'settings-editor.md', 'stream-controls.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\$guide") -Destination $guideRoot
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\images') -Destination (Join-Path $guideRoot 'images') -Recurse
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md') -Destination $stageRoot }
    $licenseRoot = Join-Path $stageRoot 'Licenses'
    New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Native\vendor\minhook\LICENSE.txt') -Destination (Join-Path $licenseRoot 'MinHook-HDE.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Native\vendor\NVIDIA-LICENSE.txt') -Destination $licenseRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $licenseRoot 'SteamLinkVRSettings-MIT.txt')
    if (Test-Path -LiteralPath $portableRoot) {
        $previousRoot = Join-Path $artifactRoot ('.previous-' + [guid]::NewGuid().ToString('N'))
        Assert-ArtifactPath $portableRoot
        Assert-ArtifactPath $previousRoot
        Move-Item -LiteralPath $portableRoot -Destination $previousRoot
    }
    Assert-ArtifactPath $stageRoot
    Assert-ArtifactPath $portableRoot
    Move-Item -LiteralPath $stageRoot -Destination $portableRoot
    $zip = Join-Path $artifactRoot 'SteamLinkVRSettings-SteamFrame-win-x64.zip'
    Compress-Archive -LiteralPath $portableRoot -DestinationPath $zip -Force
    Write-Output "Portable app: $portableRoot"
    Write-Output "ZIP: $zip"
} finally { Pop-Location }
