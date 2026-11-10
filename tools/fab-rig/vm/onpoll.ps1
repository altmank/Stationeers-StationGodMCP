# vm-fabricate.ps1's OnPoll hook, dot-sourced on every poll inside vm-client-run.ps1's run ($r, $gRes, $gOut,
# Invoke-Rig, Rig-Log in scope). Once StationGod's pipe is up it starts fab.py in the guest, once: a hidden
# scheduled task as the rig user (the game's pipe is the rig user's), writing fab.log and the done file on R:.
if (-not $global:SgFabStarted -and $r.PhaseAt.Keys -contains 'stationgod') {
    $global:SgFabStarted = $true
    $fabCtx = Get-Content -Raw $env:SG_FAB_CONTEXT | ConvertFrom-Json
    Rig-Log 'StationGod up: starting fab.py in the guest'
    Invoke-Rig -ArgumentList "SG-Fab-$($r.RunId)", "$gRes\sg", $fabCtx.pipe, $gOut, "$gRes\logs\fab.log" {
        param($task, $sg, $pipe, $out, $logFile)
        $cmd = "`"$sg\tools\python\python.exe`" `"$sg\tools\fab-rig\fab.py`" --pipe $pipe " +
               "--hub `"$sg\hub\StationGodMCP.Server.exe`" --done `"$out`" > `"$logFile`" 2>&1"
        Set-Content "$sg\fab-run.cmd" ("@set PYTHONUNBUFFERED=1`r`n@set PYTHONIOENCODING=utf-8`r`n@$cmd`r`n")
        Set-Content "$sg\fab-run.ps1" "& cmd.exe /d /c `"$sg\fab-run.cmd`"`r`nexit `$LASTEXITCODE"
        $act = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File $sg\fab-run.ps1" -WorkingDirectory $sg
        $pr = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\rig" -LogonType Interactive -RunLevel Limited
        $set = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 1)
        Register-ScheduledTask -TaskName $task -Action $act -Principal $pr -Settings $set -Force | Out-Null
        Start-ScheduledTask -TaskName $task
    } | Out-Null
}
