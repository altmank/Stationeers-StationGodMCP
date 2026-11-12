# vm-pictures.ps1's OnPoll hook, dot-sourced on every poll inside vm-client-run.ps1's run ($r, $gRes, $gOut,
# Invoke-Rig, Rig-Log in scope). Once StationGod's pipe is up it starts shots.py in the guest, once: a hidden
# scheduled task as the rig user (the game's pipe is the rig user's), writing shots.log and the done file on R:.
if (-not $global:SgPicStarted -and $r.PhaseAt.Keys -contains 'stationgod') {
    $global:SgPicStarted = $true
    $picCtx = Get-Content -Raw $env:SG_PIC_CONTEXT | ConvertFrom-Json
    Rig-Log 'StationGod up: starting shots.py in the guest'
    $picFlags = if ($picCtx.frames) { '--frames' } else { '' }
    # The key guard check reads the game's BepInEx log (the client folder is beside the run's res folder).
    if ($picCtx.keyguard) { $picFlags += " --keyguard --bep-log `"$(Join-Path (Split-Path (Split-Path $gRes)) 'client-a\BepInEx\LogOutput.log')`"" }
    Invoke-Rig -ArgumentList "SG-Pictures-$($r.RunId)", "$gRes\sg", $picCtx.pipe, $gOut, "$gRes\logs\shots.log", $picFlags {
        param($task, $sg, $pipe, $out, $logFile, $flags)
        $cmd = "`"$sg\tools\python\python.exe`" `"$sg\tools\picture-rig\shots.py`" --pipe $pipe " +
               "--hub `"$sg\hub\StationGodMCP.Server.exe`" --done `"$out`" $flags > `"$logFile`" 2>&1"
        Set-Content "$sg\shots-run.cmd" ("@set PYTHONUNBUFFERED=1`r`n@set PYTHONIOENCODING=utf-8`r`n@$cmd`r`n")
        Set-Content "$sg\shots-run.ps1" "& cmd.exe /d /c `"$sg\shots-run.cmd`"`r`nexit `$LASTEXITCODE"
        $act = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File $sg\shots-run.ps1" -WorkingDirectory $sg
        $pr = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\rig" -LogonType Interactive -RunLevel Limited
        $set = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 1)
        Register-ScheduledTask -TaskName $task -Action $act -Principal $pr -Settings $set -Force | Out-Null
        Start-ScheduledTask -TaskName $task
    } | Out-Null
}
