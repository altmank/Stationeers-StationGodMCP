# Default RigVM host folder: the sibling RigVM repo's host\ folder, found relative to the caller's script
# (walks up from it, so it works from the main checkout and from a .worktrees\<lane> checkout). No absolute path.
function Get-DefaultRigVMHost([string] $From) {
    for ($d = $From; $d; $d = Split-Path $d) {
        $c = Join-Path $d 'RigVM\host'
        if (Test-Path -LiteralPath $c) { return (Resolve-Path -LiteralPath $c).Path }
    }
    # Not found: the main-checkout layout, so the refusal names where it looked.
    return [IO.Path]::GetFullPath((Join-Path $From '..\..\..\RigVM\host'))
}

# A RigVM entry script runs in its own pwsh child process (P111: RigVM's host side is pwsh 7.5 only; never powershell.exe),
# so rig-lib's `exit` ends that child, not the caller. -Arguments is the entry script's named parameters; a hashtable can't
# cross a command line intact (arrays, JSON strings), so it goes through an Export-Clixml file and the child splats it.
# Output streams through the same console (no window); returns the script's exit code.
function Invoke-RigVMEntry {
    param(
        [Parameter(Mandatory)] [string] $Script,
        [hashtable] $Arguments = @{}
    )
    $argFile = Join-Path ([IO.Path]::GetTempPath()) ("sg-rigvm-args-" + [guid]::NewGuid().ToString('N') + '.xml')
    try {
        $Arguments | Export-Clixml -LiteralPath $argFile
        $q = { param([string] $s) "'" + $s.Replace("'", "''") + "'" }
        $command = "`$ProgressPreference = 'SilentlyContinue'; `$a = Import-Clixml -LiteralPath $(& $q $argFile); & $(& $q $Script) @a; exit `$LASTEXITCODE"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $pwsh = (Get-Process -Id $PID).Path
        & $pwsh -NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand $encoded | Out-Host
        return $LASTEXITCODE
    }
    finally {
        Remove-Item -LiteralPath $argFile -Force -ErrorAction SilentlyContinue
    }
}
