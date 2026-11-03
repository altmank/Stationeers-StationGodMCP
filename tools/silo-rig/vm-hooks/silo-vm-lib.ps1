# Pure helpers for vm-run-silo-checks.ps1 and its vm-client-run hooks (no rig, no game): tested by
# test-silo-vm-lib.ps1.

# Why a staged package may not be the build under test, or '' when it may: package-build.txt (written by build.ps1)
# must name HEAD with a clean tree, and the tree must still be clean (as build.ps1 -DeployOnly).
function Get-PackageRefusal([string[]] $Stamp, [string] $Head, [bool] $Dirty, [bool] $HasDll) {
    if (-not $HasDll -or @($Stamp).Count -lt 2) { return 'no staged package with a build record (run build.ps1)' }
    if ($Stamp[0] -ne $Head) { return "the package was staged from $($Stamp[0]), HEAD is $Head (run build.ps1)" }
    if ($Stamp[1] -ne 'clean') { return 'the package was staged from a tree with uncommitted changes (commit, then run build.ps1)' }
    if ($Dirty) { return 'the tree has uncommitted changes since the package was staged (commit, then run build.ps1)' }
    ''
}

# The checks' own log, watched by vm-client-run beside Player.log and BepInEx's LogOutput.log (-WatchLogs): its lines
# count as life and mark phases. The wrapper passes it with vm-client-run's {res} token; the OnPoll hook writes it at
# the same place under the run's res folder (<ClientRoot>\res\<runid>).
function Get-SiloChecksLog([string] $Res = '{res}') { "$Res\logs\checks.log" }

# The phases, in order, matched on whole lines of the watched logs. StationGod's pipe comes up while mods load, before
# the world: the OnPoll hook starts the checks there. The rest are silo_checks.py's own lines: progress() ("...   "),
# record() ("PASS  ", "FAIL  ", "SKIP  ") and summary_line() (the last).
function Get-SiloPhases([string] $Pipe) {
    [ordered]@{
        bepinex    = 'Chainloader startup complete'
        stationgod = "Pipe name: $([regex]::Escape($Pipe))"
        checking   = '^\.\.\.   connecting to '
        world      = '^\.\.\.   world running: '
        first      = '^(PASS|FAIL|SKIP)  '
        summary    = '^\d+ checks: \d+ passed or skipped, \d+ failed$'
    }
}

# rig-lib's stuck rule: while later phases are expected, no new phase and no new watched line within the limit of the
# phase the run is in (60 s default and minimum) ends the run as stall. Limits for the stretches that may be slow:
# mods loading after BepInEx; the checks' scheduled task starting after the pipe is up; the world loading while the
# checks wait for it (they print every 3 s, but a call may block while the world loads, a 600-entry silo save); the
# first check after the world runs (it reads every silo and holder); one check to the next. After summary nothing is
# expected; the stall rule (-StallSeconds, no log growth at all) still applies.
function Get-SiloPhaseLimits { [ordered]@{ bepinex = 300; stationgod = 300; checking = 480; world = 300; first = 300 } }
$script:SiloStallSeconds = 900

# P46: the run's expected length, from the lock: boot, load sg-silo, 12 to 14 checks. About 6 minutes.
function Get-SiloExpectedMinutes { 6 }
# The timeout (vm-client-run counts it from the game's launch): the expected length plus a margin of 9 minutes, which
# covers the longest phase limit (checking, the world loading) on top of a run that is slow everywhere else.
function Get-SiloTimeoutSeconds { (Get-SiloExpectedMinutes) * 60 + 540 }
# How long silo_checks.py waits for the world to run before it fails at once with "world never loaded": under the
# checking limit, so a world that never loads ends as that clear fail, not as a stuck or stall.
function Get-SiloWorldTimeout { 420 }

# The game's launch argument that loads the world: the save's folder name ({savename}, vm-client-run's token), looked
# up in the run's saves folder. Never {save} or {savefile}: a folder path goes to LoadAtFullPath, which wants a .save
# file, and a file path to a name lookup; both leave the client at the menu.
function Get-SiloGameArgs { '-load {savename}' }

# How many checks a full run on sg-silo makes (silo_checks.py: 12 silo checks and 2 food checks).
function Get-SiloExpectedChecks { 14 }

# The run's verdict from silo_checks.py's done file: its name (done-ok, done-fail or none) and its lines (every
# PASS/FAIL/SKIP line, then the trailer "<n> checks, <f> failed"). It passes only on done-ok whose trailer says at least
# -ExpectedChecks checks and 0 failed, with one result line per check, no FAIL and no SKIP: an empty, partial or short
# done file, or a check skipped, fails the run.
function Get-SiloVerdict([string] $DoneName, [string] $Text, [int] $ExpectedChecks = (Get-SiloExpectedChecks)) {
    $lines = @("$Text" -split "`r?`n" | ? { $_ })
    $results = @($lines | ? { $_ -match '^(PASS|FAIL|SKIP)  ' })
    $fails = @($results | ? { $_ -like 'FAIL*' })
    $skips = @($results | ? { $_ -like 'SKIP*' })
    $fail = { param($why) [pscustomobject]@{ Status = 'fail'; Reason = $why; Lines = $lines } }
    if (-not $DoneName) { return & $fail 'the silo checks left no done file' }
    $m = if ($lines) { [regex]::Match($lines[-1], '^(\d+) checks, (\d+) failed$') }
    if (-not $m -or -not $m.Success -or [int]$m.Groups[1].Value -ne $results.Count) {
        return & $fail "done file incomplete (${DoneName}: $($results.Count) result lines, last line '$(if ($lines) { $lines[-1] })')"
    }
    $n = [int]$m.Groups[1].Value
    if ($DoneName -ne 'done-ok' -or $fails -or [int]$m.Groups[2].Value -ne 0) { return & $fail "$($lines[-1]); $($fails -join ' | ')" }
    if ($skips) { return & $fail "skipped: $($skips -join ' | ')" }
    if ($n -lt $ExpectedChecks) { return & $fail "$n of $ExpectedChecks checks" }
    [pscustomobject]@{ Status = 'pass'; Reason = $lines[-1]; Lines = $lines }
}

# The guest command line that runs silo_checks.py (a .cmd body: cmd's redirect keeps checks.log as Python's UTF-8).
function Get-SiloChecksCmd([string] $Python, [string] $Version, [string] $Sg, [string] $Pipe, [string] $HubFile,
    [string] $Out, [string] $LogFile, [string] $DotnetDir, [int] $WorldTimeout = 0) {
    $path = if ($DotnetDir) { "@set DOTNET_ROOT=$DotnetDir`r`n@set PATH=$DotnetDir;%PATH%`r`n" } else { '' }
    $world = if ($WorldTimeout -gt 0) { " --world-timeout $WorldTimeout" } else { '' }
    $cmd = "`"$Python`" $Version`"$Sg\tools\silo-rig\silo_checks.py`" --pipe $Pipe --hub `"$Sg\hub\$HubFile`" " +
           "--fixtures `"$Sg\fixtures.json`" --done `"$Out`"$world > `"$LogFile`" 2>&1"
    "@set PYTHONUNBUFFERED=1`r`n@set PYTHONIOENCODING=utf-8`r`n$path@$cmd`r`n"
}
