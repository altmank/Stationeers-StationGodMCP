<#
.SYNOPSIS
  Build StationGod MCP and stage the Steam Workshop package.

.DESCRIPTION
  Builds the mod DLL against the local game install, checks the one version is the same everywhere, and stages
  .\package\: the About and GameData folders, the DLL, the LICENSE and the Sidecar folder with both sidecar ZIPs.
  With -Deploy it also replaces Documents\My Games\Stationeers\mods\StationGodMCP with the package and installs the
  portable sidecar the MCP client runs into %LOCALAPPDATA%\StationGodMCP\server.

.EXAMPLE
  .\build.ps1 -Deploy
#>
[CmdletBinding()]
param(
    [string]$GameDir = $env:STATIONEERS_DIR,
    [switch]$Deploy,
    [switch]$Force,
    [string]$SidecarPath = (Join-Path $env:LOCALAPPDATA 'StationGodMCP\server')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

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

Write-Host "Building $version against $GameDir (Workshop page $pageLength characters)"
dotnet build (Join-Path $root 'StationGodMCP.csproj') -c Release -p:StationeersPath="$GameDir" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
$dll = Join-Path $root 'StationGodMCP.dll'
if (-not (Test-Path $dll)) { throw "Build produced no DLL at $dll" }

$package = Join-Path $root 'package'
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
    Write-Host "Staged $package"

    if ($Deploy) {
        # A running game holds the old DLL and keeps running it; the player may be in game.
        # A brand-new mod the game has never loaded is the one safe exception: pass -Force.
        if (-not $Force -and (Get-Process -Name 'rocketstation' -ErrorAction SilentlyContinue)) {
            throw 'Stationeers is running: close the game before deploying (or -Force for a mod it has never loaded).'
        }
        $mods = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers\mods\StationGodMCP'
        if (Test-Path $mods) { Remove-Item $mods -Recurse -Force }
        New-Item -ItemType Directory -Path $mods -Force | Out-Null
        Copy-Item "$package\*" $mods -Recurse
        Write-Host "Deployed to $mods"

        # The tool list lives in the sidecar, so a new tool has to reach both halves.
        New-Item -ItemType Directory -Path $SidecarPath -Force | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $portable -File) {
            $destination = Join-Path $SidecarPath $file.Name
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
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
