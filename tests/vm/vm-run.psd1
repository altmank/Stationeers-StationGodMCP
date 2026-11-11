@{
    Schema = 1
    Mod = 'StationGodMCP'
    OwnerPrefix = 'sg'
    RigVMPin = 'a68067e'
    Build = @{
        # The kit runs this under 5.1 (powershell.exe; a bare x.ps1 becomes & .\x.ps1), and build.ps1 needs pwsh 7.5.
        Command = 'pwsh -NoProfile -ExecutionPolicy Bypass -File .\build.ps1'
        Package = 'package'
    }
    # Lattice is optional: StationGod must load and answer calls with and without it (LESSONS: 2.10-2.16.0 answered
    # none without it). The kit's loadproof kind proves both.
    Deps = @{
        Required = @()
        Optional = @(@{ Name = 'Lattice'; Default = 'installed' })
    }
    Runs = @{}
}
