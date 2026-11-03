# vm-fabricate.ps1's PreLaunch hook, dot-sourced inside vm-client-run.ps1's run body ($r, $gRes, Invoke-Rig,
# Get-RigSession, Rig-Log, Stop-Rig in scope). Puts fab.py, sgcall.py, the sg client, the hub and a portable Python
# on the run's results disk in the guest (<res>\sg): nothing is installed in the guest.
$fabCtx = Get-Content -Raw $env:SG_FAB_CONTEXT | ConvertFrom-Json
$r.Info.pipe = $fabCtx.pipe; $r.Info.repo = $fabCtx.repoAt; $r.Info.expectedMin = $fabCtx.expectedMin
$fabStage = Join-Path $r.Res 'sg-stage\sg'
Measure-RigStep fabStage {
    New-Item -ItemType Directory -Force "$fabStage\tools\fab-rig", "$fabStage\tools\silo-rig", "$fabStage\clients" | Out-Null
    Copy-Item (Join-Path $fabCtx.here 'fab.py') "$fabStage\tools\fab-rig"
    Copy-Item (Join-Path $fabCtx.repo 'tools\silo-rig\sgcall.py') "$fabStage\tools\silo-rig"
    Copy-Item -Recurse (Join-Path $fabCtx.repo 'clients\python') "$fabStage\clients\python"
    Get-ChildItem "$fabStage\clients" -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force
    Copy-Item -Recurse $fabCtx.hub "$fabStage\hub"
    Copy-Item $fabCtx.pythonZip "$fabStage\python.zip"
    Copy-Item -ToSession (Get-RigSession) -Path $fabStage -Destination $gRes -Recurse -Force
    Remove-Item -Recurse -Force (Split-Path $fabStage) -ErrorAction SilentlyContinue
    $said = Invoke-Rig -TimeoutSeconds 600 -ArgumentList "$gRes\sg" {
        param($sg)
        Get-ScheduledTask -TaskName 'SG-Fab-*' -ErrorAction SilentlyContinue | % {
            Stop-ScheduledTask -TaskName $_.TaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
        }
        Expand-Archive "$sg\python.zip" "$sg\tools\python" -Force; Remove-Item "$sg\python.zip"
        if (-not (Test-Path "$sg\tools\python\python.exe")) { return 'FAIL no python.exe at the top of -PythonZip' }
        'python'
    }
    if ($said -like 'FAIL*') { Stop-Rig infra "fab rig stage: $($said.Substring(5))" }
    Rig-Log "fab rig staged in the guest ($said)"
}
$global:SgFabStarted = $false
