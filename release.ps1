<#
.SYNOPSIS
  Build a tagged StationGod MCP version and upload it as a GitHub Release.

.DESCRIPTION
  GitHub Actions cannot build this mod (it compiles against the game's own assemblies), so releases are built here.

  For version X.Y.Z (default: the version in HEAD's StationGodMCP.csproj):
    1. Tag vX.Y.Z must exist, or HEAD must be the commit that bumped the csproj to X.Y.Z (the tag is then created
       as an annotated tag, which needs a clean working tree). The tag is pushed to the remote if missing there,
       and must point at the same commit on the remote and be contained in the remote's main.
    2. The tagged commit is checked out in a temporary git worktree (never the working tree, which may hold
       unfinished edits), built with its own build.ps1 and tested with dotnet test.
    3. Assets go to release\vX.Y.Z\:
         StationGodMCP.zip                   the mod folder, extract into the Stationeers mods folder
         StationGodMCP.Server-win-x64.zip    self-contained sidecar
         StationGodMCP.Server-portable.zip   sidecar for the .NET 8 runtime
         SHA256SUMS.txt                      sha256sum format
       Asset names carry no version, so /releases/latest/download/<name> always resolves to the newest.
    4. Notes: the version's CHANGELOG.md section plus an install and checksum footer.
    5. gh release create (latest; -Draft for a draft), or upload --clobber onto an existing release. A published
       release is only changed with -Clobber, since replacing its assets changes the checksums users have.
    6. Verifies every asset on GitHub: size, and sha256 of a fresh download against SHA256SUMS.txt.

  Never deletes or moves tags or releases and never force-pushes. Publishing a draft is a separate step:
  gh release edit vX.Y.Z --draft=false --latest

.EXAMPLE
  .\release.ps1 -Draft
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Draft,
    [switch]$Clobber,
    [string]$Remote = 'github',
    [string]$GameDir = $env:STATIONEERS_DIR
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Invoke-Git {
    # Continue: under Stop, PowerShell 5.1 turns any git stderr line (progress, hints) into a terminating error.
    $ErrorActionPreference = 'Continue'
    $output = & git.exe -C $root @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed: $($output -join ' ')" }
    $output | ForEach-Object { "$_" }
}
function Get-GitOutput {
    $ErrorActionPreference = 'Continue'
    $output = & git.exe -C $root @args 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $output | ForEach-Object { "$_" }
}
function VersionAt([string]$rev) {
    $csproj = Get-GitOutput show "${rev}:StationGodMCP.csproj"
    if (-not $csproj) { return $null }
    ([regex]::Match(($csproj -join "`n"), '<Version>([^<]+)</Version>')).Groups[1].Value
}
function Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

# The GitHub repository behind the remote, for gh -R.
$url = (Invoke-Git remote get-url $Remote) | Select-Object -First 1
$match = [regex]::Match($url, 'github\.com[:/]([^/]+/[^/]+?)(\.git)?$')
if (-not $match.Success) { throw "Remote '$Remote' ($url) is not a GitHub repository." }
$repo = $match.Groups[1].Value

if (-not $Version) { $Version = VersionAt 'HEAD' }
if (-not $Version) { throw 'No <Version> in HEAD:StationGodMCP.csproj; pass -Version.' }
$tag = "v$Version"

# 1. The tag: exists, or HEAD is the version commit and gets it now.
Invoke-Git fetch $Remote main --quiet | Out-Null
$commit = Get-GitOutput rev-parse --verify --quiet "refs/tags/$tag^{commit}"
if (-not $commit) {
    if ((VersionAt 'HEAD') -ne $Version) { throw "Tag $tag does not exist and HEAD is not version $Version." }
    if ((VersionAt 'HEAD~1') -eq $Version) {
        throw "Tag $tag does not exist and HEAD is not the commit that bumped to $Version; tag that commit by hand (git tag -a $tag -m $Version <commit>)."
    }
    if (Get-GitOutput status --porcelain) { throw "Tag $tag does not exist and the working tree is not clean; commit first." }
    Invoke-Git tag -a $tag -m $Version | Out-Null
    $commit = Get-GitOutput rev-parse --verify --quiet "refs/tags/$tag^{commit}"
    Write-Host "Tagged $tag on $commit"
}
$commit = "$commit".Trim()
if ((VersionAt $commit) -ne $Version) { throw "Tag $tag points at $commit, whose csproj version is $(VersionAt $commit)." }

$remoteTag = Invoke-Git ls-remote --tags $Remote "refs/tags/$tag" "refs/tags/$tag^{}"
if (-not $remoteTag) {
    Invoke-Git push $Remote "refs/tags/${tag}:refs/tags/$tag" | Out-Null
    $remoteTag = Invoke-Git ls-remote --tags $Remote "refs/tags/$tag" "refs/tags/$tag^{}"
    Write-Host "Pushed $tag to $Remote"
}
# An annotated tag lists its peeled commit as <tag>^{}; a lightweight one lists the commit itself.
$peeled = @($remoteTag | Where-Object { $_ -match '\^\{\}$' }) + @($remoteTag) | Select-Object -First 1
if (($peeled -split '\s+')[0] -ne $commit) { throw "Tag $tag on $Remote is not $commit locally: $($remoteTag -join '; '). Resolve by hand." }
& git.exe -C $root merge-base --is-ancestor $commit "$Remote/main"
if ($LASTEXITCODE -ne 0) { throw "$tag ($commit) is not on $Remote/main; push main first (git push $Remote main --follow-tags)." }

if (-not $GameDir) { $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Stationeers' }
$existing = $null
$ErrorActionPreference = 'Continue'
$view = & gh release view $tag -R $repo --json isDraft,url 2>$null
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE -eq 0) { $existing = $view | ConvertFrom-Json }
if ($existing -and -not $existing.isDraft -and -not $Clobber) {
    throw "Release $tag is published ($($existing.url)). Pass -Clobber to replace its assets."
}

# 2. Build and test the tagged commit in a worktree.
$work = Join-Path ([System.IO.Path]::GetTempPath()) "StationGodMCP-release-$tag"
$out = Join-Path $root "release\$tag"
$ErrorActionPreference = 'Continue'
& git.exe -C $root worktree remove --force $work 2>&1 | Out-Null
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
Invoke-Git worktree prune | Out-Null
Invoke-Git worktree add --detach $work $commit | Out-Null
try {
    Write-Host "Building $tag ($($commit.Substring(0, 7))) in $work"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $work 'build.ps1') -GameDir $GameDir
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed with exit code $LASTEXITCODE." }
    dotnet test (Join-Path $work 'tests\StationGodMCP.Tests\StationGodMCP.Tests.csproj') -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }

    # 3. Assets.
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
    New-Item -ItemType Directory -Path $out | Out-Null
    $package = Join-Path $work 'package'
    Copy-Item (Join-Path $package 'Sidecar\StationGodMCP.Server-win-x64.zip') $out
    Copy-Item (Join-Path $package 'Sidecar\StationGodMCP.Server-portable.zip') $out
    # The mod zip holds one StationGodMCP folder, so extracting it into the mods folder gives the right layout.
    $modStage = Join-Path $work 'release-stage\StationGodMCP'
    New-Item -ItemType Directory -Path $modStage -Force | Out-Null
    Copy-Item (Join-Path $package '*') $modStage -Recurse
    [System.IO.Compression.ZipFile]::CreateFromDirectory($modStage, (Join-Path $out 'StationGodMCP.zip'),
        [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $assets = @('StationGodMCP.zip', 'StationGodMCP.Server-win-x64.zip', 'StationGodMCP.Server-portable.zip')
    $sums = $assets | ForEach-Object { "$(Sha256 (Join-Path $out $_))  $_" }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), $utf8)
    $assets += 'SHA256SUMS.txt'

    # 4. Notes: the CHANGELOG section of this version, as tagged.
    $changelog = (Get-Content -LiteralPath (Join-Path $work 'CHANGELOG.md') -Raw -Encoding UTF8) -replace "`r`n", "`n"
    $section = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($Version))\s*\n(.*?)(?=^## |\z)")
    if (-not $section.Success) { throw "CHANGELOG.md has no '## $Version' section." }
    $install = "https://github.com/$repo#readme"
    if ($null -ne (Get-GitOutput cat-file -t "$Remote/main:docs/install.md")) { $install = "https://github.com/$repo/blob/main/docs/install.md" }
    $about = [xml](Get-Content -LiteralPath (Join-Path $work 'About\About.xml') -Raw -Encoding UTF8)
    $handle = "$($about.ModMetadata.WorkshopHandle)".Trim()
    $workshop = 'not published yet'
    if ($handle -and $handle -ne '0') { $workshop = "https://steamcommunity.com/sharedfiles/filedetails/?id=$handle" }
    $notes = $section.Groups[1].Value.Trim() + "`n`n---`n`n" +
        "**Install:** $install`n`n" +
        "- ``StationGodMCP.zip``: the mod; extract into ``Documents\My Games\Stationeers\mods``.`n" +
        "- ``StationGodMCP.Server-win-x64.zip``: the MCP sidecar as a self-contained Windows executable.`n" +
        "- ``StationGodMCP.Server-portable.zip``: the MCP sidecar for the .NET 8 runtime (``dotnet StationGodMCP.Server.dll``).`n" +
        "- ``SHA256SUMS.txt``: checksums of the three archives.`n`n" +
        "**Steam Workshop:** $workshop`n"
    $notesFile = Join-Path $out 'notes.md'
    [System.IO.File]::WriteAllText($notesFile, $notes, $utf8)

    # 5. Upload.
    $paths = $assets | ForEach-Object { Join-Path $out $_ }
    if ($existing) {
        & gh release upload $tag @paths -R $repo --clobber
        if ($LASTEXITCODE -ne 0) { throw "gh release upload failed with exit code $LASTEXITCODE." }
        & gh release edit $tag -R $repo --notes-file $notesFile
        if ($LASTEXITCODE -ne 0) { throw "gh release edit failed with exit code $LASTEXITCODE." }
    }
    else {
        $flags = @('--title', "StationGod MCP $Version", '--notes-file', $notesFile, '--verify-tag')
        if ($Draft) { $flags += '--draft' } else { $flags += '--latest' }
        & gh release create $tag @paths -R $repo @flags
        if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE." }
    }

    # 6. Verify what GitHub holds: every asset, its size, and its sha256 after a fresh download.
    $release = (& gh release view $tag -R $repo --json url,isDraft,assets) | ConvertFrom-Json
    $check = Join-Path $work 'release-check'
    New-Item -ItemType Directory -Path $check -Force | Out-Null
    & gh release download $tag -R $repo -D $check
    if ($LASTEXITCODE -ne 0) { throw "gh release download failed with exit code $LASTEXITCODE." }
    foreach ($name in $assets) {
        $remoteAsset = $release.assets | Where-Object { $_.name -eq $name }
        $local = Get-Item -LiteralPath (Join-Path $out $name)
        if (-not $remoteAsset) { throw "Asset $name is missing from the release." }
        if ($remoteAsset.size -ne $local.Length) { throw "Asset $name is $($remoteAsset.size) bytes on GitHub, $($local.Length) locally." }
        if ((Sha256 (Join-Path $check $name)) -ne (Sha256 $local.FullName)) { throw "Asset $name downloaded with a different sha256." }
        Write-Host ("  {0,-36} {1,12:N0} bytes  ok" -f $name, $local.Length)
    }
    foreach ($line in $sums) {
        $hash, $name = $line -split '  ', 2
        if ((Sha256 (Join-Path $check $name)) -ne $hash) { throw "$name does not match SHA256SUMS.txt." }
    }
    $state = if ($release.isDraft) { 'draft' } else { 'published' }
    Write-Host "Release $tag ($state): $($release.url)"
}
finally {
    $ErrorActionPreference = 'Continue'
    & git.exe -C $root worktree remove --force $work 2>&1 | Out-Null
    & git.exe -C $root worktree prune 2>&1 | Out-Null
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
