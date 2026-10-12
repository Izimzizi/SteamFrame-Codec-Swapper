param([ValidateRange(1,200)][int]$Mbps=100,[ValidateRange(1,230)][int]$PeakMbps=115,[switch]$FixedTarget,[ValidateSet(90,120)][int]$Fps=120)
$ErrorActionPreference='Stop'
if($PeakMbps -lt $Mbps){throw 'Peak must be at least the average target'}
$labRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$labVrRoot='C:\Program Files (x86)\Steam\steamapps\common\SteamVR\bin\win64\'
$labBackend=Join-Path $labRepo 'artifacts\AV1Lab\backend'
if(Get-Process SteamLinkVRSettings -ErrorAction SilentlyContinue){throw 'Close the HEVC monitor app before this isolated trial'}
if(Get-Process vrserver -ErrorAction SilentlyContinue){throw 'Stop SteamVR before this isolated trial'}
Start-Process -FilePath 'C:\Program Files (x86)\Steam\steam.exe' -ArgumentList '-applaunch','250820' -WindowStyle Hidden
$labDeadline=[DateTime]::UtcNow.AddSeconds(25)
while([DateTime]::UtcNow -lt $labDeadline){
 $labServer=Get-Process vrserver -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith($labVrRoot,[StringComparison]::OrdinalIgnoreCase)} | Select-Object -First 1
 if($labServer){
  try {$labFound=$labServer.Modules | Where-Object {$_.ModuleName -eq 'driver_vrlink.dll'}} catch {$labFound=$null}
  if($labFound){
   & (Join-Path $labBackend 'trial_control.exe') $labServer.Id (Join-Path $labBackend 'SteamFrameAV1Backend.dll') install ($Mbps*1000000) ($PeakMbps*1000000) ([int]$FixedTarget.IsPresent) $Fps
   if($LASTEXITCODE -eq 0){Set-Content (Join-Path $labRepo 'artifacts\AV1Lab\steamvr-trial.pid') $labServer.Id;exit 0}
  }
 }
 Start-Sleep -Milliseconds 100
}
throw 'Early AV1 trial installation timed out'
