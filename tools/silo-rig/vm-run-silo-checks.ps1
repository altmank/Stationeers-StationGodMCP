#Requires -Version 7.5
<#
.SYNOPSIS
    The silo checks on a VM rig: a client loads a copy of the sg-silo test world itself (it hosts the world and is
    the player, no dedicated server), silo_checks.py runs inside the guest through StationGod, and its PASS, FAIL and
    SKIP lines come back in result.json.

.DESCRIPTION
    A thin caller of RigVM's shared host\vm-client-run.ps1 (beside rig-lib.ps1): that runner owns the pool lock (the
    pool allocates the rig; callers never name one), the client copy, the results disk, the game start, the watch and
    the exit code. This script first refuses what must not run, then stages what the checks need, then hands over
    with four hooks (vm-hooks\): -PreLaunch copies the checks into the guest, -OnPoll starts them once StationGod's
    pipe is up, -Judge passes or fails the run from their done file, and -OnEnd, run on every outcome (timeout, stall
    and crash too), removes the checks' scheduled task from the guest.

    Refused before anything starts:
    - a package that is not the build under test: package-build.txt (beside package\, written by build.ps1) must name
      HEAD with a clean tree, and the tree must still be clean;
    - no sg-silo world (<Library>\sg-silo\sg-silo.save and fixtures.json); the library entry is only read,
      vm-client-run copies the save into the run;
    - the live pipe name.
    vm-client-run refuses (infra) a RigVM whose HEAD does not contain -RigVMCommit (-RigVMPin, Assert-RigVMMinimum),
    and writes the RigVM HEAD the run used into result.json (info.rigVM).

    The game loads the world with its own launch command `-load {savename}` (Util.Commands.LoadGameCommand, IsLaunchCmd
    true, registered as "load" in Util.Commands.CommandLine): the save's folder name, looked up in the run's saves folder
    (FileCommand.GetStationDirectory), which holds the one .save. Not a full folder path: LoadGameCommand sends an
    existing folder to FileCommand.LoadAtFullPath, which wants a .save file ("Could not find save at path."), and the
    client sits at the menu (this script's first run, 11-02; vm-client-run notes the same). No world name follows it:
    with one, a missing save would start a new game instead (LoadGameCommand.LoadGame).

    Expected length (P46): Get-SiloExpectedMinutes from the lock, written to result.json (info.expectedMin); the
    timeout is that plus a margin (Get-SiloTimeoutSeconds). If the world has not loaded within Get-SiloWorldTimeout,
    silo_checks.py fails at once with "world never loaded".

    No machine paths live here (the repo is public): each comes from a parameter or its environment variable.

    Exit (vm-client-run's): 0 pass (no check failed), 1 fail (a check failed: the lines are in result.json
    info.checks), 2 timeout, stall or crash, 3 infra or a refusal here.

.PARAMETER RigLib
    RigVM's host\rig-lib.ps1 (default: host\rig-lib.ps1 in the sibling RigVM repo); vm-client-run.ps1 is taken from the same folder.
.PARAMETER RigVMCommit
    The minimum RigVM commit, passed as vm-client-run's -RigVMPin: RigVM's HEAD must contain it. Default 0f8c2af (RigVM main)
    (callers never name a rig, the OnEnd hook, Assert-RigVMMinimum, the stuck rule, -PhaseLimitJson, -WatchLogs).
.PARAMETER StallSeconds
    rig-lib's stall rule: no growth in any watched log for this long ends the run. Default 900. The checks' own log is
    watched (-WatchLogs), and the per-phase stuck limits and phases are Get-SiloPhaseLimits and Get-SiloPhases
    (vm-hooks\silo-vm-lib.ps1).
.PARAMETER Library
    The world library holding sg-silo (default: STATIONGOD_TEST_WORLDS).
.PARAMETER SettingsTemplate
    A host setting.xml for vm-client-run's -SettingFrom (default: SG_RIG_SETTINGS, else vm-client-run's own default).
.PARAMETER Hub
    Host folder of the StationGodMCP.Server build. A self-contained publish needs no dotnet in the guest; otherwise
    give -DotnetZip.
.PARAMETER PythonZip
    Host path of the Python 3.12 embeddable zip (default: SG_RIG_PYTHON_ZIP), unzipped into the run's folder in the
    guest. Without it the guest's own Python (-GuestPython) is used.
.PARAMETER DotnetZip
    Host path of a .NET 8 runtime zip (default: SG_RIG_DOTNET_ZIP); needed only for a framework-dependent hub.
.PARAMETER GuestPython
    The guest's own Python 3.12 command when there is no -PythonZip (default: SG_RIG_GUEST_PYTHON, else py).
.PARAMETER Pipe
    StationGod's pipe name in the guest (never the live StationGodMCP).
#>
param(
    [string] $Owner = 'sg-silo-checks',
    [string] $RigLib,
    [string] $RigVMCommit = '0f8c2af',
    # The board item this run serves (RigVM P88 asks for it): item:StationGodMCP/<id>, the prefix included. No default.
    [string] $Item,
    [string] $Library = $env:STATIONGOD_TEST_WORLDS,
    [string] $SettingsTemplate = $env:SG_RIG_SETTINGS,
    [string] $Hub,
    [string] $PythonZip = $env:SG_RIG_PYTHON_ZIP,
    [string] $DotnetZip = $env:SG_RIG_DOTNET_ZIP,
    [string] $GuestPython = $(if ($env:SG_RIG_GUEST_PYTHON) { $env:SG_RIG_GUEST_PYTHON } else { 'py' }),
    [string] $Pipe = 'StationGodMCP-VM',
    [int] $TimeoutSeconds = 0,
    [int] $StallSeconds = 0,
    [switch] $Dxdiag
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path (Split-Path $here) 'rigvm-host.ps1')
if (-not $RigLib) { $RigLib = Join-Path (Get-DefaultRigVMHost $here) 'rig-lib.ps1' }
$repo = Split-Path (Split-Path $here)
$World = 'sg-silo'
. (Join-Path $here 'vm-hooks\silo-vm-lib.ps1')
if ($StallSeconds -le 0) { $StallSeconds = $script:SiloStallSeconds }
if ($TimeoutSeconds -le 0) { $TimeoutSeconds = Get-SiloTimeoutSeconds }
function Refuse([string] $why) { Write-Error "refusing: $why" -ErrorAction Continue; exit 3 }

# ---- refusals: nothing has started ----
$missing = @(foreach ($n in 'RigLib', 'Hub', 'Library') { if (-not (Get-Variable $n -ValueOnly)) { $n } })
if ($missing) { Refuse "no $($missing -join ', ') (give the parameter or its environment variable; see Get-Help $PSCommandPath)" }
if ($Pipe -eq 'StationGodMCP') { Refuse 'the live pipe StationGodMCP' }
if ($Item -notmatch '^item:StationGodMCP/\S+$') { Refuse "-Item must be item:StationGodMCP/<id> (the prefix is required), got '$Item'" }
$clientRun = Join-Path (Split-Path $RigLib) 'vm-client-run.ps1'
if (-not (Test-Path $RigLib) -or -not (Test-Path $clientRun)) { Refuse "no rig-lib.ps1 and vm-client-run.ps1 at $(Split-Path $RigLib)" }
$package = Join-Path $repo 'package'
$stampFile = Join-Path $repo 'package-build.txt'
$why = Get-PackageRefusal -Stamp $(if (Test-Path -LiteralPath $stampFile) { @(Get-Content -LiteralPath $stampFile) } else { @() }) `
    -Head "$(& git -C $repo rev-parse HEAD 2>$null)".Trim() -Dirty ([bool](& git -C $repo status --porcelain 2>$null)) `
    -HasDll (Test-Path (Join-Path $package 'StationGodMCP.dll'))
if ($why) { Refuse $why }
$worldFile = Join-Path $Library "$World\$World.save"
$fixtures = Join-Path $Library "$World\fixtures.json"
if (-not (Test-Path $worldFile) -or -not (Test-Path $fixtures)) { Refuse "no $World world in the library ($worldFile, $fixtures)" }
if (-not (Test-Path (Join-Path $Hub 'StationGodMCP.Server.dll'))) { Refuse "no StationGodMCP.Server.dll in $Hub" }
$hubFile = if ((Test-Path (Join-Path $Hub 'StationGodMCP.Server.exe')) -and (Test-Path (Join-Path $Hub 'hostfxr.dll'))) { 'StationGodMCP.Server.exe' } else { 'StationGodMCP.Server.dll' }
foreach ($z in @($PythonZip) + @(if ($hubFile -like '*.dll') { $DotnetZip })) { if ($z -and -not (Test-Path $z)) { Refuse "missing $z" } }

# ---- staging on the host: the mod (named StationGodMCP) and the checks' folder the PreLaunch hook copies in ----
$stage = Join-Path ([IO.Path]::GetTempPath()) ('sg-silo-checks-' + [guid]::NewGuid().ToString('N'))
$mod = Join-Path $stage 'mods\StationGodMCP'
$sg = Join-Path $stage 'sg'
try {
    New-Item -ItemType Directory -Force (Split-Path $mod), "$sg\tools\silo-rig", "$sg\clients" | Out-Null
    Copy-Item -Recurse $package $mod
    Get-ChildItem $here -File -Filter *.py | ? Name -notlike 'test_*' | Copy-Item -Destination "$sg\tools\silo-rig"
    Copy-Item -Recurse (Join-Path $repo 'clients\python') "$sg\clients\python"
    Get-ChildItem "$sg\clients" -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force
    Copy-Item -Recurse $Hub "$sg\hub"
    Copy-Item $fixtures "$sg\fixtures.json"
    # The game's saves folder shape (<saves>\<world>\<world>.save), built here from the library's flat entry.
    $worldSave = Join-Path $stage "saves\$World"
    New-Item -ItemType Directory -Force $worldSave | Out-Null
    Copy-Item $worldFile (Join-Path $worldSave "$World.save")
    if ($PythonZip) { Copy-Item $PythonZip "$sg\python.zip" }
    if ($hubFile -like '*.dll' -and $DotnetZip) { Copy-Item $DotnetZip "$sg\dotnet.zip" }

    # What the hooks read (they run dot-sourced inside vm-client-run, in this process).
    $env:SG_SILO_STAGE = $sg; $env:SG_SILO_PIPE = $Pipe; $env:SG_SILO_HUB_FILE = $hubFile; $env:SG_SILO_GUEST_PYTHON = $GuestPython
    $env:SG_SILO_EXPECTED_MIN = Get-SiloExpectedMinutes; $env:SG_SILO_WORLD_TIMEOUT = Get-SiloWorldTimeout
    $env:SG_SILO_PACKAGE = "$(& git -C $repo rev-parse HEAD 2>$null)".Trim()

    $phases = Get-SiloPhases $Pipe | ConvertTo-Json -Compress
    $limits = Get-SiloPhaseLimits | ConvertTo-Json -Compress
    $bepCfg = @{ 'net.xceled.stationeers.stationgodmcp.cfg' = "[Pipe]`r`n`r`nName = $Pipe`r`n" } | ConvertTo-Json -Compress
    $hooks = Join-Path $here 'vm-hooks'
    $run = @{
        Mod = 'sg-silo-checks'; Owner = $Owner; Mods = @($mod); Save = $worldSave
        # -load last, the save's folder name, no world name (see above).
        GameArgs = Get-SiloGameArgs; PhasesJson = $phases; DoneGlob = '{out}\done-*'; BepCfgJson = $bepCfg
        PreLaunch = Join-Path $hooks 'silo-prelaunch.ps1'; OnPoll = Join-Path $hooks 'silo-onpoll.ps1'
        Judge = Join-Path $hooks 'silo-judge.ps1'; OnEnd = Join-Path $hooks 'silo-onend.ps1'; RigVMPin = $RigVMCommit
        Item = $Item; ExpectedMinutes = (Get-SiloExpectedMinutes)
        CheckScripts = @($hooks, (Join-Path $here 'silo_checks.py'), (Join-Path $here 'sgcall.py'))
        NoteRegex = '(?m)^(FAIL  |Traceback|SystemExit|RuntimeError)[^\r\n]*'
        TimeoutSeconds = $TimeoutSeconds; StallSeconds = $StallSeconds; PhaseLimitJson = $limits; WatchLogs = @(Get-SiloChecksLog); Dxdiag = $Dxdiag
    }
    if ($SettingsTemplate) { $run.SettingFrom = $SettingsTemplate }
    # P92: RigVM starts under Windows PowerShell 5.1, never in-process from this pwsh 7 script.
    $code = Invoke-RigVMEntry -Script $clientRun -Arguments $run
}
finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}
exit $code
