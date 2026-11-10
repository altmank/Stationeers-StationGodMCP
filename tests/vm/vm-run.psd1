@{
    Schema = 1
    Mod = 'StationGodMCP'
    OwnerPrefix = 'sg'
    RigVMPin = 'a68067e'
    Build = @{
        Command = 'build.ps1'
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
