# powershell -NoProfile -File tools\silo-rig\vm-hooks\test-silo-vm-lib.ps1   (also under pwsh; no rig, no game)
# Exit 0 when every assertion holds, else the number that failed.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'silo-vm-lib.ps1')
$script:failed = 0
function Assert([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "ok    $what" } else { Write-Host "FAIL  $what"; $script:failed++ }
}

# Get-PackageRefusal
Assert ((Get-PackageRefusal @('abc', 'clean') 'abc' $false $true) -eq '') 'package from HEAD, clean, tree clean: allowed'
Assert ((Get-PackageRefusal @('abc', 'clean') 'def' $false $true) -like '*staged from abc, HEAD is def*') 'other commit refused'
Assert ((Get-PackageRefusal @('abc', 'dirty') 'abc' $false $true) -like '*uncommitted*') 'dirty build refused'
Assert ((Get-PackageRefusal @('abc', 'clean') 'abc' $true $true) -like '*since the package was staged*') 'tree changed since refused'
Assert ((Get-PackageRefusal @() 'abc' $false $true) -like 'no staged package*') 'no build record refused'
Assert ((Get-PackageRefusal @('abc', 'clean') 'abc' $false $false) -like 'no staged package*') 'no DLL refused'

# Get-SiloVerdict: only a complete done-ok with every expected check passed and none skipped passes.
function Done([int] $pass, [string[]] $more = @(), [int] $fails = 0) {
    $l = @(1..$pass | % { "PASS  check $_  -- ok" }) + $more
    ($l + "$($l.Count) checks, $fails failed") -join "`n"
}
Assert ((Get-SiloExpectedChecks) -eq 14) 'a full sg-silo run makes 14 checks'
$v = Get-SiloVerdict 'done-ok' (Done 14)
Assert ($v.Status -eq 'pass' -and $v.Reason -eq '14 checks, 0 failed' -and $v.Lines.Count -eq 15) 'a good 14 of 14: pass'
$v = Get-SiloVerdict 'done-ok' ((Done 14) -replace "`n", "`r`n")
Assert ($v.Status -eq 'pass') 'CRLF lines: pass'
$v = Get-SiloVerdict 'done-ok' ''
Assert ($v.Status -eq 'fail' -and $v.Reason -like 'done file incomplete*') 'an empty done file: incomplete'
$v = Get-SiloVerdict 'done-ok' ((1..9 | % { "PASS  check $_" }) -join "`n")
Assert ($v.Status -eq 'fail' -and $v.Reason -like 'done file incomplete*') 'a partial done file (no trailer): incomplete'
$v = Get-SiloVerdict 'done-ok' ((@(1..9 | % { "PASS  check $_" }) + '14 checks, 0 failed') -join "`n")
Assert ($v.Status -eq 'fail' -and $v.Reason -like 'done file incomplete*') 'a trailer claiming more lines than the file holds: incomplete'
$v = Get-SiloVerdict 'done-ok' (Done 13)
Assert ($v.Status -eq 'fail' -and $v.Reason -eq '13 of 14 checks') '13 of 14: fail'
$v = Get-SiloVerdict 'done-ok' (Done 13 @('SKIP  deposit silo_full past 600 entries  -- no silo_full fixture'))
Assert ($v.Status -eq 'fail' -and $v.Reason -like 'skipped: SKIP  deposit silo_full*') 'a SKIP: fail, naming the check'
$v = Get-SiloVerdict 'done-fail' (Done 13 @('FAIL  withdraw food that rots  -- got x') 1)
Assert ($v.Status -eq 'fail' -and $v.Reason -eq '14 checks, 1 failed; FAIL  withdraw food that rots  -- got x') 'a FAIL: fail, naming it'
$v = Get-SiloVerdict 'done-ok' (Done 13 @('FAIL  c') 1)
Assert ($v.Status -eq 'fail') 'a FAIL line fails even in done-ok'
$v = Get-SiloVerdict 'done-ok' (Done 15)
Assert ($v.Status -eq 'pass') 'more checks than expected: pass'
$v = Get-SiloVerdict '' ''
Assert ($v.Status -eq 'fail' -and $v.Reason -like '*no done file*') 'no done file: fail'

# Get-SiloChecksCmd
$c = Get-SiloChecksCmd 'C:\py\python.exe' '' 'R:\run\sg' 'StationGodMCP-VM' 'StationGodMCP.Server.exe' 'R:\run\out' 'R:\run\logs\checks.log' ''
Assert ($c -like '*"C:\py\python.exe" "R:\run\sg\tools\silo-rig\silo_checks.py" --pipe StationGodMCP-VM*') 'runs silo_checks.py on the pipe'
Assert ($c -like '*--hub "R:\run\sg\hub\StationGodMCP.Server.exe"*--fixtures "R:\run\sg\fixtures.json" --done "R:\run\out"*') 'hub, fixtures, done'
Assert ($c -like '*> "R:\run\logs\checks.log" 2>&1*') 'output to checks.log'
Assert ($c -notlike '*DOTNET_ROOT*') 'no dotnet lines without a dotnet folder'
$c = Get-SiloChecksCmd 'py' '-3.12 ' 'R:\s' 'P' 'StationGodMCP.Server.dll' 'R:\o' 'R:\l' 'R:\s\tools\dotnet'
Assert ($c -like '*@set DOTNET_ROOT=R:\s\tools\dotnet*' -and $c -like '*"py" -3.12 "R:\s\tools*') 'dotnet and py -3.12'
Assert ($c -notlike '*--world-timeout*') 'no world timeout unless given'
$c = Get-SiloChecksCmd 'py' '' 'R:\s' 'P' 'h.exe' 'R:\o' 'R:\l' '' 420
Assert ($c -like '*--done "R:\o" --world-timeout 420 > "R:\l" 2>&1*') 'the world timeout goes to silo_checks.py'

# Get-SiloPhases / Get-SiloPhaseLimits: what the wrapper passes as -PhasesJson and -PhaseLimitJson, read back the way
# vm-client-run reads them (ConvertFrom-Json, each value [int]).
$phases = Get-SiloPhases 'StationGodMCP-VM.1'
Assert ((@($phases.Keys) -join ',') -eq 'bepinex,stationgod,checking,world,first,summary') 'phases in order'
Assert ('[Info   :StationGod] Pipe name: StationGodMCP-VM.1' -match $phases.stationgod) 'the pipe phase matches its log line'
Assert (-not ('Pipe name: StationGodMCP-VMx1' -match $phases.stationgod)) 'the pipe name is matched literally'
$read = @{}; (ConvertFrom-Json (Get-SiloPhaseLimits | ConvertTo-Json -Compress)).PSObject.Properties | % { $read[$_.Name] = [int]$_.Value }
Assert ($read.Count -eq 5 -and $read.bepinex -eq 300 -and $read.stationgod -eq 300 -and $read.checking -eq 480 -and $read.world -eq 300 -and $read.first -eq 300) 'limits survive the JSON round trip'
Assert (@($phases.Keys | Select-Object -SkipLast 1 | ? { -not $read.Contains($_) }).Count -eq 0) 'every phase but the last has a limit'

# The checks' phases against the lines silo_checks.py itself prints (progress, record, summary_line), in its order.
$py = @'
import sys
sys.path.insert(0, sys.argv[1])
import silo_checks as sc
sc.progress("connecting to StationGodMCP-VM, then waiting for the world to run")
sc.progress("waiting for the world: {}")
sc.progress("world running: {'game_time_s': 3.0}")
sc.progress("inspect contents on a silo")
sc.record("inspect contents on a silo", True)
sc.record("deposit silo_full past 600 entries", None, "why")
sc.record("withdraw food that rots", False, "got x")
print(sc.summary_line())
'@
# From a file, not -c: Windows PowerShell mangles quotes inside a native command's argument.
$pyFile = Join-Path ([IO.Path]::GetTempPath()) ('silo-lines-' + [guid]::NewGuid().ToString('N') + '.py')
Set-Content -LiteralPath $pyFile -Value $py -Encoding ascii
try { $ErrorActionPreference = 'Continue'; $lines = @(& py -3.12 $pyFile (Split-Path $PSScriptRoot) 2>&1 | % { "$_" }) }
finally { $ErrorActionPreference = 'Stop'; Remove-Item -LiteralPath $pyFile -ErrorAction SilentlyContinue }
Assert ($LASTEXITCODE -eq 0 -and $lines.Count -eq 8) "silo_checks.py printed its lines ($($lines.Count))"
$hit = [ordered]@{}
foreach ($l in $lines) { foreach ($k in $phases.Keys) { if (-not $hit.Contains($k) -and $l -match $phases[$k]) { $hit[$k] = $l } } }
Assert ((@($hit.Keys) -join ',') -eq 'checking,world,first,summary') "each checks phase matches a line, in order ($(@($hit.Keys) -join ','))"
Assert ($hit.summary -eq '3 checks: 2 passed or skipped, 1 failed') 'summary is the last line'
Assert (-not ('...   waiting for the world: {}' -match $phases.world)) 'waiting is not the world phase'
Assert (-not ('PASSED  x' -match $phases.first) -and ('SKIP  y' -match $phases.first)) 'first matches a result line only'
Assert ((Get-SiloChecksLog) -eq '{res}\logs\checks.log' -and (Get-SiloChecksLog 'C:\r\res\run1') -eq 'C:\r\res\run1\logs\checks.log') 'checks.log: the token the wrapper watches, the path the hook writes'
Assert (-not @($read.Keys | ? { @($phases.Keys) -notcontains $_ })) 'every limit names a phase'
Assert (-not @($read.Values | ? { $_ -lt 60 })) 'no limit under rig-lib''s 60 s minimum'
Assert ($read.Keys -notcontains @($phases.Keys)[-1]) 'none on the last phase (nothing is expected after it)'
Assert ($script:SiloStallSeconds -ge 300 -and $script:SiloStallSeconds -ge ($read.Values | Measure-Object -Maximum).Maximum) 'stall at least rig-lib''s 300 s and the longest phase limit'

# P46: expected length, timeout, the world's fail-fast bound; and the load argument.
Assert ((Get-SiloExpectedMinutes) -eq 6) 'expected about 6 minutes'
Assert ($read.checking -eq 480 -and $script:SiloStallSeconds -eq 900) 'checking 480, stall 900 (as the help says)'
Assert ((Get-SiloTimeoutSeconds) -eq 900) 'timeout: 6 minutes expected plus a 9 minute margin'
Assert ((Get-SiloTimeoutSeconds) -ge (Get-SiloExpectedMinutes) * 60 + $read.checking) 'the margin covers the world-loading limit'
Assert ((Get-SiloWorldTimeout) -lt $read.checking -and (Get-SiloWorldTimeout) -ge 60) 'world never loaded fails inside the checking limit'
Assert ((Get-SiloGameArgs) -eq '-load {savename}') 'loads by save name'
Assert ((Get-SiloGameArgs) -notmatch '\{save\}|\{savefile\}') 'never a folder or file path (LoadAtFullPath wants a .save file)'

Write-Host "$script:failed failed"
exit $script:failed
