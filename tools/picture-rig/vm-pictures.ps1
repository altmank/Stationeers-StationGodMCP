<#
.SYNOPSIS
    show picture's in-game proof in a VM pool rig: a rendered client (no -batchmode) hosts a saved test world staged
    after landing (default loom-small, the world library's; P51: no test world plays the lander sequence), with
    StationGod as its only mod, and shots.py takes pictures of the player's vanilla suit (front, side, back) through
    the sg client. The pictures come back in the run's results (out\shots\suit).

.DESCRIPTION
    A thin caller of RigVM's shared client run, host\vm-client-run.ps1, after tools\test-worlds\vm-build-world.ps1:
      PreLaunch: shots.py, sgcall.py, the sg client, the hub and a portable Python go onto the run's results disk;
      OnPoll: once StationGod's pipe is up, shots.py starts in the guest (a hidden task as the rig user);
      Judge: shots.py's done file decides, with replies.json and the pictures (they are on the results disk already);
      OnEnd: on every outcome, the task and any Python it left are removed.
    StationGod's config in the guest sets the test pipe and [Pictures] Folder = {out}\shots, so the game writes the
    pictures straight onto the results disk.

    No machine paths live here (the repo is public): each comes from a parameter or its environment variable.

    Exit (vm-client-run's): 0 pass, 1 fail (reason in result.json), 2 timeout, stall or crash, 3 infra.
#>
param(
    [string] $Owner = 'sg-camera-shot',
    [string] $RigVM = $env:STATIONGOD_RIGVM,
    [string] $RigVMCommit = '7cc64ca',
    [int] $LockWaitMinutes = 120,
    [string] $SettingFrom = $env:SG_RIG_SETTINGS,
    # StationGod's staged package folder (its leaf is the mod's folder name); the only mod.
    [string] $Mod,
    # The saved world's folder in the world library (default: loom-small there); its player is the client's.
    [string] $World = $(if ($env:STATIONGOD_TEST_WORLDS) { Join-Path $env:STATIONGOD_TEST_WORLDS 'loom-small' }),
    [string] $Hub,
    [string] $PythonZip = $env:SG_RIG_PYTHON_ZIP,
    [string] $Pipe = 'StationGodMCP-VM',
    # P46: the run's expected length (the save loaded, about 3 min to the player; the pictures and 3 refusals, about
    # 1 min; the 4096x4096 one the longest); every limit is that plus a margin, not a blanket one.
    [double] $ExpectedMinutes = 6,
    [int] $TimeoutSeconds = 0,
    [int] $PhaseSeconds = 0
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    $env:PSModulePath = (($env:PSModulePath -split ';') | ? { $_ -match '\\WindowsPowerShell\\' }) -join ';'
}
$here = $PSScriptRoot
if ($TimeoutSeconds -le 0) { $TimeoutSeconds = [int]($ExpectedMinutes * 60 * 2) }
if ($PhaseSeconds -le 0) { $PhaseSeconds = [int]($ExpectedMinutes * 60) }
$repo = Split-Path (Split-Path $here)

function Refuse([string] $Why) { Write-Error "refusing: $Why"; exit 3 }
$missing = @(foreach ($n in 'RigVM', 'Mod', 'World', 'Hub', 'PythonZip') { if (-not (Get-Variable $n -ValueOnly)) { $n } })
if ($missing) { Refuse "no $($missing -join ', ') (give the parameter or its environment variable)" }
if ($Pipe -eq 'StationGodMCP') { Refuse 'the live pipe StationGodMCP' }
$clientRun = Join-Path $RigVM 'vm-client-run.ps1'
if (-not (Test-Path $clientRun)) { Refuse "no vm-client-run.ps1 in $RigVM" }
foreach ($p in @($Mod, $World, $Hub, $PythonZip) + @($SettingFrom | ? { $_ })) { if (-not (Test-Path $p)) { Refuse "missing $p" } }
if (-not ((Test-Path "$Mod\About\About.xml") -and ((Get-Content -Raw "$Mod\About\About.xml") -match 'StationGod'))) { Refuse "$Mod is not a StationGod build" }
if (-not (Test-Path (Join-Path $Hub 'StationGodMCP.Server.exe')) -or -not (Test-Path (Join-Path $Hub 'hostfxr.dll'))) { Refuse "$Hub is not a self-contained hub publish" }

$ctxFile = Join-Path ([IO.Path]::GetTempPath()) "sg-pictures-$PID.json"
[ordered]@{ pipe = $Pipe; expectedMin = $ExpectedMinutes; repo = $repo; here = $here; hub = $Hub; pythonZip = $PythonZip
    repoAt = (& git -C $repo rev-parse --short HEAD 2>$null) } | ConvertTo-Json | Set-Content -Encoding UTF8 $ctxFile
$env:SG_PIC_CONTEXT = $ctxFile

$pipeRe = [regex]::Escape($Pipe) -replace '\\', '\\'
$phases = '{"bepinex":"Chainloader startup complete","stationgod":"Pipe name: ' + $pipeRe + '"}'
$bepCfg = '{"net.xceled.stationeers.stationgodmcp.cfg":"[Pipe]\r\n\r\nName = ' + $Pipe + '\r\n\r\n[Pictures]\r\n\r\nFolder = {out}\\shots\r\n"}'
$runArgs = @{
    Mod = 'sg-camera-shot'; Owner = $Owner; Mods = @($Mod); RigVMPin = $RigVMCommit; LockWaitMinutes = $LockWaitMinutes
    Save = $World; GameArgs = '-load {savename}'
    PhasesJson = $phases; DoneGlob = '{out}\done-*'; Require = @('stationgod'); BepCfgJson = $bepCfg
    PreLaunch = "$here\vm\prelaunch.ps1"; OnPoll = "$here\vm\onpoll.ps1"; Judge = "$here\vm\judge.ps1"
    OnEnd = "$here\vm\onend.ps1"
    TimeoutSeconds = $TimeoutSeconds; StallSeconds = $PhaseSeconds
    WatchLogs = @('{res}\logs\shots.log')
    IgnoreErrorRegex = 'StationGodMCP\.LatticeLink|Lattice\.Api'
    PhaseLimitJson = '{"bepinex":' + $PhaseSeconds + ',"stationgod":' + $PhaseSeconds + '}'
}
if ($SettingFrom) { $runArgs.SettingFrom = $SettingFrom }
try { & $clientRun @runArgs; $code = $LASTEXITCODE } finally { Remove-Item $ctxFile -Force -ErrorAction SilentlyContinue }
exit $code
