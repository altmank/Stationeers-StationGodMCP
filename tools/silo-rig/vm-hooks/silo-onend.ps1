# vm-client-run -OnEnd hook: rig-lib's finally runs it on every outcome (pass, fail, timeout, stall, crash, infra),
# dot-sourced in a child scope with $script:RigRun and Invoke-Rig (bounded by the OnEnd budget) at hand. It undoes
# what the OnPoll hook set up in the guest: the silo checks' scheduled task, stopped (ending silo_checks.py if it
# still runs) and unregistered. A failure is logged by rig-lib and never changes the run's verdict.
$task = "SG-Silo-$($script:RigRun.RunId)"
Invoke-Rig -ArgumentList $task {
    param($t)
    Stop-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue
} | Out-Null
