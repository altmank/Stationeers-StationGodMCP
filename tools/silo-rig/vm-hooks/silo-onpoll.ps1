# vm-client-run -OnPoll hook (dot-sourced at every poll, $phase set; $r, $gRes, $gOut, Invoke-Rig and Rig-Log in scope).
# Once StationGod's pipe is up (phase stationgod), starts silo_checks.py in the guest, once: a scheduled task as the
# rig user (the game's pipe is the rig user's), hidden, writing <res>\<runid>\logs\checks.log and the done file in
# <out>. silo_checks.py waits for the world to run itself (sgcall.wait_ready).
if ($global:SgSiloChecksRun -ne $r.RunId -and $r.PhaseAt.Keys -contains 'stationgod') {
    $global:SgSiloChecksRun = $r.RunId
    . (Join-Path $PSScriptRoot 'silo-vm-lib.ps1')
    Rig-Log 'StationGod up: starting the silo checks in the guest'
    $gSg = "$gRes\sg"
    Invoke-Rig -ArgumentList "SG-Silo-$($r.RunId)", $env:SG_SILO_GUEST_PYTHON, $gSg, $env:SG_SILO_PIPE, $gOut, (Get-SiloChecksLog $gRes), $env:SG_SILO_HUB_FILE, ${function:Get-SiloChecksCmd}.ToString(), ([int]$env:SG_SILO_WORLD_TIMEOUT) {
        param($task, $py, $sg, $pipe, $out, $logFile, $hubFile, $cmdFn, $worldTimeout)
        ${function:Get-SiloChecksCmd} = [scriptblock]::Create($cmdFn)
        $ver = ''
        if (Test-Path "$sg\tools\python\python.exe") { $py = "$sg\tools\python\python.exe" }
        elseif ((& $py -3.12 -c 'print(1)' 2>$null) -eq '1') { $ver = '-3.12 ' }
        $dotnet = if (Test-Path "$sg\tools\dotnet\dotnet.exe") { "$sg\tools\dotnet" } else { '' }
        New-Item -ItemType Directory -Force $out, (Split-Path $logFile) | Out-Null
        Set-Content "$sg\checks-run.cmd" (Get-SiloChecksCmd $py $ver $sg $pipe $hubFile $out $logFile $dotnet $worldTimeout) -NoNewline
        Set-Content "$sg\checks-run.ps1" "& cmd.exe /d /c `"$sg\checks-run.cmd`"`r`nexit `$LASTEXITCODE"
        $act = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File $sg\checks-run.ps1" -WorkingDirectory $sg
        $pr = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\rig" -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $task -Action $act -Principal $pr -Force | Out-Null
        Start-ScheduledTask -TaskName $task
    } | Out-Null
}
