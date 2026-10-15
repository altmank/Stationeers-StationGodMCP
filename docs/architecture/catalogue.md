# The verb catalogue

`catalogue/verbs/` is the one description of everything a caller can ask the game for: nineteen JSON files —
`_shared.json`, `_errors.json` and one per verb file (the fifteen verbs, plus `help` and `run_script`). The mod, the
sidecar and the C# client each embed these files; the MCP tool list, the help pages, the Python stubs and the mod's
typed options are generated from them (`catalogue/verbs/README.md:1-6`). Edit a source here, never a generated copy.
The files' bytes are the catalogue's identity: SHA-256 over them, line endings normalised, is the hash the mod
announces in `welcome`, and every library checks it (below).

## The files

- `<verb>.json`, one per verb: the tool `description`, extra help `text`, `read_only`, the tool list's own short
  texts (`tool.options`), and `whats` — the kinds of call the verb takes (`catalogue/verbs/README.md:7-11`).
- Per what: `summary` and the shorter `brief`; `options` (schema, help text, `default`, `x-default-when`,
  `required`, `x-script`, `x-wire`); the notes (`text`, `x-script-text`); `example`; `x-cost` (the class, how the
  call's size counts, and the growth the cost gate holds it to); `reply` (the main list, typed `keys`, small `types`, `x-wire` keys); `id_list`; and for a
  write what a `write` block (route, cheat, `x-undo`, `x-target`) (`catalogue/verbs/README.md:16-32`).
- A what carrying `"hub": true` is answered by the hub, not the game: the saved-script parts of `find`, `inspect`,
  `remove` and `write`, and all of `run_script` (`catalogue/verbs/find.json:565`, `inspect.json:881`,
  `remove.json:388`, `write.json:696`, `run_script.json:16`).
- `_shared.json`: the verb order, the shaping options (`fields`, `omit`, `list_limits`, `output_file`, `whole`,
  `repeat`), the instructions agents get at `initialize`, the help topics, and `report_top` (`report_only`)
  (`catalogue/verbs/_shared.json`, *verbs*, *options*, *instructions*, *topics*).
- `_errors.json`: every error code with a short description and a `see` — the verb/what or topic/subtopic that
  explains it. The mod's `ErrorGuide` gives each error its pointer (`src/StationGodMCP.Mod/Pure/Catalogue/ErrorGuide.cs:14-31`,
  `:43-54`).

A resolving step keeps the sources small: an option with `"use": "<name>"` takes that shared definition, its own keys
replacing the shared ones (`catalogue/verbs/README.md:10`).

## What is generated from it

| Output | What it holds |
| --- | --- |
| `generated/verbs/tools.json` | The MCP tools array (17 tools) plus the instructions: one tool per verb, a `what` enum of its whats, every option any of its whats takes with the verb's short text, and the shaping options (`tests/StationGodMCP.Tests/Verbs/VerbToolList.cs:7-19`). |
| `generated/verbs/help/*.txt` | The direct help pages: the index, each verb, each what, each shared topic (`src/StationGodMCP.Server/Verbs/HelpPages.cs:7-11`). |
| `generated/verbs/stubs/*.py` | One Python stub per what (no stub for the hub-only `run_script`): its options and reply keys, with `sg` in the examples (`tests/StationGodMCP.Tests/Verbs/VerbOutputs.cs:44-55`). |
| `src/StationGodMCP.Mod/Pure/Verbs/Generated/*.cs` | The mod's typed options classes, one per what, and the generated verb surface (`VerbSurface.Generated.cs`). |
| `generated/verbs/sizes.json` | The tool list's token budget by tool, against today's figure and the targets (`tests/StationGodMCP.Tests/Verbs/VerbSizes.cs:11-18`, `:58-96`). |
| `sg/_catalogue.py`, `sg/_verbs.py`, `sg/_verbs.pyi` | The Python library's verb functions, its tables and its IDE stubs (`clients/python/generate_catalogue.py:1-12`). |

The C# generator is the test project: `tests/StationGodMCP.Tests/Verbs/VerbOutputs.cs:10-34` assembles every file,
and `VerbCatalogueTests.GeneratedFilesAreCurrent` rewrites them when `STATIONGOD_WRITE_CATALOGUE=1` is set and
otherwise fails when anything differs (`tests/StationGodMCP.Tests/Verbs/VerbCatalogueTests.cs:40-54`). One layout is
used everywhere: two-space indents, LF line ends, and any object or array that fits in 120 columns on one line
(`tests/StationGodMCP.Tests/Catalogue/CatalogueFiles.cs:13-18`). The server embeds the sources *and* the generated
files (`src/StationGodMCP.Server/StationGodMCP.Server.csproj:28-31`); the mod and the client embed the sources alone
(`StationGodMCP.csproj:35`; `src/StationGodMCP.Client/StationGodMCP.Client.csproj:26`).

`sg`'s layer is written by `py -3 clients/python/generate_catalogue.py` from `catalogue/verbs` and the generated
stubs: `_catalogue.py` (the verbs, whats, write whats and main lists), `_verbs.py` (one function per verb, its
options as keyword arguments) and `_verbs.pyi` (the stubs, one overload per what). `--check` exits 1 when any is
stale, and a Python test runs it (`clients/python/generate_catalogue.py:91-124`, `:209-215`;
`clients/python/tests/test_generated.py:23-28`).

## Who reads it

- **The mod.** It embeds `catalogue/verbs/*.json` (`StationGodMCP.csproj:32-35`), and at start `VerbDocument`
  assembles them into one document — `{"shared": …, "errors": …, "verbs": {…}}` — and computes the hash
  (`src/StationGodMCP.Mod/Pure/Catalogue/VerbDocument.cs:8-25`, `:66-83`, `:145-160`). A document that does not load
  is logged and every request is answered `internal_error` (`src/StationGodMCP.Mod/Api/ApiHost.cs:136-176`). The
  assembled text is what the `catalogue` method answers, and the hash is announced in `welcome`
  (`src/StationGodMCP.Mod/Protocol/CallSession.cs:243-249`, `:450-460`). The mod's own verb table comes from the
  generated options classes (`src/StationGodMCP.Mod/Pure/Verbs/VerbSurface.cs:7-15`).
- **The sidecar and hub.** They embed the sources and the generated files, and read the model for option checking,
  write rules, help pages and the tool list (`src/StationGodMCP.Server/Verbs/VerbCatalogue.cs:8-19`, `:76-120`);
  `help` is answered from the generated pages (`src/StationGodMCP.Server/Verbs/VerbPipeline.cs:189-192`).
- **The C# client.** `GameCatalogue` embeds the same files, computes the same hash from its own copy
  (`src/StationGodMCP.Client/GameCatalogue.cs:50-60`, `:168-186`), and uses the model only for what a call is
  (read/write/cheat, `x-effects`) and how long its deadline may run (`:65-115`).
- **`sg`.** Its generated layer is a build-time read; at run time `sg` never parses the model.

## The hash chain

The hash is `"sha256:"` and the lowercase hex of SHA-256 over the verb files' raw bytes with every line ending a bare
LF, concatenated whole in ordinal resource-name order, with nothing between them — a checkout that rewrote the
committed LF bytes announces the same hash
(`src/StationGodMCP.Mod/Pure/Catalogue/VerbDocument.cs:8-25`, `src/StationGodMCP.Client/GameCatalogue.cs:50-53`).
`welcome.catalogue` carries the hash, the number of whats and the number of protocol methods
(`src/StationGodMCP.Mod/Protocol/Wire.cs:184-198`); the client compares it with the one it was built with and, when
they differ, calls `catalogue` once and uses the mod's document from then on, falling back to its own if the fetch
fails (`src/StationGodMCP.Client/GameLink.cs:763-800`). Only raw bytes are hashed; the document is the files' own text
on one line, not a re-serialisation, so nothing about a number or a word changes shape in transit
(`VerbDocument.cs:196-226`).

## What a change to the catalogue means

1. Edit `catalogue/verbs/` only.
2. Run the C# tests with `STATIONGOD_WRITE_CATALOGUE=1`: `GeneratedFilesAreCurrent` rewrites `generated/verbs/` and
   the mod's options classes, and the other `tests/StationGodMCP.Tests/Verbs/` checks run against the new sources —
   the verb set, the write blocks, `x-target`, every what's cost, report costs, default forms, the help and stub
   text, the error pointers, the tools' reply lines and the size targets (`VerbCatalogueTests.cs`,
   `VerbModelTests.cs`, `VerbOptionsCode.cs`, `VerbPathWireTests.cs`).
3. Run `py -3 clients/python/generate_catalogue.py` to rewrite `sg`'s generated files (the Python test fails until
   they are current).
4. The hash changes with any byte of the sources. A client built with the old hash fetches the new document on its
   next connection; a client that predates the change still gets the new checks, because both the hub and the mod
   check every call against the model they run (`src/StationGodMCP.Server/Verbs/VerbPipeline.cs:184-187`;
   `src/StationGodMCP.Mod/Pure/Verbs/OptionCheck.cs`).

The catalogue is the only place these rules exist: options are checked against it, the write rule, refs and faces are
applied against it (`VerbPipeline*.cs`), and the mod's handlers bind options classes generated from it, so a handler
cannot name an option the catalogue does not declare without failing to compile.
