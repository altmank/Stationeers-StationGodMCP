<#
.SYNOPSIS
  Build StationGod MCP and stage the Steam Workshop package.

.DESCRIPTION
  Runs every test (the verb catalogue's checks included), builds the mod DLL against the local game install, checks the
  one version is the same everywhere, and stages
  .\package\: the About and GameData folders, the DLL, the LICENSE and the Sidecar folder with both sidecar ZIPs.
  With -Deploy it also replaces Documents\My Games\Stationeers\mods\StationGodMCP with the package and installs the
  portable sidecar the MCP client runs into %LOCALAPPDATA%\StationGodMCP\server.
  A build records the commit it staged from, and whether the tree was clean, in package-build.txt beside .\package
  (not inside it: the package is exactly what the Workshop gets).

.PARAMETER DeployOnly
  Install the already staged .\package exactly as -Deploy does, and do nothing else: no test, build or staging. It
  refuses unless the package was staged from the commit at HEAD with a clean tree and the tree is still clean (run
  build.ps1 first); -Force does not skip that check.

.PARAMETER Target
  The mod folder -Deploy and -DeployOnly replace (default Documents\My Games\Stationeers\mods\StationGodMCP).

.PARAMETER LockScript
  The game-lock script both deploys take around the remove and copy, and only there (default
  $env:STATIONEERS_GAME_LOCK). It is called as <script> acquire|release -Owner <name>; acquire waits while another
  owner holds the lock. With no lock script found, a deploy refuses unless -NoLock or -LockHeld is passed.

.PARAMETER NoLock
  Deploy without any game lock: for a machine that has none, such as a player building from source.

.PARAMETER LockHeld
  The caller already holds the game lock for its whole run (a rig run that deploys inside it): the deploy then neither
  takes nor releases it. The lock is not re-entrant, so without this such a caller would wait on itself.

.EXAMPLE
  .\build.ps1 -Deploy
.EXAMPLE
  .\build.ps1; .\build.ps1 -DeployOnly
#>
[CmdletBinding()]
param(
    [string]$GameDir = $env:STATIONEERS_DIR,
    [switch]$Deploy,
    [switch]$DeployOnly,
    [switch]$Force,
    [string]$LockScript = $env:STATIONEERS_GAME_LOCK,
    [switch]$NoLock,
    [switch]$LockHeld,
    [string]$SidecarPath = (Join-Path $env:LOCALAPPDATA 'StationGodMCP\server'),
    [string]$Target = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers\mods\StationGodMCP')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$package = Join-Path $root 'package'
# Where a build records the commit it staged from: HEAD's hash, then 'clean' or 'dirty'.
$stamp = Join-Path $root 'package-build.txt'
# Whether a deploy takes the game lock itself: not when the caller holds it already or there is none to take.
$takeLock = -not ($LockHeld -or $NoLock)
if (($Deploy -or $DeployOnly) -and $takeLock -and -not ($LockScript -and (Test-Path -LiteralPath $LockScript))) {
    # Checked before any build, so a refused deploy wastes nothing and writes nothing.
    throw "Deploy refused: no game-lock script at '$LockScript'. Set STATIONEERS_GAME_LOCK or pass -LockScript, or pass -NoLock on a machine with no game lock."
}

# HEAD and whether the tree has changes, or $null outside a git checkout (a plain source copy still builds).
function Get-TreeState {
    try {
        $head = & git -C $root rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or -not $head) { return $null }
        $changes = & git -C $root status --porcelain
        if ($LASTEXITCODE -ne 0) { return $null }
    }
    catch { return $null }
    [pscustomobject]@{ Head = "$head".Trim(); State = $(if ($changes) { 'dirty' } else { 'clean' }) }
}

# Replaces $Target with the package's mod folder and copies the portable sidecar in $portableDir to $SidecarPath.
function Install-Package([string]$portableDir) {
    # A running game holds the old DLL and keeps running it; the player may be in game.
    # A brand-new mod the game has never loaded is the one safe exception: pass -Force.
    if (-not $Force -and (Get-Process -Name 'rocketstation' -ErrorAction SilentlyContinue)) {
        throw 'Stationeers is running: close the game before deploying (or -Force for a mod it has never loaded).'
    }
    # The game lock is held while a game runs or for a deploy copy, never across a build: take it only around the
    # remove and copy, whatever the target, and always give it back. acquire waits while another owner holds it.
    $owner = "stationgod-deploy $(Split-Path $root -Leaf) pid $PID"
    if ($takeLock) {
        & $LockScript acquire -Owner $owner | ForEach-Object { Write-Host "   $_" }
        if ($LASTEXITCODE -ne 0) { throw 'Deploy failed: could not take the game lock.' }
    }
    try {
        if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
        New-Item -ItemType Directory -Path $Target -Force | Out-Null
        Copy-Item "$package\*" $Target -Recurse
    }
    finally {
        if ($takeLock) { & $LockScript release -Owner $owner | ForEach-Object { Write-Host "   $_" } }
    }
    Write-Host "Deployed to $Target"

    # The tool list lives in the sidecar, so a new tool has to reach both halves.
    New-Item -ItemType Directory -Path $SidecarPath -Force | Out-Null
    # Recursively: the script workers' sg package ships in the sidecar's python\sg folder (run_script needs it).
    $portableRoot = (Get-Item -LiteralPath $portableDir).FullName
    foreach ($file in Get-ChildItem -LiteralPath $portableRoot -File -Recurse) {
        $destination = Join-Path $SidecarPath $file.FullName.Substring($portableRoot.Length).TrimStart('\', '/')
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        try {
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
        catch {
            # A running MCP client holds StationGodMCP.Server.dll open. Windows refuses the overwrite but allows a
            # rename, and the client loads the new file when it next starts. The dated name keeps each set-aside
            # copy, since an older client may still hold the one before.
            Rename-Item -LiteralPath $destination -NewName "$($file.Name).previous-$(Get-Date -Format yyyyMMddHHmmss)" -Force
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
    }
    Write-Host "Deployed sidecar to $SidecarPath (restart the MCP client to load it)"
}

if ($DeployOnly) {
    # Only a package staged from this exact commit, from a clean tree, may go in: anything else is not what HEAD says.
    $now = Get-TreeState
    if (-not $now) { throw "Deploy refused: $root is not a git checkout, so the package cannot be matched to a commit." }
    $recorded = if (Test-Path -LiteralPath $stamp) { @(Get-Content -LiteralPath $stamp) } else { @() }
    $zip = Join-Path $package 'Sidecar\StationGodMCP.Server-portable.zip'
    if (-not (Test-Path (Join-Path $package 'StationGodMCP.dll')) -or -not (Test-Path $zip) -or $recorded.Count -lt 2) {
        throw "Deploy refused: no staged package with a build record at $package. Run build.ps1 first."
    }
    if ($recorded[0] -ne $now.Head) {
        throw "Deploy refused: the package was staged from $($recorded[0]), HEAD is $($now.Head). Run build.ps1 first."
    }
    if ($recorded[1] -ne 'clean') {
        throw 'Deploy refused: the package was staged from a tree with uncommitted changes. Commit, then run build.ps1.'
    }
    if ($now.State -ne 'clean') {
        throw 'Deploy refused: the tree has uncommitted changes since the package was staged. Commit, then run build.ps1.'
    }
    $unpacked = Join-Path ([System.IO.Path]::GetTempPath()) ('StationGodMCP-deploy-' + [guid]::NewGuid().ToString('N'))
    try {
        # The portable zip holds the same files -Deploy copies from its publish folder.
        Expand-Archive -LiteralPath $zip -DestinationPath $unpacked
        Install-Package $unpacked
    }
    finally {
        if (Test-Path -LiteralPath $unpacked) { Remove-Item -LiteralPath $unpacked -Recurse -Force }
    }
    return
}

if (-not $GameDir) {
    $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Stationeers'
}
if (-not (Test-Path (Join-Path $GameDir 'rocketstation_Data\Managed\Assembly-CSharp.dll'))) {
    throw "Stationeers not found at '$GameDir'. Pass -GameDir or set STATIONEERS_DIR."
}

# One version in five places and nothing else keeps them in step. A published build and a local build sharing a
# version number cannot be told apart afterwards, and the MCP client sees only the sidecar's.
function Find([string]$path, [string]$pattern) {
    ([regex]::Match((Get-Content (Join-Path $root $path) -Raw), $pattern)).Groups[1].Value
}
$versions = [ordered]@{
    'StationGodMCP.csproj' = Find 'StationGodMCP.csproj' '<Version>([^<]+)</Version>'
    'StationGodMod.Version' = Find 'src\StationGodMCP.Mod\StationGodMod.cs' 'const string Version = "([^"]+)"'
    'About.xml' = Find 'About\About.xml' '<Version>([^<]+)</Version>'
    'Server.csproj' = Find 'src\StationGodMCP.Server\StationGodMCP.Server.csproj' '<Version>([^<]+)</Version>'
    'Program.ServerVersion' = Find 'src\StationGodMCP.Server\Program.cs' 'ServerVersion = "([^"]+)"'
}
# @() so a single shared version stays an array; indexing a bare string yields one char.
$distinct = @($versions.Values | Sort-Object -Unique)
if ($distinct.Count -ne 1 -or [string]::IsNullOrWhiteSpace($distinct[0])) {
    $detail = ($versions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '
    throw "Version mismatch: $detail. Bump all of them to the same value."
}
$version = $distinct[0]

# The Workshop page is the About.xml Description; the Workshop refuses 8000 characters or more, and LaunchPad
# checks the ChangeLog (the Steam change note) against the same limit.
$about = [xml](Get-Content (Join-Path $root 'About\About.xml') -Raw)
$pageLength = $about.ModMetadata.Description.Length
if ($pageLength -ge 8000) { throw "About.xml Description is $pageLength characters; the Workshop limit is under 8000." }
$changeLength = $about.ModMetadata.ChangeLog.Length
if ($changeLength -ge 8000) { throw "About.xml ChangeLog is $changeLength characters; the limit is under 8000." }

# Lattice's contract (Lattice.Api.dll) is referenced, never shipped: LATTICE_API_DIR, else the Lattice repo's build
# beside this one (Lattice's build.ps1 makes it). A worktree elsewhere (the deploy queue's under %TEMP%) looks beside
# the repo it belongs to.
$latticeDir = if ($env:LATTICE_API_DIR) { $env:LATTICE_API_DIR } else {
    Join-Path (Split-Path -Parent $root) 'Lattice\artifacts\Release\Lattice'
}
if (-not $env:LATTICE_API_DIR -and -not (Test-Path (Join-Path $latticeDir 'Lattice.Api.dll'))) {
    $common = & git -C $root rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($common) {
        $latticeDir = Join-Path (Split-Path -Parent (Split-Path -Parent $common)) 'Lattice\artifacts\Release\Lattice'
    }
}
if (-not (Test-Path (Join-Path $latticeDir 'Lattice.Api.dll'))) {
    throw "No Lattice.Api.dll in $latticeDir. Build Lattice (its build.ps1) or set LATTICE_API_DIR."
}
# The projects read the same folder.
$env:LATTICE_API_DIR = $latticeDir

# Every test, the verb catalogue's checks (VerbCatalogueTests) included: a verb, what or option that skips a rule, a
# generated/verbs or mod option file that is not current, or any failing test stops the build before anything is staged.
# Read before the tests and the build, which may write files: the record is what the sources were.
$tree = Get-TreeState
if (-not $tree) { $tree = [pscustomobject]@{ Head = 'unknown'; State = 'not a git checkout' } }
Write-Host "Testing $version"
dotnet test (Join-Path $root 'tests\StationGodMCP.Tests\StationGodMCP.Tests.csproj') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE; nothing was built." }

# The sg client's tests, test_generated among them: sg's verb layer stale against the catalogue (generate_catalogue.py
# not run after a catalogue change) stops the build too.
Push-Location (Join-Path $root 'clients\python')
try { cmd /c "py -3 -m unittest discover -s tests 2>&1" }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "sg client tests failed with exit code $LASTEXITCODE; nothing was built." }

Write-Host "Building $version against $GameDir (Workshop page $pageLength characters)"
dotnet build (Join-Path $root 'StationGodMCP.csproj') -c Release -p:StationeersPath="$GameDir" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
$dll = Join-Path $root 'StationGodMCP.dll'
if (-not (Test-Path $dll)) { throw "Build produced no DLL at $dll" }

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('StationGodMCP-sidecar-' + [guid]::NewGuid().ToString('N'))
$server = Join-Path $root 'src\StationGodMCP.Server\StationGodMCP.Server.csproj'
$portable = Join-Path $staging 'portable'
$native = Join-Path $staging 'win-x64'
try {
    # Portable: runs through the Microsoft-signed dotnet host, needs the .NET 8 runtime.
    dotnet publish $server -c Release --self-contained false -p:UseAppHost=false -p:DebugType=None `
        -p:DebugSymbols=false -p:StationGodServerRoot="$(Join-Path $staging 'build-portable')" `
        -p:PublishDir="$portable" -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Portable sidecar publish failed with exit code $LASTEXITCODE." }
    # Self-contained single executable: no runtime needed, but unsigned.
    dotnet publish $server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None `
        -p:DebugSymbols=false -p:StationGodServerRoot="$(Join-Path $staging 'build-native')" `
        -p:PublishDir="$native" -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Self-contained sidecar publish failed with exit code $LASTEXITCODE." }

    if (Test-Path $stamp) { Remove-Item $stamp -Force }
    if (Test-Path $package) { Remove-Item $package -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $package 'Sidecar') | Out-Null
    Copy-Item (Join-Path $root 'About') $package -Recurse
    Copy-Item (Join-Path $root 'GameData') $package -Recurse
    Copy-Item $dll $package
    Copy-Item (Join-Path $root 'LICENSE') $package
    # The default lint rules; a lint-rules.json next to a save overrides them (docs/lint-rules.md).
    Copy-Item (Join-Path $root 'lint-rules.json') $package
    # The sidecars ship zipped: LaunchPad scans a mod folder recursively for DLLs and would try to load theirs.
    foreach ($to in @((Join-Path $package 'Sidecar'), $portable, $native)) {
        Copy-Item (Join-Path $root 'Sidecar\README.txt') $to
    }
    Compress-Archive -Path (Join-Path $portable '*') -DestinationPath (Join-Path $package 'Sidecar\StationGodMCP.Server-portable.zip')
    Compress-Archive -Path (Join-Path $native '*') -DestinationPath (Join-Path $package 'Sidecar\StationGodMCP.Server-win-x64.zip')

    # Steam rejects workshop previews over 1 MB.
    $thumb = Join-Path $package 'About\thumb.png'
    if ((Test-Path $thumb) -and (Get-Item $thumb).Length -gt 1MB) {
        Write-Warning 'About\thumb.png is over 1 MB. Steam caps previews at 1 MB and the upload falls back to a blank image.'
    }
    Set-Content -LiteralPath $stamp -Value @($tree.Head, $tree.State) -Encoding ascii
    Write-Host "Staged $package from $($tree.Head) ($($tree.State) tree)"

    if ($Deploy) { Install-Package $portable }
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
