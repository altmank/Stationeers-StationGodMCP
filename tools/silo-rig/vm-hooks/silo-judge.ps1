# vm-client-run -Judge hook (dot-sourced at the end, before collection; $r, $gOut, Invoke-Rig, Complete-Rig in scope).
# The silo checks' done file has the last word (Get-SiloVerdict): only a complete done-ok with every expected check
# passed, none skipped, passes; anything else fails, and every PASS/FAIL/SKIP line goes into result.json (info.checks). The scheduled task is removed by the OnEnd hook, which runs
# on every outcome.
. (Join-Path $PSScriptRoot 'silo-vm-lib.ps1')
$end = Invoke-Rig -ArgumentList $gOut { param($o) Get-ChildItem $o -Filter 'done-*' -ErrorAction SilentlyContinue | Select-Object -First 1 | % { [pscustomobject]@{ name = $_.Name; text = (Get-Content -Raw $_.FullName) } } }
$verdict = Get-SiloVerdict "$($end.name)" "$($end.text)"
$r.Info.checks = $verdict.Lines
Complete-Rig $verdict.Status $verdict.Reason
