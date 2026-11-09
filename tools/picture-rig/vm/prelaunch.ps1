# vm-pictures.ps1's PreLaunch hook, dot-sourced inside vm-client-run.ps1's run body ($r, $gRes, Invoke-Rig,
# Get-RigSession, Rig-Log, Stop-Rig in scope). Puts shots.py, sgcall.py, the sg client, the hub and a portable Python
# on the run's results disk in the guest (<res>\sg): nothing is installed in the guest.
$picCtx = Get-Content -Raw $env:SG_PIC_CONTEXT | ConvertFrom-Json
$r.Info.pipe = $picCtx.pipe; $r.Info.repo = $picCtx.repoAt; $r.Info.expectedMin = $picCtx.expectedMin
$picStage = Join-Path $r.Res 'sg-stage\sg'
Measure-RigStep picturesStage {
    New-Item -ItemType Directory -Force "$picStage\tools\picture-rig", "$picStage\tools\silo-rig", "$picStage\clients" | Out-Null
    Copy-Item (Join-Path $picCtx.here 'shots.py'), (Join-Path $picCtx.here 'judge_shots.py') "$picStage\tools\picture-rig"
    Copy-Item (Join-Path $picCtx.repo 'tools\silo-rig\sgcall.py') "$picStage\tools\silo-rig"
    Copy-Item -Recurse (Join-Path $picCtx.repo 'clients\python') "$picStage\clients\python"
    Get-ChildItem "$picStage\clients" -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force
    Copy-Item -Recurse $picCtx.hub "$picStage\hub"
    Copy-Item $picCtx.pythonZip "$picStage\python.zip"
    Copy-Item -ToSession (Get-RigSession) -Path $picStage -Destination $gRes -Recurse -Force
    Remove-Item -Recurse -Force (Split-Path $picStage) -ErrorAction SilentlyContinue
    # The portable Python's unzip is the long step here; rig-lib's default limit is for short calls.
    $said = Invoke-Rig -TimeoutSeconds 600 -ArgumentList "$gRes\sg" {
        param($sg)
        Get-ScheduledTask -TaskName 'SG-Pictures-*' -ErrorAction SilentlyContinue | % {
            Stop-ScheduledTask -TaskName $_.TaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
        }
        Expand-Archive "$sg\python.zip" "$sg\tools\python" -Force; Remove-Item "$sg\python.zip"
        if (-not (Test-Path "$sg\tools\python\python.exe")) { return 'FAIL no python.exe at the top of -PythonZip' }
        'python'
    }
    if ($said -like 'FAIL*') { Stop-Rig infra "pictures stage: $($said.Substring(5))" }
    Rig-Log "pictures staged in the guest ($said)"
}
$global:SgPicStarted = $false
