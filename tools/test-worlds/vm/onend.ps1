# vm-build-world.ps1's OnEnd hook, run by rig-lib's finally on every outcome (pass, fail, timeout, stall, crash,
# infra) while the rig is still locked and up. Only rig-lib's own state is in scope here ($script:RigRun, Invoke-Rig,
# Rig-Log), not the run body's variables. It removes the builder's scheduled task and stops its Python, so nothing
# of this run keeps running in the guest. A failure here is logged by rig-lib, never the verdict.
$sgRunId = $script:RigRun.RunId
$gone = Invoke-Rig -ArgumentList "SG-Build-$sgRunId", $sgRunId {
    param($task, $runId)
    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    # The builder's Python runs from this run's results folder: stop any still alive (by path, never by name alone).
    $left = @(Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue | ? { $_.ExecutablePath -like "*\$runId\sg\*" })
    $left | % { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    "task $task removed; $($left.Count) builder process(es) stopped"
}
Rig-Log "OnEnd: $gone"
