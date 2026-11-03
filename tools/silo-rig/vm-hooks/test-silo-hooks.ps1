# powershell -NoProfile -File tools\silo-rig\vm-hooks\test-silo-hooks.ps1   (also under pwsh)
# Runs the vm-client-run hooks against faked rig-lib calls (Invoke-Rig records its calls and runs nothing in any guest
# or here), the way rig-lib and vm-client-run run them. Exit 0 when every assertion holds, else the number that failed.
$ErrorActionPreference = 'Stop'
$script:failed = 0
function Assert([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "ok    $what" } else { Write-Host "FAIL  $what"; $script:failed++ }
}
$hooks = $PSScriptRoot

# ---- fakes ----
$script:calls = @()
$script:reply = $null
function Invoke-Rig([scriptblock] $Block, [object[]] $ArgumentList = @()) {
    $script:calls += , [pscustomobject]@{ Block = "$Block"; Args = @($ArgumentList) }
    $script:reply
}
function Rig-Log([string] $t) { }
$script:completed = $null
function Complete-Rig([string] $Status, [string] $Reason) { $script:completed = [pscustomobject]@{ Status = $Status; Reason = $Reason } }

# ---- PreLaunch: records the run's expected length and the package (P46) ----
function Measure-RigStep([string] $Name, [scriptblock] $Block) { }
function Add-RigCollect([string] $Guest, [string] $Name) { }
function Stop-Rig([string] $Status, [string] $Reason) { throw "stop: $Reason" }
$stageDir = Join-Path ([IO.Path]::GetTempPath()) ('sg-hooks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force "$stageDir\tools\silo-rig" | Out-Null; Set-Content "$stageDir\tools\silo-rig\silo_checks.py" ''
try {
    $env:SG_SILO_STAGE = $stageDir; $env:SG_SILO_EXPECTED_MIN = '6'; $env:SG_SILO_PACKAGE = 'abc123'
    $r = [pscustomobject]@{ RunId = 'run0'; Info = [ordered]@{} }; $gRes = 'C:\res\run0'
    . (Join-Path $hooks 'silo-prelaunch.ps1')
    Assert ($r.Info.expectedMin -eq 6 -and $r.Info.package -eq 'abc123') 'PreLaunch writes info.expectedMin and info.package'
}
finally { Remove-Item -Recurse -Force $stageDir -ErrorAction SilentlyContinue }

# ---- OnEnd: rig-lib's finally runs it as & { . <path> }, on every outcome ----
$script:RigRun = [pscustomobject]@{ RunId = '20261102-010203-rig2' }
$script:calls = @()
& { . (Join-Path $hooks 'silo-onend.ps1') }
Assert ($script:calls.Count -eq 1) 'OnEnd makes one guest call'
Assert ($script:calls[0].Args[0] -eq 'SG-Silo-20261102-010203-rig2') 'OnEnd names the run''s task'
Assert ($script:calls[0].Block -match 'Stop-ScheduledTask' -and $script:calls[0].Block -match 'Unregister-ScheduledTask') 'OnEnd stops and unregisters it'

# ---- OnPoll: vm-client-run dot-sources it at each poll; it starts the checks once, after the stationgod phase ----
$global:SgSiloChecksRun = $null
$r = [pscustomobject]@{ RunId = 'run1'; PhaseAt = [ordered]@{ bepinex = 1 } }
$gRes = 'C:\GuestRes\run1'; $gOut = "$gRes\out"
$env:SG_SILO_PIPE = 'StationGodMCP-VM'; $env:SG_SILO_HUB_FILE = 'StationGodMCP.Server.exe'; $env:SG_SILO_GUEST_PYTHON = 'py'; $env:SG_SILO_WORLD_TIMEOUT = '420'
$script:calls = @()
$phase = 'bepinex'; . (Join-Path $hooks 'silo-onpoll.ps1')
Assert ($script:calls.Count -eq 0) 'OnPoll waits for StationGod''s pipe'
$r.PhaseAt.stationgod = 2
$phase = 'stationgod'; . (Join-Path $hooks 'silo-onpoll.ps1')
Assert ($script:calls.Count -eq 1) 'OnPoll starts the checks once the pipe is up'
Assert ($script:calls[0].Args[0] -eq 'SG-Silo-run1' -and $script:calls[0].Args[3] -eq 'StationGodMCP-VM') 'the task and the pipe'
Assert ($script:calls[0].Args[2] -eq "$gRes\sg" -and $script:calls[0].Args[4] -eq $gOut) 'the sg folder and the done folder'
Assert ($script:calls[0].Block -match 'Register-ScheduledTask' -and $script:calls[0].Block -match 'WindowStyle Hidden') 'a hidden scheduled task'
Assert ($script:calls[0].Args[8] -eq 420) 'the world timeout goes to the guest'
. (Join-Path $hooks 'silo-onpoll.ps1')
Assert ($script:calls.Count -eq 1) 'not started twice'
$global:SgSiloChecksRun = $null

# ---- Judge: dot-sourced at the end; the done file decides ----
$r = [pscustomobject]@{ RunId = 'run1'; Info = [ordered]@{} }
$script:reply = [pscustomobject]@{ name = 'done-ok'; text = ((@(1..14 | % { "PASS  check $_" }) + '14 checks, 0 failed') -join "`n") + "`n" }
. (Join-Path $hooks 'silo-judge.ps1')
Assert ($script:completed.Status -eq 'pass') 'done-ok: pass'
Assert (@($r.Info.checks).Count -eq 15) 'every line in info.checks'
Assert (-not ($script:calls | ? { $_.Block -match 'Unregister-ScheduledTask' })) 'the Judge leaves the task to OnEnd'
$script:reply = [pscustomobject]@{ name = 'done-ok'; text = ((@(1..13 | % { "PASS  check $_" }) + 'SKIP  s  -- why' + '14 checks, 0 failed') -join "`n") }
. (Join-Path $hooks 'silo-judge.ps1')
Assert ($script:completed.Status -eq 'fail' -and $script:completed.Reason -like 'skipped: SKIP  s*') 'a SKIP: fail'
$script:reply = [pscustomobject]@{ name = 'done-fail'; text = "FAIL  c  -- got x`n1 checks, 1 failed" }
. (Join-Path $hooks 'silo-judge.ps1')
Assert ($script:completed.Status -eq 'fail' -and $script:completed.Reason -like '*FAIL  c*') 'done-fail: fail, naming the FAIL'
$script:reply = $null
. (Join-Path $hooks 'silo-judge.ps1')
Assert ($script:completed.Status -eq 'fail' -and $script:completed.Reason -like '*no done file*') 'no done file: fail'

Write-Host "$script:failed failed"
exit $script:failed
