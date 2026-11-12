# Test-WorkingEol: refuses a checkout whose working files carry line endings other than .gitattributes asks for (P47).
# The PDB records each source file's hash and lint-rules.json is embedded, so a CRLF working copy of an `eol=lf` file
# builds a different dll than a fresh checkout of the same commit. Dot-sourced by build.ps1 and tools\test-working-eol.ps1.
function Test-WorkingEol([string] $Root) {
    $lines = @(& git -C $Root ls-files --eol 2>$null)
    if ($LASTEXITCODE -ne 0) { return }   # not a git checkout: nothing to compare
    $bad = @(foreach ($line in $lines) {
        # "i/lf    w/crlf  attr/text eol=lf    <TAB>path"
        if ($line -match '^i/\S+\s+w/(?<w>\S+)\s+attr/(?<a>.*?)\t(?<p>.+)$') {
            $attr = $Matches.a; $w = $Matches.w; $p = $Matches.p
            if (($attr -match '\beol=lf\b' -and $w -in 'crlf', 'mixed') -or
                ($attr -match '\beol=crlf\b' -and $w -in 'lf', 'mixed')) { $p }
        }
    })
    if ($bad.Count -eq 0) { return }
    $shown = ($bad | Select-Object -First 10) -join "`n  "
    $more = if ($bad.Count -gt 10) { "`n  ... and $($bad.Count - 10) more" } else { '' }
    throw ("$($bad.Count) tracked file(s) have working-copy line endings that differ from .gitattributes (git ls-files --eol), " +
        "so this checkout would build a different dll than a fresh one (P47):`n  $shown$more`n" +
        "Fix: delete those files and run 'git checkout -- <paths>', or re-clone.")
}
