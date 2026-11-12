#Requires -Version 7.5
# The fast gate lane.ps1 land runs on the merged tree (tools\tests\protected.txt, P108): the .NET tests, the sg Python
# client's tests and a Release compile of the mod project (StationGodMCP.csproj), from the tree root. Exit 0 only when all three pass.
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
Write-Host ("fast-gate: Python client tests passed ({0:n0} s)" -f $sw.Elapsed.TotalSeconds)

# The mod project itself: the tests compile only the Pure and Shared sources, so a break in the game-facing code (a missing
# game assembly reference, a polyfill) passed this gate once (KeyGuard, 11-12). One Release build the way build.ps1 does it
# (same dotnet call, same game folder rule), no packaging or deploy. A missing game folder is a failure, never a skip.
$gameDir = if ($env:STATIONEERS_DIR) { $env:STATIONEERS_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Stationeers' }
if (-not (Test-Path -LiteralPath (Join-Path $gameDir 'rocketstation_Data\Managed\Assembly-CSharp.dll'))) {
    Write-Host "fast-gate: Stationeers game assemblies not found at '$gameDir' (set STATIONEERS_DIR); the mod project cannot be compiled"
    exit 2
}
dotnet build (Join-Path $root 'StationGodMCP.csproj') -c Release -p:StationeersPath="$gameDir" -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host "fast-gate: the mod project failed to compile (exit $LASTEXITCODE)"; exit 1 }
Write-Host ("fast-gate: all passed ({0:n0} s)" -f $sw.Elapsed.TotalSeconds)
exit 0
