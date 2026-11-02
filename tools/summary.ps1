# -Summary for build.ps1 and release.ps1 (dot-sourced by both): the script runs again as a child, without -Summary,
# its whole output going to a log; the console gets each step and whether it passed, the test totals, failing test
# names, the warning and error counts, and a last line naming the log. The exit code is the child's.

# The child's parameters: every bound one but -Summary, as PowerShell source (values single-quoted).
function Get-ChildArguments([System.Collections.IDictionary]$bound) {
    $list = foreach ($key in $bound.Keys) {
        if ($key -eq 'Summary') { continue }
        $value = $bound[$key]
        if ($value -is [System.Management.Automation.SwitchParameter]) {
            if ($value.IsPresent) { "-$key" }
        }
        else { "-$key"; "'" + ("$value" -replace "'", "''") + "'" }
    }
    @($list | Where-Object { $null -ne $_ })
}

# Runs $script with $arguments, everything it prints going to $log. Returns the exit code.
function Invoke-Logged([string]$script, [string[]]$arguments, [string]$log) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $log) -Force | Out-Null
    # No parameters reach here as $null, not an empty array: a function's empty output is nothing.
    $arguments = @($arguments | Where-Object { $_ })
    if ($log -match '["%]') { throw "-Summary cannot write a log whose path holds a double quote or %: $log" }
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
    # The script runs under try/catch rather than -File: PowerShell 5.1 wraps an uncaught error's message at the
    # console width, and the catch writes it on one line. The exit codes are -File's: neither script calls exit, so
    # it ends with 0 or throws, which -File turns into 1. The try/catch goes in a runner file beside the log, so cmd
    # quotes only two paths (-Command or -EncodedCommand would need more quoting, or write stderr as CLIXML).
    $quoted = "'" + ($script -replace "'", "''") + "'"
    $runner = "$log.run.ps1"
    $body = "try { & $quoted $($arguments -join ' '); exit 0 }`r`ncatch { [Console]::Out.WriteLine('summary: error: ' + `$_.Exception.Message); exit 1 }`r`n"
    # With a BOM, so Windows PowerShell reads a non-ASCII path in it as UTF-8.
    [System.IO.File]::WriteAllText($runner, $body, (New-Object System.Text.UTF8Encoding($true)))
    try {
        # cmd's own redirection writes the log, so stderr stays plain text. The child shares this console: no window.
        $line = "chcp 65001 >nul & powershell -NoProfile -ExecutionPolicy Bypass -File `"$runner`" > `"$log`" 2>&1"
        & cmd.exe /d /s /c "`"$line`""
        $LASTEXITCODE
    }
    finally { Remove-Item -LiteralPath $runner -Force -ErrorAction SilentlyContinue }
}

# Prints the summary of $log. $steps: ordered @{ Name; Start } where Start is the regex of the line that shows the
# step began (searched after the previous step's start); the first step's Start is ignored.
function Write-Summary([string]$log, [object[]]$steps, [int]$code) {
    # @() around the whole if: assigning an if's output unrolls a one-line log into a bare string.
    $lines = @(if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Encoding UTF8 })

    # Steps: one started and followed by the next passed; the last one started passed only if the run did.
    $at = @(0)
    for ($i = 1; $i -lt $steps.Count; $i++) {
        $found = -1
        if ($at[$i - 1] -ge 0) {
            for ($j = $at[$i - 1]; $j -lt $lines.Count; $j++) { if ($lines[$j] -match $steps[$i].Start) { $found = $j; break } }
        }
        $at += $found
    }
    $last = 0
    for ($i = 0; $i -lt $steps.Count; $i++) { if ($at[$i] -ge 0) { $last = $i } }
    for ($i = 0; $i -lt $steps.Count; $i++) {
        $state = if ($at[$i] -lt 0) { 'not run' } elseif ($i -lt $last -or $code -eq 0) { 'PASS' } else { 'FAIL' }
        Write-Host ('  {0,-28} {1}' -f $steps[$i].Name, $state)
    }

    # Test totals: one line per dotnet test run and per Python unittest run.
    $failing = [System.Collections.Generic.List[string]]::new()
    $ran = $null
    foreach ($l in $lines) {
        $m = [regex]::Match($l, '^\s*(Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+)')
        if ($m.Success) { Write-Host ('  .NET tests    passed {0}  failed {1}  skipped {2}' -f $m.Groups[3].Value, $m.Groups[2].Value, $m.Groups[4].Value) }
        # xUnit's "[xUnit.net 00:00:01.48]     Name [FAIL]" at -v q; VSTest's "  Failed Name [12 ms]" at higher verbosity.
        $m = [regex]::Match($l, '^\[xUnit\.net [^\]]*\]\s+(\S.*?) \[FAIL\]\s*$')
        if (-not $m.Success) { $m = [regex]::Match($l, '^\s+Failed (\S.*?) \[[^\]]*\]\s*$') }
        if ($m.Success -and -not $failing.Contains($m.Groups[1].Value)) { $failing.Add($m.Groups[1].Value) }
        $m = [regex]::Match($l, '^(FAIL|ERROR): (.+)$')
        if ($m.Success) { $failing.Add($m.Groups[2].Value) }
        $m = [regex]::Match($l, '^Ran (\d+) tests? in ')
        if ($m.Success) { $ran = [int]$m.Groups[1].Value }
        $m = [regex]::Match($l, '^(OK|FAILED)(?: \((.*)\))?\s*$')
        if ($m.Success -and $null -ne $ran) {
            $count = @{ failures = 0; errors = 0; skipped = 0; 'expected failures' = 0; 'unexpected successes' = 0 }
            foreach ($part in ($m.Groups[2].Value -split ',\s*')) {
                $kv = $part -split '=', 2
                if ($kv.Count -eq 2) { $count[$kv[0].Trim()] = [int]$kv[1] }
            }
            $failed = $count.failures + $count.errors + $count['unexpected successes']
            $passed = $ran - $failed - $count.skipped - $count['expected failures']
            Write-Host ('  Python tests  passed {0}  failed {1}  skipped {2}' -f $passed, $failed, $count.skipped)
            $ran = $null
        }
    }
    if ($failing.Count) {
        Write-Host "  failing tests ($($failing.Count)):"
        $failing | Select-Object -First 20 | ForEach-Object { Write-Host "    $_" }
        if ($failing.Count -gt 20) { Write-Host "    ... $($failing.Count - 20) more in the log" }
    }

    # Compiler warnings and errors, each counted once (MSBuild repeats them per project and per pass); PowerShell
    # warnings; and the error that stopped the script, which Invoke-Logged's catch writes on one line.
    $strip = { param($s) ($s -replace '\s*\[[^\]]+\.(cs|fs|vb)proj\]\s*$', '').Trim() }
    $warnings = [System.Collections.Generic.HashSet[string]]::new()
    $errors = [System.Collections.Generic.HashSet[string]]::new()
    for ($j = 0; $j -lt $lines.Count; $j++) {
        $l = $lines[$j]
        if ($l -match ':\s+warning\s+[A-Za-z]+\d+\s*:' -or $l -match '^WARNING: ') { [void]$warnings.Add((& $strip $l)) }
        elseif ($l -match ':\s+error\s+[A-Za-z]+\d+\s*:') { [void]$errors.Add((& $strip $l)) }
        elseif ($l -match '^summary: error: (.*)$') { [void]$errors.Add($Matches[1].Trim()) }
    }
    Write-Host ('  warnings {0}  errors {1}' -f $warnings.Count, $errors.Count)
    $errors | Select-Object -First 20 | ForEach-Object { Write-Host "    $(if ($_.Length -gt 200) { $_.Substring(0, 197) + '...' } else { $_ })" }
}
