#Requires -Version 7.5
<#
.SYNOPSIS
    Builds one test world in a VM pool rig: a client starts a new Lunar Creative world itself (it hosts the world and
    is the player, no dedicated server), build_world.py builds the fixtures inside the guest through StationGod, and
    the save comes back and is stored in the world library.

.DESCRIPTION
    A thin caller of RigVM's shared client run, host\vm-client-run.ps1 (rig-lib: pool lock, the pool picks the rig, transcript,
    result.json, exit codes, results disk, client copy, settings without host values, heartbeat watch). What is this
    world build's own lives in four hooks beside this script (vm\):
      PreLaunch: the builder (this folder, tools\silo-rig\sgcall.py, clients\python), the hub and a portable Python
        go onto the run's results disk in the guest (nothing is installed in the guest);
      OnPoll: once StationGod's pipe is up, build_world.py starts in the guest (a hidden task as the rig user); it
        waits for the world's player itself and writes done-ok or done-fail;
      Judge: the done file decides; the save (and manifest) come back over PS Direct and `build_world.py store` puts
        them in the library;
      OnEnd: on every outcome, the builder's task and any Python it left in the guest are removed.
    The client starts with the game's own launch command `-newgame Lunar Creative` (Util.Commands.NewGameCommand,
    IsLaunchCmd), so StationGod runs as host (role host, dedicated false) and the client's body is the player.

    No machine paths live here (the repo is public): each comes from a parameter or its environment variable, and
    the script refuses when one is missing. The dedicated-server path (README.md) still works; this is the other one.

    Exit (vm-client-run's): 0 pass (built, saved, stored), 1 fail (the builder failed: reason in result.json), 2
    timeout, stall or crash, 3 infra.

.PARAMETER World
    sg-silo, sg-minimal or sg-gate; or sg-smoke, the first run on a rig: a new world, StationGod's welcome (role,
    dedicated) and player, a save as sg-smoke copied back. Nothing is built and nothing goes into the library.
.PARAMETER Check
    Load the stored world from the library (-load) instead of building one: pass when StationGod answers server info
    and the loaded world's thing count (find thing total) equals the save file's. Nothing is changed or stored.
.PARAMETER RigVM
    RigVM's host folder, holding vm-client-run.ps1 and rig-lib.ps1 (default: the sibling RigVM repo's host folder).
.PARAMETER RigVMCommit
    The minimum RigVM commit (default 31b4f68, RigVM main: -WorldBuild, RigGuard, the saved player, -CheckScripts, -Item), passed as vm-client-run's -RigVMPin: rig-lib's Assert-RigVMMinimum
    ends the run as infra when RigVM doesn't contain it, and result.json records the pin and the actual HEAD. The
    pool picks the rig (P45: callers never name one).
.PARAMETER LockWaitMinutes
    How long to wait for a rig, queue included (default 120, vm-client-run's).
.PARAMETER Library
    The world library (default: STATIONGOD_TEST_WORLDS). Not needed for sg-smoke.
.PARAMETER SettingFrom
    A host setting.xml to start the client's settings from (default: SG_RIG_SETTINGS, else vm-client-run's own).
.PARAMETER Mods
    Host folders of the mod builds to load (each leaf is the mod's folder name). StationGod's is required; no other
    mod is needed (every test world is vanilla plus StationGod).
.PARAMETER Hub
    Host folder of the StationGodMCP.Server build. A self-contained publish
    (`dotnet publish src/StationGodMCP.Server -r win-x64 --self-contained`) needs no dotnet in the guest; otherwise
    give -DotnetZip.
.PARAMETER PythonZip
    A zip of a Python 3.12 with its standard library (the embeddable zip, or a stdlib-only zip of an install; default:
    SG_RIG_PYTHON_ZIP), unzipped onto the run's results disk.
.PARAMETER DotnetZip
    A .NET 8 runtime zip (default: SG_RIG_DOTNET_ZIP), for a framework-dependent hub only.
.PARAMETER Pipe
    StationGod's pipe name in the guest (never the live StationGodMCP).
#>
param(
    [Parameter(Mandatory)] [ValidateSet('sg-smoke', 'sg-silo', 'sg-minimal', 'sg-gate', 'sg-cost-large')] [string] $World,
    # Load the library's stored <World> instead of building it: StationGod answers and the thing count is the save's.
    [switch] $Check,
    [string] $Owner = 'sg-test-worlds-build',
    [string] $RigVM,
    [string] $RigVMCommit = '31b4f68',
    # The board item this run serves (RigVM P88 asks for it): item:StationGodMCP/<id>, the prefix included. No default.
    [string] $Item,
    [int] $LockWaitMinutes = 120,
    [string] $Library = $env:STATIONGOD_TEST_WORLDS,
    [string] $SettingFrom = $env:SG_RIG_SETTINGS,
    # A comma list when passed through run-hidden.
    [string[]] $Mods = @(),
    [string] $Hub,
    [string] $PythonZip = $env:SG_RIG_PYTHON_ZIP,
    [string] $DotnetZip = $env:SG_RIG_DOTNET_ZIP,
    [string] $Pipe = 'StationGodMCP-VM',
    [double] $Lift = 12.0,
    # Limits (P46): each run states its expected length, and its limits are that plus a margin ($RUN_LIMITS below,
    # from the rig runs: smoke 1.5 min pipe to done, sg-silo 4 min lock to stored). 0 takes the table's value. The
    # build runs in the stationgod phase (runs reach bepinex, stationgod, done); the builder's build.log is watched,
    # one CALL line per StationGod call, so a healthy build always shows life.
    [int] $TimeoutSeconds = 0,
    [int] $StallSeconds = 0,
    [int] $PhaseSeconds = 0,
    [switch] $Dxdiag
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path (Split-Path $here) 'rigvm-host.ps1')
if (-not $RigVM) { $RigVM = Get-DefaultRigVMHost $here }
# Minutes: expected length, the build (or load) phase limit, the stall limit. The timeout is the stall plus 3 min.
$RUN_LIMITS = @{
    # The builders wait for the lander first (up to 7 min; P46: tightened once a run measures it).
    'sg-smoke' = @(3, 5, 6); 'sg-minimal' = @(9, 12, 14); 'sg-silo' = @(11, 15, 17); 'sg-gate' = @(11, 15, 17)
    'check' = @(8, 12, 15)
}
$lim = if ($Check) { $RUN_LIMITS['check'] } elseif ($RUN_LIMITS[$World]) { $RUN_LIMITS[$World] } else { $RUN_LIMITS['sg-silo'] }
$expectedMin = $lim[0]
if (-not $PhaseSeconds) { $PhaseSeconds = $lim[1] * 60 }
if (-not $StallSeconds) { $StallSeconds = $lim[2] * 60 }
# The stall rule (no log growth at all) must not end a quiet build before its phase limit does.
if ($StallSeconds -lt $PhaseSeconds) { Write-Host "StallSeconds $StallSeconds raised to the phase limit $PhaseSeconds"; $StallSeconds = $PhaseSeconds }
if (-not $TimeoutSeconds) { $TimeoutSeconds = $StallSeconds + 180 }
$limitsText = "expected $expectedMin min; phase limit $PhaseSeconds s, stall $StallSeconds s, timeout $TimeoutSeconds s"
Write-Host $limitsText
$repo = Split-Path (Split-Path $here)
$Mods = @($Mods | % { $_ -split ',' } | ? { $_ })
$NeedMods = @{}   # every test world is vanilla plus StationGod (LU: StationGod's testing depends on no other mod)

# Refusals before the run starts (no transcript yet: nothing has started).
function Refuse([string] $Why) { Write-Error "refusing: $Why" -ErrorAction Continue; exit 3 }
$missing = @(foreach ($n in @('RigVM', 'Hub', 'PythonZip') + @(if ($World -ne 'sg-smoke') { 'Library' })) { if (-not (Get-Variable $n -ValueOnly)) { $n } })
if ($missing) { Refuse "no $($missing -join ', ') (give the parameter or its environment variable; see Get-Help $PSCommandPath)" }
if ($Pipe -eq 'StationGodMCP') { Refuse 'the live pipe StationGodMCP' }
if ($Item -notmatch '^item:StationGodMCP/\S+$') { Refuse "-Item must be item:StationGodMCP/<id> (the prefix is required), got '$Item'" }
$clientRun = Join-Path $RigVM 'vm-client-run.ps1'
if (-not (Test-Path $clientRun)) { Refuse "no vm-client-run.ps1 in $RigVM (RigVM 31b4f68 or later)" }
foreach ($p in @($Hub, $PythonZip) + $Mods + @($SettingFrom | ? { $_ })) { if (-not (Test-Path $p)) { Refuse "missing $p" } }
if (-not (Test-Path (Join-Path $Hub 'StationGodMCP.Server.dll'))) { Refuse "no StationGodMCP.Server.dll in $Hub" }
# Self-contained: the exe carries its runtime (hostfxr.dll beside it); else dotnet runs the dll.
$hubFile = if ((Test-Path (Join-Path $Hub 'StationGodMCP.Server.exe')) -and (Test-Path (Join-Path $Hub 'hostfxr.dll'))) { 'StationGodMCP.Server.exe' } else { 'StationGodMCP.Server.dll' }
if ($hubFile -like '*.dll' -and -not ($DotnetZip -and (Test-Path $DotnetZip))) { Refuse "the hub in $Hub is not self-contained and there is no -DotnetZip" }
if (-not @($Mods | ? { (Test-Path "$_\About\About.xml") -and ((Get-Content -Raw "$_\About\About.xml") -match 'StationGod') })) { Refuse 'no StationGod build among -Mods' }
if ($World -eq 'sg-cost-large' -and -not $Check) { Refuse 'sg-cost-large is made from sg-silo by make_large_world.py; give -Check to load it' }
$savedFolder = ''; $expect = 0
if ($Check) {
    $libSave = Join-Path $Library "$World\$World.save"
    if (-not (Test-Path $libSave)) { Refuse "no $World.save in $(Join-Path $Library $World) (store the world first)" }
    $expect = [int](& (Get-Command py).Source -3.12 -c "import sys; sys.path.insert(0, sys.argv[1]); import twlib; print(twlib.counts(twlib.read_world_xml(sys.argv[2]))['findable'])" $here $libSave)
    if (-not $expect) { Refuse "could not count the things in $libSave" }
}
$names = @($Mods | % { Split-Path $_ -Leaf })
foreach ($need in @(if (-not $Check) { $NeedMods[$World] })) { if ($need -and $names -notcontains $need) { Refuse "$World needs the mod $need in -Mods" } }

# The hooks read what they need from one context file (they run dot-sourced inside vm-client-run's run body).
$ctxFile = Join-Path ([IO.Path]::GetTempPath()) "sg-test-worlds-$PID.json"
[ordered]@{
    world = $World; check = [bool]$Check; expectedMin = $expectedMin; limits = $limitsText; expect = $expect; pipe = $Pipe; lift = $Lift; library = $Library; repo = $repo; here = $here
    hub = $Hub; hubFile = $hubFile; pythonZip = $PythonZip; dotnetZip = $(if ($hubFile -like '*.dll') { $DotnetZip } else { '' })
    repoAt = (& git -C $repo rev-parse --short HEAD 2>$null)
} | ConvertTo-Json | Set-Content -Encoding utf8BOM $ctxFile
$env:SG_TW_CONTEXT = $ctxFile

$pipeRe = [regex]::Escape($Pipe) -replace '\\', '\\'
$phases = '{"bepinex":"Chainloader startup complete","stationgod":"Pipe name: ' + $pipeRe + '","newgame":"Creating new world ''Lunar'' with difficulty ''Creative''","world":"Started new game in world|World Loaded in"}'
if ($Check) { $phases = '{"bepinex":"Chainloader startup complete","stationgod":"Pipe name: ' + $pipeRe + '","world":"World Loaded in"}' }
$bepCfg = '{"net.xceled.stationeers.stationgodmcp.cfg":"[Pipe]\r\n\r\nName = ' + $Pipe + '\r\n"}'
$runArgs = @{
    Mod = 'sg-test-worlds'; Owner = $Owner; Mods = $Mods; RigVMPin = $RigVMCommit; LockWaitMinutes = $LockWaitMinutes
    GameArgs = '-newgame Lunar Creative'     # last: on launch every later '-' word starts another command
    PhasesJson = $phases; DoneGlob = '{out}\done-*'; Require = @('stationgod'); BepCfgJson = $bepCfg
    PreLaunch = "$here\vm\prelaunch.ps1"; OnPoll = "$here\vm\onpoll.ps1"; Judge = "$here\vm\judge.ps1"
    OnEnd = "$here\vm\onend.ps1"
    Item = $Item; ExpectedMinutes = [int]$expectedMin
    CheckScripts = @("$here\vm", "$here\build_world.py", "$here\builder.py", "$here\twlib.py")
    TimeoutSeconds = $TimeoutSeconds; StallSeconds = $StallSeconds; Dxdiag = [bool]$Dxdiag
    WatchLogs = @('{res}\logs\build.log')
    # StationGod 2.16.1 without Lattice logs a caught TypeLoadException for its Lattice link (LatticeLink types that
    # name Lattice.Api) and carries on; RigVM's fatal patterns would end the run on it (sg-silo run, 11-02 01:57).
    IgnoreErrorRegex = 'StationGodMCP\.LatticeLink|Lattice\.Api'
    PhaseLimitJson = '{"bepinex":600,"stationgod":' + $PhaseSeconds + ',"newgame":' + $PhaseSeconds + ',"world":' + $PhaseSeconds + '}'
}
if ($SettingFrom) { $runArgs.SettingFrom = $SettingFrom }
# P51: only a world builder may start from -newgame; a -Check run loads a stored, landed world.
if (-not $Check) { $runArgs.WorldBuild = $true }
# vm-client-run ends with rig-lib's `exit <code>`, which ends that script only: pass its code on.
try {
    # A check loads the stored save by its folder name (vm-client-run: '-load {savename}', never a path). The library is
    # flat; the game loads <saves>\<world>\<world>.save, so that folder shape is built here, at staging time, inside the
    # try so that the finally removes it on every exit.
    if ($Check) {
        $savedFolder = Join-Path ([IO.Path]::GetTempPath()) ("sg-tw-stage-" + [guid]::NewGuid().ToString('N') + "\$World")
        New-Item -ItemType Directory -Force $savedFolder | Out-Null
        Copy-Item $libSave (Join-Path $savedFolder "$World.save")
        $runArgs.Save = $savedFolder; $runArgs.GameArgs = '-load {savename}'
    }
    # RigVM runs in its own pwsh child process (Invoke-RigVMEntry), so its exit ends that child, not this script.
    $code = Invoke-RigVMEntry -Script $clientRun -Arguments $runArgs
} finally {
    Remove-Item $ctxFile -Force -ErrorAction SilentlyContinue
    if ($savedFolder) { Remove-Item (Split-Path $savedFolder) -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $code
