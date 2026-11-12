#Requires -Version 7.5
# Unit test for Test-WorkingEol (tools\working-eol.ps1): a temp git repo with one `eol=lf` file, written CRLF (refused) and
# LF (passes). No game, no VM. Run: pwsh -NoProfile -File tools\test-working-eol.ps1 (exit 0 = all held).
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'working-eol.ps1')
$failed = 0
function Assert-That([bool] $Ok, [string] $What) { if ($Ok) { Write-Host "ok    $What" } else { Write-Host "FAIL  $What"; $script:failed++ } }
function Get-Refusal([string] $Root) { try { Test-WorkingEol $Root; $null } catch { $_.Exception.Message } }

$dir = Join-Path ([IO.Path]::GetTempPath()) ('sg-working-eol-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dir | Out-Null
try {
    & git -C $dir init -q
    & git -C $dir config core.autocrlf false
    [IO.File]::WriteAllText("$dir\.gitattributes", "*.cs text eol=lf`n*.bat text eol=crlf`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText("$dir\a.cs", "x`ny`n")
    [IO.File]::WriteAllText("$dir\b.bat", "x`r`ny`r`n")
    [IO.File]::WriteAllText("$dir\free.txt", "x`r`ny`n")
    & git -C $dir add -A
    Assert-That ($null -eq (Get-Refusal $dir)) 'LF eol=lf file, CRLF eol=crlf file and an unattributed mixed file pass'

    [IO.File]::WriteAllText("$dir\a.cs", "x`r`ny`r`n")
    $m = Get-Refusal $dir
    Assert-That ($m -and $m -match 'a\.cs' -and $m -match '^1 tracked') 'eol=lf file written CRLF is refused, naming the path and count'
    [IO.File]::WriteAllText("$dir\a.cs", "x`r`ny`n")
    Assert-That ([bool](Get-Refusal $dir)) 'eol=lf file written mixed is refused'
    [IO.File]::WriteAllText("$dir\a.cs", "x`ny`n")
    Assert-That ($null -eq (Get-Refusal $dir)) 'restored to LF, it passes'

    [IO.File]::WriteAllText("$dir\b.bat", "x`ny`n")
    $m = Get-Refusal $dir
    Assert-That ($m -and $m -match 'b\.bat') 'eol=crlf file written LF is refused'
    [IO.File]::WriteAllText("$dir\b.bat", "x`r`ny`r`n")

    foreach ($i in 1..12) { [IO.File]::WriteAllText("$dir\m$i.cs", "x`ny`n") }
    & git -C $dir add -A
    foreach ($i in 1..12) { [IO.File]::WriteAllText("$dir\m$i.cs", "x`r`ny`r`n") }
    $m = Get-Refusal $dir
    Assert-That ($m -and $m -match '^12 tracked' -and $m -match 'and 2 more') 'twelve bad files: count 12, ten listed, "and 2 more"'
}
finally { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue }
exit $failed
