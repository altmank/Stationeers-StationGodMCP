#Requires -Version 7.5
<#
.SYNOPSIS
    write fabricator's in-game proof in a VM pool rig: a rendered client loads a landed save (P51: never a new game,
    never the lander), with StationGod as its only mod, and fab.py builds a Tool Manufactory and a battery, steps the
    fabricator through its build states and calls write fabricator at each, then makes ItemMKIIWrench.

.DESCRIPTION
    A thin caller of RigVM's shared client run, host\vm-client-run.ps1:
      PreLaunch: fab.py, sgcall.py, the sg client, the hub and a portable Python go onto the run's results disk;
      OnPoll: once StationGod's pipe is up, fab.py starts in the guest (a hidden task as the rig user);
      Judge: fab.py's done file decides, with replies.json and fab.log (collected);
      OnEnd: on every outcome, the task and any Python it left are removed.
    No machine paths live here (the repo is public): each comes from a parameter or its environment variable.
    -GameArgs is what loads the landed save (the world RigVM's client run knows by name); it has no default.

    Exit (vm-client-run's): 0 pass, 1 fail (reason in result.json), 2 timeout, stall or crash, 3 infra.
#>
param(
    [string] $Owner = 'sg-fabricate',
    # RigVM's host folder (vm-client-run.ps1 and rig-lib.ps1); default: the sibling RigVM repo's host\ folder.
    [string] $RigVM,
    # The minimum RigVM commit (main a122e5a, rigvm-min.txt: P72 -CheckScripts, P83, P88 -Item and -ExpectedMinutes).
    [string] $RigVMCommit = 'a122e5a',
    # The board item this run serves (RigVM P88 asks for it): item:StationGodMCP/<id>, the prefix included. No default.
    [string] $Item,
    [int] $LockWaitMinutes = 120,
    # The world library and the world in it (a landed save, P51: never a new game); the game loads it by its folder name.
    [string] $Library = $env:STATIONGOD_TEST_WORLDS,
    [string] $World = 'loom-small',
    [string] $GameArgs = '-load {savename}',
    [string] $SettingFrom = $env:SG_RIG_SETTINGS,
    # Host folder of a self-contained StationGodMCP.Server publish.
    [string] $Hub,
    [string] $PythonZip = $env:SG_RIG_PYTHON_ZIP,
    [string] $Pipe = 'StationGodMCP-VM',
    # P46: the run's expected length (a saved world loading, a floor of frames, two machines, the build states and
    # about 20 calls); every limit is that plus a margin, not a blanket one.
    [double] $ExpectedMinutes = 8,
    [int] $TimeoutSeconds = 0,
    [int] $PhaseSeconds = 0,
    # [Rig] MaxFps for the guest game (a rendered run; lowers host GPU load); 0 = uncapped.
    [int] $MaxFps = 30
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path (Split-Path $here) 'rigvm-host.ps1')
if (-not $RigVM) { $RigVM = Get-DefaultRigVMHost $here }
if ($TimeoutSeconds -le 0) { $TimeoutSeconds = [int]($ExpectedMinutes * 60 * 2) }
if ($PhaseSeconds -le 0) { $PhaseSeconds = [int]($ExpectedMinutes * 60) }
$repo = Split-Path (Split-Path $here)

function Refuse([string] $Why) { Write-Error "refusing: $Why" -ErrorAction Continue; exit 3 }
$missing = @(foreach ($n in 'RigVM', 'Hub', 'PythonZip', 'Library') { if (-not (Get-Variable $n -ValueOnly)) { $n } })
if ($missing) { Refuse "no $($missing -join ', ') (give the parameter or its environment variable)" }
if ($Item -notmatch '^item:StationGodMCP/\S+$') { Refuse "-Item must be item:StationGodMCP/<id> (the prefix is required), got '$Item'" }
if ($Pipe -eq 'StationGodMCP') { Refuse 'the live pipe StationGodMCP' }
$clientRun = Join-Path $RigVM 'vm-client-run.ps1'
if (-not (Test-Path $clientRun)) { Refuse "no vm-client-run.ps1 in $RigVM" }
foreach ($p in @($Hub, $PythonZip) + @($SettingFrom | ? { $_ })) { if (-not (Test-Path $p)) { Refuse "missing $p" } }
# The package must be the build under test: package-build.txt names HEAD, the tree is clean and was when it built.
$package = Join-Path $repo 'package'
$stamp = @(if (Test-Path (Join-Path $repo 'package-build.txt')) { Get-Content (Join-Path $repo 'package-build.txt') })
$head = "$(& git -C $repo rev-parse HEAD 2>$null)".Trim()
if (-not (Test-Path (Join-Path $package 'StationGodMCP.dll'))) { Refuse 'no staged package: run build.ps1' }
if ($stamp.Count -lt 2 -or $stamp[0].Trim() -ne $head -or $stamp[1].Trim() -ne 'clean' -or (& git -C $repo status --porcelain 2>$null)) { Refuse 'package-build.txt does not name HEAD built from a clean tree: run build.ps1 on a clean tree' }
$worldSave = Join-Path $Library $World
if (-not (Test-Path (Join-Path $worldSave "$World.save"))) { Refuse "no $World.save in $worldSave" }
if (-not (Test-Path (Join-Path $Hub 'StationGodMCP.Server.exe')) -or -not (Test-Path (Join-Path $Hub 'hostfxr.dll'))) { Refuse "$Hub is not a self-contained hub publish" }

# The mod, staged under the folder name StationGodMCP.
$stage = Join-Path ([IO.Path]::GetTempPath()) ('sg-fab-stage-' + [guid]::NewGuid().ToString('N'))
$mod = Join-Path $stage 'mods\StationGodMCP'
New-Item -ItemType Directory -Force (Split-Path $mod) | Out-Null
Copy-Item -Recurse $package $mod

$ctxFile = Join-Path ([IO.Path]::GetTempPath()) "sg-fab-$PID.json"
[ordered]@{ pipe = $Pipe; expectedMin = $ExpectedMinutes; repo = $repo; here = $here; hub = $Hub; pythonZip = $PythonZip
    repoAt = (& git -C $repo rev-parse --short HEAD 2>$null) } | ConvertTo-Json | Set-Content -Encoding utf8BOM $ctxFile
$env:SG_FAB_CONTEXT = $ctxFile

$pipeRe = [regex]::Escape($Pipe) -replace '\\', '\\'
$phases = '{"bepinex":"Chainloader startup complete","stationgod":"Pipe name: ' + $pipeRe + '"}'
$bepCfg = '{"net.xceled.stationeers.stationgodmcp.cfg":"[Pipe]\r\n\r\nName = ' + $Pipe + '\r\n\r\n[Rig]\r\n\r\nMaxFps = ' + $MaxFps + '\r\n"}'
$runArgs = @{
    Mod = 'sg-fabricate'; Owner = $Owner; Mods = @($mod); Save = $worldSave; RigVMPin = $RigVMCommit; LockWaitMinutes = $LockWaitMinutes
    GameArgs = $GameArgs     # last: on launch every later '-' word starts another command
    PhasesJson = $phases; DoneGlob = '{out}\done-*'; Require = @('stationgod'); BepCfgJson = $bepCfg
    PreLaunch = "$here\vm\prelaunch.ps1"; OnPoll = "$here\vm\onpoll.ps1"; Judge = "$here\vm\judge.ps1"
    OnEnd = "$here\vm\onend.ps1"
    Item = $Item; ExpectedMinutes = [int][math]::Ceiling($ExpectedMinutes)
    CheckScripts = @("$here\vm", "$here\fab.py", (Join-Path (Split-Path $here) 'silo-rig\sgcall.py'))
    TimeoutSeconds = $TimeoutSeconds; StallSeconds = $PhaseSeconds
    WatchLogs = @('{res}\logs\fab.log')
    IgnoreErrorRegex = 'StationGodMCP\.LatticeLink|Lattice\.Api'
    PhaseLimitJson = '{"bepinex":' + $PhaseSeconds + ',"stationgod":' + $PhaseSeconds + '}'
}
if ($SettingFrom) { $runArgs.SettingFrom = $SettingFrom }
# P92: RigVM starts under Windows PowerShell 5.1, never in-process from this pwsh 7 script.
try { $code = Invoke-RigVMEntry -Script $clientRun -Arguments $runArgs } finally { Remove-Item $ctxFile -Force -ErrorAction SilentlyContinue; Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue }
exit $code
