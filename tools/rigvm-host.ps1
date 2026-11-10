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
