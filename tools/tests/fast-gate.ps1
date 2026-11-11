#Requires -Version 7.5
# The fast gate lane.ps1 land runs on the merged tree (tools\tests\protected.txt, P108): the .NET tests and the sg Python
# client's tests, from the tree root. Exit 0 only when both pass.
# Lattice.Api (compile-time only) is found the way build.ps1's callers set it: LATTICE_API_DIR, else the Lattice repo beside
# the main checkout (found from git's common dir, so it works from a lane worktree or a throwaway one in TEMP).
$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
if (-not $env:LATTICE_API_DIR) {
    $common = "$(& git -C $root rev-parse --path-format=absolute --git-common-dir 2>$null)".Trim()
    if ($common) {
        $guess = Join-Path (Split-Path (Split-Path $common)) 'Lattice\artifacts\Release\Lattice'
        if (Test-Path -LiteralPath (Join-Path $guess 'Lattice.Api.dll')) { $env:LATTICE_API_DIR = $guess }
    }
}
if (-not $env:LATTICE_API_DIR) { Write-Host 'fast-gate: no Lattice.Api.dll (set LATTICE_API_DIR)'; exit 2 }

$sw = [Diagnostics.Stopwatch]::StartNew()
dotnet test (Join-Path $root 'tests\StationGodMCP.Tests\StationGodMCP.Tests.csproj') -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host "fast-gate: .NET tests failed (exit $LASTEXITCODE)"; exit 1 }
Write-Host ("fast-gate: .NET tests passed ({0:n0} s)" -f $sw.Elapsed.TotalSeconds)

Push-Location (Join-Path $root 'clients\python')
try { & py -3 -m unittest discover -s tests; $py = $LASTEXITCODE } finally { Pop-Location }
if ($py -ne 0) { Write-Host "fast-gate: Python client tests failed (exit $py)"; exit 1 }
Write-Host ("fast-gate: all passed ({0:n0} s)" -f $sw.Elapsed.TotalSeconds)
exit 0
