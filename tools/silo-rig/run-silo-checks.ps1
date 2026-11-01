<#
.SYNOPSIS
  One silo check run on a test rig instance: take the game lock, reset the instance to its prepared save, run
  silo_checks.py against its pipe, then stop the instance and release the lock, even when a step fails.

.DESCRIPTION
  The instance must already have the StationGod build under test in its own mods folder, and HubPath the sidecar from
  the same build (the portable zip in package\Sidecar, unpacked). Refuses to start while another rocketstation process
  runs outside the instance's folder (someone is playing). Never touches a live server or a player's saves: the
  instance tools only reset the instance's own save copy.

.EXAMPLE
  .\run-silo-checks.ps1 -Instance sgt5 -Pipe StationGodMCP-SGT5 -InstanceTools <scoreboard>\instance `
      -InstanceRoot <rigs>\StationGod\sgt5 -LockScript <workspace>\bt-game-lock.ps1 -HubPath <unpacked sidecar>
#>
param(
    [Parameter(Mandatory)][string]$Instance,
    [Parameter(Mandatory)][string]$Pipe,
    [Parameter(Mandatory)][string]$InstanceTools,
    [Parameter(Mandatory)][string]$InstanceRoot,
    [Parameter(Mandatory)][string]$LockScript,
    [Parameter(Mandatory)][string]$HubPath,
    [string]$Owner = 'silo-checks'
)
$ErrorActionPreference = 'Stop'
if ($Pipe -eq 'StationGodMCP') { throw 'refusing the live pipe name' }
$others = @(Get-Process rocketstation* -ErrorAction SilentlyContinue | Where-Object { $_.Path -notlike "$InstanceRoot\*" })
if ($others.Count -gt 0) { throw "a game is running outside $InstanceRoot (PID $($others.Id -join ', ')); not starting" }

& $LockScript acquire -Owner $Owner
if ($LASTEXITCODE -ne 0) { throw 'could not take the game lock' }
$failed = $null
try {
    & (Join-Path $InstanceTools 'reset.ps1') $Instance -NoWait
    & py -3.12 (Join-Path $PSScriptRoot 'silo_checks.py') --pipe $Pipe --hub $HubPath
    $failed = $LASTEXITCODE
}
finally {
    & (Join-Path $InstanceTools 'stop.ps1') $Instance
    & $LockScript release -Owner $Owner
}
if ($failed -ne 0) { throw "$failed silo check(s) failed" }
