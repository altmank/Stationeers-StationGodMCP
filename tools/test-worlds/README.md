# Test worlds

StationGod's purpose-built test worlds (P36: no test uses LU's Vulcan2_4 base). Each lives in the world library,
`<library>\<world>\`: the save folder `<world>\`, `README.md` (what it holds and where, how it was built), `md5.txt`
and `fixtures.json` (each fixture's role and reference id).

The library is `--library <folder>`, else the environment variable `STATIONGOD_TEST_WORLDS`; with neither the tools
refuse. They write nothing outside it. The gate suite finds `sg-gate\fixtures.json` there (or `--fixtures`, or
`SG_GATE_FIXTURES`). Its world-load case runs `run_gate.py --reset <command>` (else `SG_GATE_RESET`), which must
reload a fresh copy of the sg-gate world on the test instance and return 0; the rig's run tool provides it.

| World | For | Built by |
| --- | --- | --- |
| `sg-silo` | `tools/silo-rig/silo_checks.py`, the food-rot check | `builder.silo` |
| `sg-minimal` | the release load check, with and without Lattice | `builder.minimal` |
| `sg-gate` | `tests/gate` (its `FIX` table) | `builder.gate` |
| `sg-cost-large` | the scoreboard's cost gate (larger world) | `make_large_world.py` from `sg-silo` |

## Landed worlds only (P51)

No test plays the landing. Every stored world was saved after the lander settled, with the player out of the seat and
standing. The world builders are the one allowed place for `-newgame` or `-new`, because a world has to be made once:
`vm-build-world.ps1` passes RigVM's `-WorldBuild`, and no test may start from them; a test loads a stored world.

- A builder waits for the lander to hold still, moves the player out of the seat onto clear ground, lays its floor 4 m
  above that ground (higher only if the report refuses it), and reads everything again before saving.
- `build_world.py store` reads the save itself and refuses a seated player, a player more than 2 m from the settled
  ground, or a lander away from its settled y. Ground is the height where the player settled: no verb reads terrain.
- `build_world.py verify --world <world>` runs that test on a stored world, with no game.
- `vm-build-world.ps1 -World <world> -Check` loads a stored world and fails if the player arrives seated, a lander
  descends, or the thing count differs from the save's (it names the prefabs that differ). The save's count leaves out
  organs (OrganBrain, OrganLungs, OrganStomach: the player's body), because `find thing` never keeps them.

## Rebuilding one (after a game patch)

Each world starts from a new world, never a copy. One game run, holding the game lock for its whole length, on a
test rig, never a server or save anyone plays. Two ways; both end in the same `build_world.py store`.

### On a VM rig, with a client as host (no server)

`vm-build-world.ps1 -World <world> -Mods <StationGod build> -Hub <hub build>
[-RigVMCommit <minimum>] [-LockWaitMinutes 120]`, with the rest given as parameters or environment variables (`STATIONGOD_TEST_WORLDS`; `SG_RIG_SETTINGS`; `SG_RIG_PYTHON_ZIP`; `SG_RIG_DOTNET_ZIP`; `Get-Help`
lists them). Start it in the background through `run-hidden.ps1` (a run takes longer than a foreground call).

It is a thin caller of RigVM's shared client run, `host\vm-client-run.ps1` (pool lock, the pool picks the rig, transcript,
result.json, results disk, client copy, settings without host values, watcher). What belongs to the world build is in
four hooks, `vm\prelaunch.ps1`, `vm\onpoll.ps1`, `vm\judge.ps1` and `vm\onend.ps1` (always run: removes
the builder's task). The client starts with the game's own launch
command `-newgame Lunar Creative` (`Util.Commands.NewGameCommand`), so StationGod runs as host (`role host, dedicated
false`) and the client's body is the player. Once StationGod's pipe is up, `build_world.py build` runs in the guest
(it waits for the world's player first); its `done-ok` brings the save and manifest back, and they are stored. Exit 0
pass, 1 the builder failed (reason in result.json), 2 timeout/stall/crash, 3 infra.

The guest needs nothing installed: `-PythonZip` (a Python 3.12 with its standard library: the embeddable zip, or a
stdlib-only zip of an install) and either a self-contained hub (`dotnet publish src/StationGodMCP.Server -r win-x64
--self-contained`) or `-DotnetZip` are unzipped onto the run's own results disk each run.

`-World sg-smoke` is the first run on a rig: a new world, StationGod's welcome (role, dedicated), the player, a save
as `sg-smoke` copied back into the run folder; nothing built, nothing stored.

### On a dedicated server

1. Start a test dedicated server with StationGod (no other mod: every test world is vanilla plus StationGod) on its own
   pipe: `rocketstation_DedicatedServer.exe -batchmode -nographics -logFile <log> -new Lunar Creative`.
   Creative, because StationGod's cheats (free building, `build item`, `move gas`, `move player`) run only in a
   creative world unless someone types `stationgod cheats on` at the game's console, which a headless server can't.
2. Join one Steam-off client once (`-batchmode -nographics`, to 127.0.0.1), so the world has a player character: a
   dedicated server makes none. It may stay joined through the build.
3. `py -3.12 build_world.py build --world <world> --pipe <pipe> --hub <sidecar> --manifest <run>\manifest.json
   [--saves <server saves>] [--done <dir>]`. It checks every prefab it needs first and changes nothing if one is
   missing; builds the fixtures on a floor of frames 12 m above the player's spawn (`--lift`); sets the world's
   difficulty (`sg-silo`, `sg-minimal`: Normal; `sg-gate`: stays Creative, its suite moves gas); and saves as
   `<world>`.
4. Stop the client and the server, release the lock.
5. `py -3.12 build_world.py store --world <world> --save-folder <server saves>\<world> --manifest <run>\manifest.json`
   copies the save (a folder named `<world>`) into the library and writes its README, md5 and fixtures.

### sg-cost-large

`py -3.12 make_large_world.py` (reads `sg-silo` from the library; `--world sg-minimal` also works). A file step, no game.

Tests: `py -3.12 -m unittest discover -s tools/test-worlds -p "test_*.py"` (a fake game; no game run).

## Known gaps

- `sg-gate` is vanilla: its Open cases use a powered glass door (airlock if the game has no glass door) in the open,
  vacuum on both sides on Lunar; its Setting cases use two logic memories (`mem1`, `mem2`).
- The tray's pumpkin has no water or light: it is planted (what the silo check needs), not tended.
- Not yet run on a rig: whether the rig's Steam-off client hosts and saves a local world and when `-newgame` fires
  (once the game manager is up). `-World sg-smoke` answers both.
- `sg-silo`'s third silo (`silo_full`) holds 600 entries, its capacity: `silo_checks.py`'s `silo_full` check
  deposits one spare backpack there and expects the refusal.
