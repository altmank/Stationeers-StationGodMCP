# vm-pictures.ps1's Judge hook, dot-sourced at the end of vm-client-run.ps1's run, before collection ($r, $gRes, $gOut,
# Invoke-Rig, Complete-Rig, Add-RigCollect, Rig-Log in scope). shots.py's done file decides; the pictures and
# replies.json are on the results disk (out\shots\suit, out\replies.json) and come back with it.
Add-RigCollect "$gRes\logs\shots.log" 'shots.log'
$end = @(Invoke-Rig -ArgumentList $gOut { param($o) Get-ChildItem $o -Filter 'done-*' -ErrorAction SilentlyContinue | % { [pscustomobject]@{ name = $_.Name; text = (Get-Content -Raw $_.FullName) } } })
$shots = @(Invoke-Rig -ArgumentList $gOut { param($o) Get-ChildItem "$o\shots" -Recurse -Filter *.png -ErrorAction SilentlyContinue | % { "$($_.Name) $($_.Length)" } })
$r.Info.pictures = $shots
if (-not $end) {
    Complete-Rig fail 'shots.py wrote no done file'
} elseif ($end[0].name -ne 'done-ok') {
    $r.Info.done = "$($end[0].name): $($end[0].text)"
    Complete-Rig fail "shots.py ended $($end[0].name): $($end[0].text)"
} elseif ((Get-Content -Raw $env:SG_PIC_CONTEXT | ConvertFrom-Json).slotChecks -and $end[0].text -notmatch 'slot-check ran: ') {
    $r.Info.done = "done-ok without the slot check's marker: $($end[0].text)"
    Complete-Rig fail "done-ok, but the slot check did not run (no marker): $($end[0].text)"
} elseif ((Get-Content -Raw $env:SG_PIC_CONTEXT | ConvertFrom-Json).needsChecks -and $end[0].text -notmatch 'needs-check ran: ') {
    $r.Info.done = "done-ok without the needs check's marker: $($end[0].text)"
    Complete-Rig fail "done-ok, but the needs check did not run (no marker): $($end[0].text)"
} elseif ((Get-Content -Raw $env:SG_PIC_CONTEXT | ConvertFrom-Json).standChecks -and $end[0].text -notmatch 'stand-check ran: ') {
    $r.Info.done = "done-ok without the stand check's marker: $($end[0].text)"
    Complete-Rig fail "done-ok, but the stand check did not run (no marker): $($end[0].text)"
} else {
    $r.Info.done = "done-ok: $($end[0].text)"
    Complete-Rig pass "pictures written: $($shots -join '; ')"
}
