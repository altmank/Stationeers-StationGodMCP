# vm-fabricate.ps1's Judge hook, dot-sourced at the end of vm-client-run.ps1's run, before collection ($r, $gRes, $gOut,
# Invoke-Rig, Complete-Rig, Add-RigCollect, Rig-Log in scope). fab.py's done file decides; replies.json is on
# the results disk (out\replies.json) and comes back with it.
Add-RigCollect "$gRes\logs\fab.log" 'fab.log'
$end = @(Invoke-Rig -ArgumentList $gOut { param($o) Get-ChildItem $o -Filter 'done-*' -ErrorAction SilentlyContinue | % { [pscustomobject]@{ name = $_.Name; text = (Get-Content -Raw $_.FullName) } } })
if (-not $end) {
    Complete-Rig fail 'fab.py wrote no done file'
} elseif ($end[0].name -ne 'done-ok') {
    $r.Info.done = "$($end[0].name): $($end[0].text)"
    Complete-Rig fail "fab.py ended $($end[0].name): $($end[0].text)"
} else {
    $r.Info.done = "done-ok: $($end[0].text)"
    Complete-Rig pass "fab.py done-ok: $($end[0].text)"
}
