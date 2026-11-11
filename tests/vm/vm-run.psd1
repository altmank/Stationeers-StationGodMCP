@{
    Schema = 1
    Mod = 'StationGodMCP'
    OwnerPrefix = 'sg'
    # rigvm-min (P105). Since 7feb8ef the kit runs Build.Command under pwsh 7 (P99); build.ps1 needs 7.5.
    RigVMPin = 'a122e5a'
    Build = @{
        Command = 'build.ps1'
        Package = 'package'
        # The kit caches a build by commit: the env vars a non-deploy build reads (build.ps1 and StationGodMCP.csproj).
        Inputs = @{ Env = @('STATIONEERS_DIR', 'LATTICE_API_DIR') }
    }
    # Lattice is optional: StationGod must load and answer calls with and without it (LESSONS: 2.10-2.16.0 answered
    # none without it). The kit's loadproof kind proves both.
    Deps = @{
        Required = @()
        Optional = @(@{ Name = 'Lattice'; Default = 'installed' })
    }
    Runs = @{}
}
