#Requires -Version 7.5
# Unit test for Invoke-RigVMEntry (tools\rigvm-host.ps1): a fake entry script records the edition it ran under and the
# parameters it bound, then exits 7. No VM. Run: pwsh -NoProfile -File tools\test-rigvm-host.ps1 (exit 0 = all held).
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'rigvm-host.ps1')
$failed = 0
function Assert-That([bool] $Ok, [string] $What) { if ($Ok) { Write-Host "ok    $What" } else { Write-Host "FAIL  $What"; $script:failed++ } }

$dir = Join-Path ([IO.Path]::GetTempPath()) ('sg-rigvm-host-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force "$dir\with space" | Out-Null
try {
    $fake = "$dir\with space\fake-entry's.ps1"
    $out = "$dir\out.json"
    Set-Content -LiteralPath $fake -Value @'
param([string] $Mod, [string] $Owner, [string[]] $Mods = @(), [string] $PhasesJson = '', [int] $ExpectedMinutes = 0,
      [switch] $Front, [switch] $Dxdiag, [string] $OutFile)
[pscustomobject]@{ Edition = $PSVersionTable.PSEdition; Version = "$($PSVersionTable.PSVersion)"; Mod = $Mod; Owner = $Owner
    Mods = @($Mods); Phases = $PhasesJson; Minutes = $ExpectedMinutes; Front = [bool]$Front; Dxdiag = [bool]$Dxdiag
    Module = $env:PSModulePath; Ctx = $env:SG_TEST_CTX } | ConvertTo-Json -Compress | Set-Content -LiteralPath $OutFile
exit 7
'@
    $json = '{"bepinex":"Chainloader startup complete","x":"Pipe name: Foo\.Bar"}'
    $env:SG_TEST_CTX = 'ctx-value'
    $before = $env:PSModulePath
    $code = Invoke-RigVMEntry -Script $fake -Arguments @{
        Mod = 'm'; Owner = 'sg-lane'; Mods = @('C:\a b\one', 'C:\two'); PhasesJson = $json; ExpectedMinutes = 6
        Front = $true; Dxdiag = $false; OutFile = $out }
    Remove-Item Env:SG_TEST_CTX
    $r = Get-Content -Raw -LiteralPath $out | ConvertFrom-Json
    Assert-That ($code -eq 7) 'exit code of the entry script comes back (7)'
    Assert-That ($r.Edition -eq 'Desktop' -and $r.Version -like '5.1*') "entry ran under Windows PowerShell 5.1 (got $($r.Edition) $($r.Version))"
    Assert-That ($r.Mod -eq 'm' -and $r.Owner -eq 'sg-lane') 'string parameters bind'
    Assert-That (@($r.Mods).Count -eq 2 -and $r.Mods[0] -eq 'C:\a b\one' -and $r.Mods[1] -eq 'C:\two') 'string[] parameter binds'
    Assert-That ($r.Phases -ceq $json) 'JSON string arrives byte for byte'
    Assert-That ($r.Minutes -eq 6) 'int parameter binds'
    Assert-That ($r.Front -eq $true -and $r.Dxdiag -eq $false) 'switch parameters bind (true and false)'
    Assert-That ($r.Ctx -eq 'ctx-value') 'env vars reach the child'
    Assert-That (-not (@($r.Module -split ';') | Where-Object { $_ -notlike '*' + [char]92 + 'WindowsPowerShell' + [char]92 + '*' })) 'child PSModulePath holds WindowsPowerShell folders only'
    Assert-That ($env:PSModulePath -ceq $before) "parent's PSModulePath restored"
    Assert-That (-not (Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'sg-rigvm-args-*.xml' -ErrorAction SilentlyContinue)) 'temp argument file deleted'
}
finally { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue }
exit $failed
