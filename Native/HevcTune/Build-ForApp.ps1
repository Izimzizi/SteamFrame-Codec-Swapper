# Build diagnostics against the exact helper shipped with the app being used.
# Historical experiment tools remain frozen; never use them to restore normal play.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSCommandPath
$repoDirectory = [IO.Path]::GetFullPath((Join-Path $projectDirectory '..\..'))
$appDirectoryPath = [IO.Path]::GetFullPath($AppDirectory)
$helper = Join-Path $appDirectoryPath 'Native\SteamFrameEncoderProbe.dll'
foreach ($required in @((Join-Path $appDirectoryPath 'SteamLinkVRSettings.dll'), $helper)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Incomplete app build: $required" }
}
$hash = (Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash.ToLowerInvariant()
# Keep the tool's parent short: the probe adds another full hash and a status
# filename below it, and SteamVR may not opt into Windows long-path handling.
# The complete SHA-256 is still checked below and recorded in the receipt.
$output = Join-Path $repoDirectory ('artifacts\AppHevcTune\' + $hash.Substring(0, 12))
& dotnet build (Join-Path $projectDirectory 'HevcTune.csproj') --configuration Release --output $output "-p:ProbeDll=$helper" --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Matching diagnostic tool build failed.' }
$copiedHelper = Join-Path $output 'Native\SteamFrameEncoderProbe.dll'
if ((Get-FileHash -LiteralPath $helper).Hash -ne $hash -or (Get-FileHash -LiteralPath $copiedHelper).Hash -ne $hash) {
    throw 'The app changed during the build, or its helper was not copied exactly. Do not run this tool; rebuild for the current app.'
}
$receipt = [pscustomobject]@{
    AppDirectory = $appDirectoryPath
    Executable = Join-Path $output 'HevcTune.exe'
    HelperSha256 = $hash
    BuiltAtUtc = [DateTime]::UtcNow.ToString('o')
}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'app-helper-receipt.json') -Encoding UTF8
$receipt | ConvertTo-Json
