# vm-build-world.ps1's Judge hook, dot-sourced at the end of vm-client-run.ps1's run, before collection ($r, $gRes,
# $gData, $gOut, Invoke-Rig, Get-RigSession, Complete-Rig, Set-RigPhase, Rig-Log in scope). The builder's done file
# decides (the builder's task is removed by vm\onend.ps1, on every outcome): done-ok brings the save (and the manifest) back over PS Direct and stores them in the world library;
# done-fail, or no done file, fails the run with the builder's reason.
$sgCtx = Get-Content -Raw $env:SG_TW_CONTEXT | ConvertFrom-Json
$world = $sgCtx.world
Add-RigCollect "$gRes\logs\build.log" 'build.log'

$end = @(Invoke-Rig -ArgumentList $gOut { param($o) Get-ChildItem $o -Filter 'done-*' -ErrorAction SilentlyContinue | % { [pscustomobject]@{ name = $_.Name; text = (Get-Content -Raw $_.FullName) } } })
if (-not $end) {
    Complete-Rig fail 'the builder wrote no done file'
} elseif ($end[0].name -ne 'done-ok') {
    $r.Info.done = "$($end[0].name): $($end[0].text)"
    Complete-Rig fail "the builder ended $($end[0].name): $($end[0].text)"
} elseif ($sgCtx.check) {
    $r.Info.done = "done-ok: $($end[0].text)"
    $r.Info.check = $end[0].text
    $loaded = if ($r.PhaseAt.Keys -contains 'world') { $r.PhaseAt['world'] } else { $null }
    $r.Info.worldLoadedAtS = $loaded
    Complete-Rig pass "$world loaded (world phase at $loaded s after launch), StationGod answered: $($end[0].text)"
} else {
    $r.Info.done = "done-ok: $($end[0].text)"
    $back = Join-Path $r.Res 'built'
    New-Item -ItemType Directory -Force $back | Out-Null
    $s = Get-RigSession
    Copy-Item -FromSession $s "$gData\saves\$world" $back -Recurse -Force
    $r.Info.saveMd5 = (Get-FileHash "$back\$world\$world.save" -Algorithm MD5).Hash
    if ($world -eq 'sg-smoke') {
        $seen = Invoke-Rig -ArgumentList "$gRes\logs\build.log" { param($l) Get-Content $l | ? { $_ -match '"role"' } | Select-Object -First 1 }
        $r.Info.smoke = $seen
        Complete-Rig pass "new world up, StationGod answered ($seen), saved and copied back (md5 $($r.Info.saveMd5))"
    } else {
        Copy-Item -FromSession $s "$gOut\manifest.json" "$back\manifest.json"
        Set-RigPhase storing
        $py = (Get-Command py -ErrorAction SilentlyContinue).Source
        if (-not $py) { Complete-Rig fail 'no py launcher on the host to store the world' }
        else {
            # Windows PowerShell 5.1 turns a native command's stderr line (2>&1) into an error record, which under Stop
            # throws before $LASTEXITCODE is read and loses the reason: Continue here, and the exit code below still fails the run.
            $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
            try { $out = & $py -3.12 (Join-Path $sgCtx.here 'build_world.py') store --world $world --save-folder "$back\$world" --manifest "$back\manifest.json" --library $sgCtx.library 2>&1 | % { "$_" }; $code = $LASTEXITCODE }
            finally { $ErrorActionPreference = $eap }
            $out | % { Rig-Log "store: $_" }
            if ($code -ne 0) { Complete-Rig fail "store exit $code`: $($out | Select-Object -Last 1)" }
            else { Complete-Rig pass "$world built, saved (md5 $($r.Info.saveMd5)) and stored: $($out | Select-Object -Last 1)" }
        }
    }
}
