# vm-pictures.ps1's OnEnd hook, run by rig-lib's finally on every outcome while the rig is still locked and up. It
# removes shots.py's scheduled task and stops its Python (by path, never by name alone).
$picRunId = $script:RigRun.RunId
$gone = Invoke-Rig -ArgumentList "SG-Pictures-$picRunId", $picRunId {
    param($task, $runId)
    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    $left = @(Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue | ? { $_.ExecutablePath -like "*\$runId\sg\*" })
    $left | % { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    "task $task removed; $($left.Count) process(es) stopped"
}
Rig-Log "OnEnd: $gone"
