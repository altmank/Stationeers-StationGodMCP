#Requires -Version 7.5
# release.ps1 must never create or push a tag: a tag made before the build and tests pass burns the version (v2.21.0).
# Runs a copy of release.ps1 in a throwaway repo with a local bare "GitHub" remote, stub build.ps1, stub dotnet and
# stub gh (on PATH ahead of the real ones). Never touches the real repo's tags or GitHub. Everything lives in one fresh
# folder under the system temp, removed at the end.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$releaseSrc = Join-Path (Split-Path -Parent (Split-Path -Parent $here)) 'release.ps1'
$base = Join-Path ([System.IO.Path]::GetTempPath()) ("release-order-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $base | Out-Null
$fails = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "  ok    $name" } else { Write-Host "  FAIL  $name $detail"; $script:fails++ }
}
function G {
    param([string]$Dir)
    $o = & git.exe -C $Dir @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $args failed: $o" }
    $o
}
function Write-Lf([string]$path, [string]$text) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

try {
    $stubs = Join-Path $base 'stubs'
    $ghLog = Join-Path $base 'gh.log'
    $store = Join-Path $base 'ghstore'
    $buildLog = Join-Path $base 'build.log'
    New-Item -ItemType Directory -Path $stubs, $store | Out-Null
    # gh stub: records every call; `create` keeps the assets; `view --json ...` answers from them.
    Write-Lf (Join-Path $stubs 'gh-stub.ps1') @'
$log = $env:STUB_GH_LOG; $store = $env:STUB_GH_STORE
Add-Content -LiteralPath $log -Value ($args -join ' ')
$cmd = "$($args[0]) $($args[1])"
if ($cmd -eq 'release create') {
    $files = @(); $target = ''
    for ($i = 3; $i -lt $args.Count; $i++) {
        if ($args[$i] -in '-R', '--title', '--notes-file') { $i++; continue }
        if ($args[$i] -eq '--target') { $target = $args[$i + 1]; $i++; continue }
        if ($args[$i] -like '-*') { continue }
        $files += $args[$i]
    }
    foreach ($f in $files) { Copy-Item -LiteralPath $f $store }
    Set-Content -LiteralPath (Join-Path $store '_target') $target
    Set-Content -LiteralPath (Join-Path $store '_draft') "$($args -contains '--draft')"
    exit 0
}
if ($cmd -eq 'release view') {
    if (-not (Test-Path (Join-Path $store '_target'))) { [Console]::Error.WriteLine('release not found'); exit 1 }
    if ($args -contains 'isDraft,url') { '{"isDraft":true,"url":"https://example/draft"}'; exit 0 }
    $assets = Get-ChildItem -LiteralPath $store -File | Where-Object { $_.Name -notlike '_*' } | ForEach-Object { @{ name = $_.Name; size = $_.Length } }
    @{ url = 'https://example/draft'; isDraft = ((Get-Content (Join-Path $store '_draft')) -eq 'True'); assets = @($assets);
       targetCommitish = (Get-Content (Join-Path $store '_target')) } | ConvertTo-Json -Depth 4 -Compress
    exit 0
}
if ($cmd -eq 'release download') {
    $dir = $args[$args.IndexOf('-D') + 1]
    Get-ChildItem -LiteralPath $store -File | Where-Object { $_.Name -notlike '_*' } | Copy-Item -Destination $dir
    exit 0
}
[Console]::Error.WriteLine("gh stub: unexpected call $args"); exit 9
'@
    Write-Lf (Join-Path $stubs 'gh.cmd') "@pwsh -NoProfile -File `"%~dp0gh-stub.ps1`" %*`r`n"
    Write-Lf (Join-Path $stubs 'dotnet.cmd') "@exit /b 0`r`n"
    $env:STUB_GH_LOG = $ghLog; $env:STUB_GH_STORE = $store; $env:STUB_BUILD_LOG = $buildLog
    $env:PATH = "$stubs;$env:PATH"

    # The throwaway repo. Its remote URL contains github.com/ so release.ps1's remote check accepts the local bare repo.
    $bare = (Join-Path $base 'github.com/test/repo.git') -replace '\\', '/'
    $repo = Join-Path $base 'repo'
    New-Item -ItemType Directory -Path $bare, $repo | Out-Null
    G $bare init --bare -b main | Out-Null
    G $repo init -b main | Out-Null
    G $repo config user.name tester | Out-Null
    G $repo config user.email tester@users.noreply.github.com | Out-Null
    G $repo config core.autocrlf false | Out-Null
    G $repo remote add origin $bare | Out-Null
    Copy-Item -LiteralPath $releaseSrc (Join-Path $repo 'release.ps1')
    Write-Lf (Join-Path $repo '.gitignore') ".worktrees/`nrelease/`npackage/`nobj/`n"
    Write-Lf (Join-Path $repo 'About/About.xml') "<ModMetadata><WorkshopHandle>0</WorkshopHandle></ModMetadata>`n"
    Write-Lf (Join-Path $repo 'tests/StationGodMCP.Tests/StationGodMCP.Tests.csproj') "<Project/>`n"
    Write-Lf (Join-Path $repo 'tools/summary.ps1') "# unused: -Summary is not run here`n"
    Write-Lf (Join-Path $repo 'build.ps1') @'
param([string]$GameDir)
Add-Content -LiteralPath $env:STUB_BUILD_LOG -Value (git -C $PSScriptRoot rev-parse HEAD)
if ($env:STUB_BUILD_FAIL) { exit 1 }
$p = Join-Path $PSScriptRoot 'package\Sidecar'
New-Item -ItemType Directory -Path $p -Force | Out-Null
foreach ($n in 'StationGodMCP.Server-win-x64.zip', 'StationGodMCP.Server-portable.zip') { Set-Content -LiteralPath (Join-Path $p $n) $n }
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'package\mod.txt') 'mod'
'@
    function Commit-Version([string]$v) {
        Write-Lf (Join-Path $repo 'StationGodMCP.csproj') "<Project><PropertyGroup><Version>$v</Version></PropertyGroup></Project>`n"
        Write-Lf (Join-Path $repo 'CHANGELOG.md') "# Changelog`n`n## $v`n`n- test change.`n"
        G $repo add -A | Out-Null
        G $repo commit -m "Version $v" | Out-Null
        "$(G $repo rev-parse HEAD)".Trim()
    }
    function Run-Release([string[]]$ReleaseArgs, [bool]$FailBuild = $false) {
        Remove-Item -LiteralPath $ghLog, $buildLog -Force -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath $store -Force | Remove-Item -Force
        $env:STUB_BUILD_FAIL = if ($FailBuild) { '1' } else { '' }
        $out = & pwsh -NoProfile -File (Join-Path $repo 'release.ps1') @ReleaseArgs 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
    }
    function Tags([string]$dir) { @(& git.exe -C $dir tag --list) }
    function GhCalls { if (Test-Path $ghLog) { @(Get-Content -LiteralPath $ghLog) } else { @() } }

    $v1 = Commit-Version '1.0.0'
    G $repo push origin main | Out-Null

    Write-Host 'case: draft, build fails'
    $r = Run-Release @('-Draft') $true
    Check 'exits non-zero' ($r.Code -ne 0) $r.Text
    Check 'says the build failed' ($r.Text -match 'build\.ps1 failed') $r.Text
    Check 'no local tag' ((Tags $repo).Count -eq 0)
    Check 'no remote tag' ((Tags $bare).Count -eq 0)
    Check 'no gh release create' (-not (GhCalls | Where-Object { $_ -match 'release create' }))

    Write-Host 'case: draft, all pass'
    $r = Run-Release @('-Draft')
    Check 'exits zero' ($r.Code -eq 0) $r.Text
    $create = @(GhCalls | Where-Object { $_ -match '^release create v1\.0\.0 ' })
    Check 'one gh release create' ($create.Count -eq 1)
    Check 'create is a draft targeting the commit' ($create.Count -eq 1 -and $create[0] -match '--draft' -and $create[0] -match "--target $v1")
    Check 'create does not demand an existing tag' ($create.Count -eq 1 -and $create[0] -notmatch '--verify-tag')
    Check 'no local tag' ((Tags $repo).Count -eq 0)
    Check 'no remote tag' ((Tags $bare).Count -eq 0)
    Check 'worktree removed' (-not (Test-Path (Join-Path $repo '.worktrees\release-v1.0.0')))

    Write-Host 'case: not the version commit'
    Write-Lf (Join-Path $repo 'extra.txt') "x`n"
    G $repo add -A | Out-Null
    G $repo commit -m 'past the bump' | Out-Null
    G $repo push origin main | Out-Null
    $r = Run-Release @('-Draft')
    Check 'refused' ($r.Code -ne 0 -and $r.Text -match 'not the commit that bumped') $r.Text
    Check 'no build, no create, no tags' (-not (Test-Path $buildLog) -and -not (GhCalls | Where-Object { $_ -match 'release create' }) -and (Tags $repo).Count -eq 0 -and (Tags $bare).Count -eq 0)

    Write-Host 'case: existing tag'
    G $repo tag -a v1.0.0 -m 1.0.0 $v1 | Out-Null
    $r = Run-Release @('-Draft')
    Check 'exits zero' ($r.Code -eq 0) $r.Text
    Check 'built the tag commit, not HEAD' ((Test-Path $buildLog) -and (@(Get-Content $buildLog)[0] -eq $v1))
    $create = @(GhCalls | Where-Object { $_ -match '^release create v1\.0\.0 ' })
    Check 'create verifies the tag and has no --target' ($create.Count -eq 1 -and $create[0] -match '--verify-tag' -and $create[0] -notmatch '--target')
    Check 'the existing tag is pushed, as before' ((Tags $bare) -contains 'v1.0.0')
}
finally {
    $env:STUB_BUILD_FAIL = ''
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fails) { Write-Host "test-release-order: $fails check(s) FAILED"; exit 1 }
Write-Host 'test-release-order: all passed'
exit 0
