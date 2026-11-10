# vm-build-world.ps1's OnPoll hook, dot-sourced on every poll inside vm-client-run.ps1's run ($r, $gRes, $gData,
# $gOut, Invoke-Rig, Rig-Log in scope). Once StationGod's pipe is up it starts build_world.py in the guest, once: a
# hidden scheduled task as the rig user (the game's pipe is the rig user's), writing build.log and the done file on R:.
# The builder waits for the world's player itself (whether a new world's console lines reach Player.log on a client
# is not known), so the pipe is the only sign it needs.
if (-not $global:SgTwBuilderStarted -and $r.PhaseAt.Keys -contains 'stationgod') {
    $global:SgTwBuilderStarted = $true
    $sgCtx = Get-Content -Raw $env:SG_TW_CONTEXT | ConvertFrom-Json
    Rig-Log 'StationGod up: starting the builder in the guest'
    # Values first: in a command's argument list `[bool]$x` is not a cast but the string "[bool]<x>", which is always
    # true (the 11-02 02:43 and 03:26 rig runs each ran `check --expect [int]@{...}.expect`, which argparse refused).
    $sgCheck = [bool]$sgCtx.check; $sgExpect = [int]$sgCtx.expect
    Invoke-Rig -ArgumentList "SG-Build-$($r.RunId)", "$gRes\sg", $sgCtx.world, $sgCtx.pipe, $gOut, "$gRes\logs\build.log", $gData, $sgCtx.lift, $sgCtx.hubFile, $sgCheck, $sgExpect {
        param($task, $sg, $world, $pipe, $out, $logFile, $data, $lift, $hubFile, $check, $expect)
        $step = if ($check) { "check --expect $expect --world $world" } elseif ($world -eq 'sg-smoke') { 'smoke' } else { "build --world $world --manifest `"$out\manifest.json`" --lift $lift" }
        $dotnetEnv = if (Test-Path "$sg\tools\dotnet\dotnet.exe") { "@set DOTNET_ROOT=$sg\tools\dotnet`r`n@set PATH=$sg\tools\dotnet;%PATH%`r`n" } else { '' }
        # cmd's redirect keeps build.log as Python's own UTF-8 bytes (PowerShell 5.1's *> writes UTF-16).
        $cmd = "`"$sg\tools\python\python.exe`" `"$sg\tools\test-worlds\build_world.py`" $step --pipe $pipe " +
               "--hub `"$sg\hub\$hubFile`" --saves `"$data\saves`" --done `"$out`" > `"$logFile`" 2>&1"
        Set-Content "$sg\build-run.cmd" ("@set PYTHONUNBUFFERED=1`r`n@set PYTHONIOENCODING=utf-8`r`n" + $dotnetEnv + "@$cmd`r`n")
        # Hidden PowerShell owns a hidden console that cmd and python inherit: no window takes the game's focus.
        Set-Content "$sg\build-run.ps1" "& cmd.exe /d /c `"$sg\build-run.cmd`"`r`nexit `$LASTEXITCODE"
        $act = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File $sg\build-run.ps1" -WorkingDirectory $sg
        $pr = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\rig" -LogonType Interactive -RunLevel Limited
        $set = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2)
        Register-ScheduledTask -TaskName $task -Action $act -Principal $pr -Settings $set -Force | Out-Null
        Start-ScheduledTask -TaskName $task
    } | Out-Null
}
