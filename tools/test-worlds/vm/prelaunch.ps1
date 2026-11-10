# vm-build-world.ps1's PreLaunch hook, dot-sourced inside vm-client-run.ps1's run body ($r, $gRes, Invoke-Rig,
# Get-RigSession, Rig-Log, Stop-Rig in scope). Puts the builder, the hub and a portable Python on the run's results
# disk in the guest (<res>\sg): nothing is installed in the guest, and it all goes with the run's disk.
$sgCtx = Get-Content -Raw $env:SG_TW_CONTEXT | ConvertFrom-Json
$r.Info.world = $sgCtx.world; $r.Info.pipe = $sgCtx.pipe; $r.Info.hub = $sgCtx.hubFile
$r.Info.repo = $sgCtx.repoAt
# P46: the run's expected length and limits in result.json, for whoever watches the run.
if ($sgCtx.expectedMin) { $r.Info.expectedMin = $sgCtx.expectedMin; $r.Info.limits = $sgCtx.limits; Save-RigResult }
$sgStage = Join-Path $r.Res 'sg-stage\sg'
Measure-RigStep builderStage {
    New-Item -ItemType Directory -Force "$sgStage\tools\test-worlds", "$sgStage\tools\silo-rig", "$sgStage\clients" | Out-Null
    Get-ChildItem $sgCtx.here -File -Filter *.py | ? Name -notlike 'test_*' | Copy-Item -Destination "$sgStage\tools\test-worlds"
    Copy-Item (Join-Path $sgCtx.repo 'tools\silo-rig\sgcall.py') "$sgStage\tools\silo-rig"
    Copy-Item -Recurse (Join-Path $sgCtx.repo 'clients\python') "$sgStage\clients\python"
    Get-ChildItem "$sgStage\clients" -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force
    Copy-Item -Recurse $sgCtx.hub "$sgStage\hub"
    Copy-Item $sgCtx.pythonZip "$sgStage\python.zip"
    if ($sgCtx.dotnetZip) { Copy-Item $sgCtx.dotnetZip "$sgStage\dotnet.zip" }
    Copy-Item -ToSession (Get-RigSession) -Path $sgStage -Destination $gRes -Recurse -Force
    Remove-Item -Recurse -Force (Split-Path $sgStage) -ErrorAction SilentlyContinue
    # Unzipping Python on the results disk can pass Invoke-Rig's 120 s default.
    $said = Invoke-Rig -TimeoutSeconds 600 -ArgumentList "$gRes\sg" {
        param($sg)
        # A builder task a dead run left behind goes first.
        Get-ScheduledTask -TaskName 'SG-Build-*' -ErrorAction SilentlyContinue | % {
            Stop-ScheduledTask -TaskName $_.TaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
        }
        foreach ($n in 'python', 'dotnet') {
            if (Test-Path "$sg\$n.zip") { Expand-Archive "$sg\$n.zip" "$sg\tools\$n" -Force; Remove-Item "$sg\$n.zip" }
        }
        if (-not (Test-Path "$sg\tools\python\python.exe")) { return 'FAIL no python.exe at the top of -PythonZip' }
        $v = & "$sg\tools\python\python.exe" -c 'import sys; print(sys.version_info[:2] >= (3, 12))'
        if ($v -ne 'True') { return 'FAIL the -PythonZip python is not 3.12 or later' }
        'python' + $(if (Test-Path "$sg\tools\dotnet\dotnet.exe") { ', dotnet' } else { '' })
    }
    if ($said -like 'FAIL*') { Stop-Rig infra "builder stage: $($said.Substring(5))" }
    Rig-Log "builder staged in the guest ($said, hub $($sgCtx.hubFile))"
}
$global:SgTwBuilderStarted = $false
