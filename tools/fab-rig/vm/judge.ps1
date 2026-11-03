# vm-fabricate.ps1's Judge hook, dot-sourced at the end of vm-client-run.ps1's run, before collection ($r, $gRes, $gOut,
# Invoke-Rig, Complete-Rig, Add-RigCollect, Rig-Log in scope). fab.py's done file has the last word, and only a
# complete one passes: done-ok whose text is the summary with the made item's id, beside a replies.json that reads as
# JSON and holds the `make` reply. Anything else fails. fab.py writes the done file under another name and renames it,
# so a done file is never half written.
Add-RigCollect "$gRes\logs\fab.log" 'fab.log'
$end = @(Invoke-Rig -ArgumentList $gOut { param($o) Get-ChildItem $o -Filter 'done-*' -ErrorAction SilentlyContinue | % { [pscustomobject]@{ name = $_.Name; text = (Get-Content -Raw $_.FullName) } } })
$replies = Invoke-Rig -ArgumentList $gOut { param($o) $f = Join-Path $o 'replies.json'; if (Test-Path $f) { Get-Content -Raw $f } }
if (-not $end) {
    Complete-Rig fail 'fab.py wrote no done file'
} elseif ($end[0].name -ne 'done-ok') {
    $r.Info.done = "$($end[0].name): $($end[0].text)"
    Complete-Rig fail "fab.py ended $($end[0].name): $($end[0].text)"
} else {
    $r.Info.done = "done-ok: $($end[0].text)"
    $summary = try { $end[0].text | ConvertFrom-Json } catch { $null }
    $log = try { $replies | ConvertFrom-Json } catch { $null }
    if (-not $summary -or -not $summary.made) {
        Complete-Rig fail "done-ok is not a complete summary (no made id): $($end[0].text)"
    } elseif (-not $log -or -not $log.make) {
        Complete-Rig fail 'replies.json is missing, unreadable, or has no make reply'
    } else {
        Complete-Rig pass "fab.py done-ok: $($end[0].text)"
    }
}
