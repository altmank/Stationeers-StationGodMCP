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
| `src/StationGodMCP.Mod` | The mod: pipe and TCP servers, the request dispatcher, every tool's game code. `Pure`, `Api/Shared` and `Api/Views` hold the code that uses no game type. |
| `src/StationGodMCP.Server` | The sidecar: the MCP server over stdio; its tool list comes from the catalogue (`ToolCatalogue.cs`). |
| `tests/StationGodMCP.Tests` | Tests that need no game. |
| `About/`, `GameData/` | The Workshop page (`About.xml`), preview images, and the gateway kit's printer recipe. |
| `lint-rules.json` | The default lint rules: shipped in the package and built into the DLL (used when the file is missing). The tests load it and run every rule's examples. Adding a rule function: [lint-rules.md](lint-rules.md#adding-a-function-maintainers). |
| `catalogue/` | The method catalogue's sources: one file per method in `methods/` (description, class, cost, argument schema, reply keys), shared schema pieces and description texts in `defs/`, every error code in `errors.json`, the server's instructions in `server.json`, and the format itself in `catalogue.schema.json`. |
| `catalogue.json` | The catalogue assembled from `catalogue/`, built into the DLL and the sidecar: the sidecar's tool list, and the argument names the mod accepts from a pipe client. Never edit it by hand: after changing `catalogue/`, run `$env:STATIONGOD_WRITE_CATALOGUE=1; dotnet test .\tests\StationGodMCP.Tests --filter CatalogueConsistencyTests` (the tests fail until it matches). |
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
- The sidecar's build output goes to `%LOCALAPPDATA%\StationGodMCP\build`, outside the source tree, because
  StationeersLaunchPad loads every DLL it finds in an enabled mod's folder.

`.\package` holds exactly what the Workshop gets: the `About` and `GameData` folders, `StationGodMCP.dll`, `LICENSE`,
`lint-rules.json`, `catalogue.json`, and `Sidecar` with both archives and its README.

## Checks the build makes

- **One version in six places:** `StationGodMCP.csproj`, `StationGodMod.Version`, `About\About.xml`,
  `StationGodMCP.Server.csproj`, `Program.ServerVersion` and `catalogue.json`'s `mod_version`. The build stops if they
  differ (after a version bump, write `catalogue.json` again as above).
- **Workshop limits:** the `About.xml` description and change note must each stay under 8000 characters; a preview
  image over 1 MB gets a warning.

## Tests

```powershell
dotnet test .\tests\StationGodMCP.Tests
```

The tests compile the mod's game-free code (`Pure`, `Api\Shared`, `Api\Views`) and check the route planners, guards,
forecasts, rotation maths and every tool's reply shape. They need no game.

The catalogue's tests hold it to the code, reading the mod's sources: it lists exactly the methods `ApiHost.Methods`
has; every argument a handler reads through `Args` is declared, and every declared one is read (or named in the
method's `x-read-by` with the file that reads it); integer bounds the handlers pass as literals are the catalogue's
`minimum` and `maximum`; reply keys match the view classes named in `x-views`; every error code the mod uses is in
`errors.json`; and the version is the mod's. At run time `mod_info.runtime.catalogue_drift` counts any argument name a
handler reads that its method does not declare.

## How the mod is put together

- Both transports live in the mod. Pipe and TCP requests are read on background threads, queued through one
  dispatcher, and run in the game's update loop, so no game object is touched off the main thread. Nothing opens
  unless the game is the host.
- Jobs that rebuild things hold the game tick across frames, so a change and its check see one consistent world.
- Every game member reached by reflection is declared in `GameMembers.cs` and checked at load; `mod_info` lists them.
  After a game update, a missing member turns off only the tools that need it.
- The tool list lives in the catalogue, built into both the mod and the sidecar, so a new tool needs both rebuilt and
  the MCP client restarted.
