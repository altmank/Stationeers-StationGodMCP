# Building from source

[Back to the README](../README.md)

For maintainers and anyone who wants to build the mod themselves.

## Prerequisites

- Stationeers installed, with BepInEx 5.4 and StationeersLaunchPad. The mod compiles against the game's own
  assemblies, which are not in this repository.
- The .NET 8 SDK.
- PowerShell, for `build.ps1`.

## Layout

| Path | What it is |
| --- | --- |
| `src/StationGodMCP.Mod` | The mod: pipe and TCP servers, the request dispatcher, every tool's game code. `Pure`, `Api/Shared` and `Api/Views` hold the code that uses no game type; `Pure/Verbs/Generated` holds the option classes generated from the catalogue. |
| `src/StationGodMCP.Server` | The sidecar: the MCP server over stdio; its tool list comes from the verb catalogue (`catalogue/verbs`). |
| `tests/StationGodMCP.Tests` | Tests that need no game. |
| `About/`, `GameData/` | The Workshop page (`About.xml`), preview images, and the gateway kit's printer recipe. |
| `lint-rules.json` | The default lint rules: shipped in the package and built into the DLL (used when the file is missing). The tests load it and run every rule's examples. Adding a rule function: [lint-rules.md](lint-rules.md#adding-a-function-maintainers). |
| `catalogue/verbs/` | The verb catalogue's sources: one file per verb (its description, whats, options, reply shape and write rules), the shared reply keys and option kinds in `_shared.json`, and every error code in `_errors.json`. The mod, the sidecar and the Python client all embed it. |
| `generated/verbs/` | Written from `catalogue/verbs/`: the MCP tool list and its size budget (`tools.json`, `sizes.json`), the direct help pages (`help/`) and the Python stub per what (`stubs/`). Never edit by hand: after changing `catalogue/verbs/`, set `STATIONGOD_WRITE_CATALOGUE=1` before running the tests, which write it (and fail while it does not match). |
| `clients/python/sg/` | The Python client library. `generate_catalogue.py` writes its verb layer from `catalogue/verbs/`; `--check` fails if it is stale. |
| `Sidecar/README.txt` | Shipped next to the sidecar archives. |
| `build.ps1` | Build, check and stage the package; optionally deploy. |

## Build

```powershell
.\build.ps1            # build, check versions and the Workshop page length, stage .\package
.\build.ps1 -Deploy    # also install the mod into the local mods folder and the sidecar into
                       # %LOCALAPPDATA%\StationGodMCP\server (close the game first)
```

- The game folder comes from `-GameDir`, else `STATIONEERS_DIR`, else the Steam default.
- `-SidecarPath` changes where `-Deploy` puts the sidecar. A running MCP client keeps the old sidecar file open;
  `-Deploy` sets it aside under a dated name and the client loads the new one when it restarts.
- `-Deploy` refuses while the game runs, because a running game keeps the old DLL. `-Force` overrides that, only for a
  mod the game has never loaded.
- The sidecar is deployed outside the source tree, by default to `%LOCALAPPDATA%\StationGodMCP\server`, because
  StationeersLaunchPad loads every DLL it finds in an enabled mod's folder.

`.\package` holds exactly what the Workshop gets: the `About` and `GameData` folders, `StationGodMCP.dll`, `LICENSE`,
`lint-rules.json`, and `Sidecar` with both archives and its README.

## Checks the build makes

- **One version in five places:** `StationGodMCP.csproj`, `StationGodMod.Version`, `About\About.xml`,
  `StationGodMCP.Server.csproj` and `Program.ServerVersion`. The build stops if they differ.
- **Workshop limits:** the `About.xml` description and change note must each stay under 8000 characters; a preview
  image over 1 MB gets a warning.

## Tests

```powershell
dotnet test .\tests\StationGodMCP.Tests
```

The tests compile the mod's game-free code (`Pure`, `Api\Shared`, `Api\Views`) and check the route planners, guards,
forecasts, rotation maths and every tool's reply shape. They need no game.

The catalogue's tests hold it to the code: every option has its schema, text and script form; the generated reply
lines match the declared reply shapes; every error code the mod uses is in `_errors.json` and points at verb help;
the generated files under `generated/verbs` and the mod's option classes are current; the size targets hold; and no
old tool or option name survives in the help. At run time the option check refuses a what or an option a call does
not belong to, in verb names with the nearest valid names.

## How the mod is put together

- Both transports live in the mod. Pipe and TCP requests are read on background threads, queued through one
  dispatcher, and run in the game's update loop, so no game object is touched off the main thread. Nothing opens
  unless the game is the host.
- Jobs that rebuild things hold the game tick across frames, so a change and its check see one consistent world.
- Every game member reached by reflection is declared in `GameMembers.cs` and checked at load; `server info` lists them.
  After a game update, a missing member turns off only the tools that need it.
- The tool list lives in the catalogue, built into both the mod and the sidecar, so a new tool needs both rebuilt and
  the MCP client restarted.
