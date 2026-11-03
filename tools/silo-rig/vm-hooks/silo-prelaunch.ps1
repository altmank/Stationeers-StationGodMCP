# vm-client-run -PreLaunch hook (dot-sourced in its run: $r, $gRes, Invoke-Rig, Get-RigSession, Stop-Rig and Rig-Log
# are in scope). Copies the silo checks' folder that vm-run-silo-checks.ps1 staged (SG_SILO_STAGE: the checks,
# clients\python, the hub, fixtures.json, optional python.zip and dotnet.zip) to the guest as <res>\<runid>\sg, unzips
# the tools there and checks a Python 3.12 is at hand. Nothing is installed in the guest.
# Which StationGod commit this run used (rig-lib records the RigVM HEAD itself, info.rigVM).
$r.Info.package = $env:SG_SILO_PACKAGE
# P46: the run's expected length in minutes (vm-run-silo-checks.ps1, Get-SiloExpectedMinutes).
if ($env:SG_SILO_EXPECTED_MIN) { $r.Info.expectedMin = [int]$env:SG_SILO_EXPECTED_MIN }
$sgHost = $env:SG_SILO_STAGE
if (-not $sgHost -or -not (Test-Path "$sgHost\tools\silo-rig\silo_checks.py")) { Stop-Rig infra "PreLaunch: no staged silo checks at '$sgHost'" }
$gSg = "$gRes\sg"
Measure-RigStep sgStage {
    Copy-Item -ToSession (Get-RigSession) -Path $sgHost -Destination $gSg -Recurse -Force
    $tools = Invoke-Rig -ArgumentList $gSg, $env:SG_SILO_GUEST_PYTHON, $env:SG_SILO_HUB_FILE {
        param($sg, $py, $hubFile)
        $said = @()
        foreach ($n in 'python', 'dotnet') {
            if (Test-Path "$sg\$n.zip") { Expand-Archive "$sg\$n.zip" "$sg\tools\$n" -Force; Remove-Item "$sg\$n.zip"; $said += $n }
        }
        if (-not (Test-Path "$sg\hub\$hubFile")) { return "FAIL no hub $hubFile in $sg\hub" }
        if ($hubFile -like '*.dll' -and -not (Test-Path "$sg\tools\dotnet\dotnet.exe") -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            return 'FAIL no dotnet in the guest: give a self-contained -Hub or -DotnetZip'
        }
        $v = if (Test-Path "$sg\tools\python\python.exe") { & "$sg\tools\python\python.exe" -c 'import sys; print(sys.version_info[:2] >= (3, 12))' }
             else { $x = & $py -3.12 -c 'import sys; print(sys.version_info[:2] >= (3, 12))' 2>$null; if ($x -ne 'True') { $x = & $py -c 'import sys; print(sys.version_info[:2] >= (3, 12))' 2>$null }; $x }
        if ($v -ne 'True') { return "FAIL no Python 3.12 in the guest ($py): give -PythonZip" }
        $said -join ','
    }
    if ($tools -like 'FAIL*') { Stop-Rig infra "PreLaunch: $($tools.Substring(5))" }
    if ($tools) { Rig-Log "guest tools in sg: $tools" }
}
Add-RigCollect "$gRes\logs\checks.log" 'checks.log'
