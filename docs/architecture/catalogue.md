# The method catalogue

[Back to the overview](README.md)

The catalogue is the one description of everything the mod offers: every method, its arguments with types, ranges
and descriptions, the shape of its reply, its class (read, write or cheat) and its cost class, plus the error codes
and the text the sidecar gives agents. The mod checks requests against it, the sidecar turns it into MCP tools, and
the Python library reads it to decide what is safe to resend and how to page. This page says what it looks like, where it lives,
how it stays true to the code, and who reads it.

## Where it comes from today

Three partial descriptions exist now, kept in step by hand and by one test:

- The sidecar's tool definitions: names, descriptions and JSON Schema input schemas written as C# objects
  (`src/StationGodMCP.Server/Program.cs:583-678` for names, `:713` onward for the tools, `:2161-2196` for how each is
  wrapped), with a read-only flag per tool that becomes MCP annotations (`Program.cs:2170-2173`), a hand-kept list
  of tools whose replies are small (`Program.cs:680-685`), and shared description texts used by many tools
  (`Program.cs:687-707`).
- `tool-arguments.json`, the top-level argument names per tool, written from the sidecar's schemas by a test
  (`src/StationGodMCP.Server/ToolArguments.cs:12-30`, `tests/StationGodMCP.Tests/ToolArgumentsFileTests.cs:16-28`) and
  embedded in the mod (`StationGodMCP.csproj`, `EmbeddedResource tool-arguments.json`), which uses it to refuse unknown
  names (`src/StationGodMCP.Mod/Api/ApiHost.cs:190-206`, `:165`).
- The handlers themselves, which read their arguments one by one with ranges written in code
  (`src/StationGodMCP.Mod/Api/Shared/Args.cs:31-223`, for example `OptionalInt(name, minimum, maximum)` at `:104`).
  Ranges live only there; the sidecar's schemas leave them out on purpose (`src/StationGodMCP.Server/ArgumentCheck.cs:14-16`).
  A few handlers also read raw JSON outside `Args` (for example `src/StationGodMCP.Mod/Api/UndoJob.cs:166-172`).

Reply shapes are described only in prose inside each tool's description and in `docs/`.

## Where it lives

```text
catalogue/
  catalogue.schema.json     the format below, as JSON Schema
  server.json               server name and the instructions text for agents
  errors.json               every error code
  shared.json               reply keys the mod adds to many methods
  defs/<name>.json          shared pieces of argument and reply schemas (refund_to, network handles, item filters, ...)
  help.json                 tool_info's intro and shared topics (see Help and the text rubric)
  text-allow.json           exceptions to the text rubric, each with its reason
  methods/<method>.json     one file per method
catalogue.json              assembled from catalogue/, checked in, embedded in the mod and the sidecar
```

`catalogue.json` sits at the repository root, where `tool-arguments.json` is today, and replaces it. It is assembled,
not hand-edited: a test writes it when `STATIONGOD_WRITE_CATALOGUE=1` is set and otherwise fails if it differs from
what the sources give, the same habit `ToolArgumentsFileTests` has today. Assembly inlines every `$ref` and every text
include, so no reader resolves anything. The file's exact bytes are its identity: `catalogue.hash` in the protocol is
SHA-256 of them, and nobody re-serialises it before hashing.

One file per method, because a method is what changes together, and a diff of one JSON file per method reads well.
Plain JSON, because the mod has no other parser available without new dependencies. In sources only, a `description`
may be an array whose items are strings or `{"$include": "<file>.txt"}` (no source uses one at present); assembly joins them into one
string, so a text shared by dozens of tools is written once.

## The format

A method entry, abridged, for `thing_health`:

```json
{
  "name": "thing_health",
  "area": "air-planet-and-plants",
  "since": "1.0.0",
  "description": "Damage and health of things ...",
  "class": "read",
  "cost": "world",
  "x-cost-when": [{"when": {"reference_ids": {"present": true}}, "cost": "bounded", "per_item": "reference_ids"},
                  {"when": {"reference_id": {"present": true}}, "cost": "instant"}],
  "params": {
    "type": "object",
    "additionalProperties": false,
    "properties": {
      "reference_ids": {"type": "array", "items": {"type": "string", "pattern": "^[0-9]+$"}, "maxItems": 256,
                        "description": "Up to 256 ids; a result per id."},
      "limit": {"type": "integer", "minimum": 1, "maximum": 500, "default": 100, "description": "..."},
      "offset": {"type": "integer", "minimum": 0, "default": 0, "description": "..."}
    }
  },
  "reply": {
    "type": "object",
    "properties": {"results": {"type": "array"}, "things": {"type": "array"}, "count": {"type": "integer"},
                   "has_more": {"type": "boolean"}}
  },
  "x-shaping": "lists",
  "x-paging": {"list": "things", "offset": "offset", "limit": "limit", "total": "total", "has_more": "has_more",
               "order": "worst damage first, then reference id"},
  "x-costly": [{"list": "things", "key": "networks"}, {"list": "results", "key": "networks"}],
  "deprecated_aliases": {"min_ratio": "min_damage_ratio"},
  "errors": ["thing_not_found", "invalid_argument"]
}
```

(The ranges, costs and order above illustrate the format; the real values come from the handlers' code in stage 2.)

### The format as JSON Schema

`catalogue/catalogue.schema.json`, describing the assembled file:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "stationgod-catalogue-1",
  "type": "object",
  "additionalProperties": false,
  "required": ["catalogue_version", "mod_version", "server", "errors", "shared_reply_keys", "methods", "protocol_methods"],
  "properties": {
    "catalogue_version": {"const": 1},
    "mod_version": {"type": "string", "pattern": "^[0-9]+\\.[0-9]+\\.[0-9]+$"},
    "server": {
      "type": "object", "additionalProperties": false, "required": ["name", "instructions"],
      "properties": {"name": {"type": "string"}, "instructions": {"type": "string"}}
    },
    "errors": {
      "type": "object",
      "propertyNames": {"pattern": "^[a-z][a-z0-9_]*$"},
      "additionalProperties": {
        "type": "object", "additionalProperties": false, "required": ["description", "origin"],
        "properties": {
          "description": {"type": "string"},
          "origin": {"enum": ["tool", "protocol", "client"]},
          "caller_may_resend": {"enum": ["never", "if_read", "yes"], "default": "never"}
        }
      }
    },
    "shared_reply_keys": {
      "type": "array",
      "items": {
        "type": "object", "additionalProperties": false, "required": ["key", "schema", "applies_when"],
        "properties": {
          "key": {"type": "string"},
          "schema": {"$ref": "#/$defs/schema"},
          "applies_when": {"enum": ["takes_network_handles", "may_touch_pipe_networks"]}
        }
      }
    },
    "protocol_methods": {"type": "array", "items": {"$ref": "#/$defs/method"}},
    "methods": {"type": "array", "items": {"$ref": "#/$defs/method"}}
  },
  "$defs": {
    "class": {"enum": ["read", "write", "cheat"]},
    "cost": {"enum": ["instant", "bounded", "world", "plan", "job", "stream"]},
    "matcher": {
      "type": "object", "additionalProperties": false, "minProperties": 1, "maxProperties": 1,
      "properties": {
        "equals": {},
        "in": {"type": "array", "minItems": 1},
        "present": {"const": true},
        "absent": {"const": true}
      }
    },
    "when": {"type": "object", "minProperties": 1, "additionalProperties": {"$ref": "#/$defs/matcher"}},
    "rule": {
      "type": "object", "additionalProperties": false, "required": ["when"],
      "properties": {
        "when": {"$ref": "#/$defs/when"},
        "any_of": {"type": "array", "items": {"$ref": "#/$defs/when"}},
        "class": {"$ref": "#/$defs/class"},
        "cost": {"$ref": "#/$defs/cost"},
        "per_item": {"type": "string"}
      }
    },
    "schema": {"type": "object", "description": "A JSON Schema in the subset listed under 'The schema subset'."},
    "method": {
      "type": "object", "additionalProperties": false,
      "required": ["name", "description", "class", "cost", "params", "reply", "x-shaping"],
      "properties": {
        "name": {"type": "string", "pattern": "^[a-z][a-z0-9_]*$"},
        "area": {"type": "string"},
        "since": {"type": "string"},
        "description": {"type": "string", "minLength": 1},
        "class": {"$ref": "#/$defs/class"},
        "x-class-when": {"type": "array", "items": {"$ref": "#/$defs/rule"}},
        "cost": {"$ref": "#/$defs/cost"},
        "x-cost-when": {"type": "array", "items": {"$ref": "#/$defs/rule"}},
        "x-effects": {"type": "array", "items": {"enum": ["display", "server_state", "files"]}},
        "params": {"$ref": "#/$defs/schema"},
        "reply": {"$ref": "#/$defs/schema"},
        "x-views": {"type": "array", "items": {"type": "string"}},
        "x-entry-views": {"type": "object", "additionalProperties": {"type": "array", "items": {"type": "string"}}},
        "x-shaping": {"enum": ["lists", "none"]},
        "x-paging": {
          "type": "object", "additionalProperties": false, "required": ["list", "offset", "limit", "order"],
          "properties": {"list": {"type": "string"}, "offset": {"type": "string"}, "limit": {"type": "string"},
                         "total": {"type": "string"}, "has_more": {"type": "string"}, "order": {"type": "string"}}
        },
        "x-costly": {
          "type": "array",
          "items": {"type": "object", "additionalProperties": false, "required": ["list", "key"],
                    "properties": {"list": {"type": "string"}, "key": {"type": "string"}}}
        },
        "x-duration": {
          "type": "object", "additionalProperties": false, "required": ["param", "max_s"],
          "properties": {"param": {"type": "string"}, "max_s": {"type": "number", "exclusiveMinimum": 0}}
        },
        "x-job": {
          "type": "object", "additionalProperties": false, "required": ["poll_param"],
          "properties": {"poll_param": {"type": "string"}}
        },
        "x-needs-mod": {"type": "array", "items": {"enum": ["StationeersLua", "BlueprintMod", "IngotVault", "TerraformingReloaded"]}},
        "x-mcp": {"enum": ["tool", "hidden"], "default": "tool"},
        "x-read-by": {"type": "object", "additionalProperties": {"type": "string"}},
        "x-file-arguments": {"type": "object", "additionalProperties": {"type": "object", "properties": {"into": {}, "description": {}}}},
        "deprecated": {"type": "boolean"},
        "deprecated_aliases": {"type": "object", "additionalProperties": {"type": "string"}},
        "errors": {"type": "array", "items": {"type": "string"}}
      }
    }
  }
}
```

### What each part means

`class` and `x-class-when` give the method's class (see [protocol.md](protocol.md), *Method classes*).
Rules are tried in order; the first whose `when` matches gives the class; if none matches, `class` applies. A `when`
matches when every argument it names matches its matcher (and, with `any_of`, at least one of those also matches).
Matchers: `equals` (JSON equality after the same trimming and case folding the mod applies to words), `in` (equals one
of), `present` (given and not null), `absent` (missing or null; the mod reads null as omitted, `DeclaredArguments.cs:61`).

`cost` and `x-cost-when` give the cost class the scheduler starts from ([scheduling.md](scheduling.md)). A rule's
`per_item` names the array argument whose length the scheduler multiplies its per-item cost by, so a large
`read_devices` and a small one are judged by their size:

| Cost | Meaning |
| --- | --- |
| `instant` | One thing, a handful of values. |
| `bounded` | Grows with the request, capped by the method's own bounds (at most 128 items, 256 ids, ...). |
| `world` | Walks a world-sized collection: every thing, every device, every cell of an area. |
| `plan` | Searches or simulates: route planning, dry runs, forecasts. |
| `job` | Starts or advances a building job that holds the game tick. |
| `stream` | Runs over time: `sample_logic`. |

`x-effects` names side effects that are not world changes: `display` (`highlight`, `show_preview` draw on the host's
screen), `server_state` (the flight recorder), `files` (CSV files in the save folder). A method with `x-effects` is
never resent automatically, even when it is read class.

`params` is the argument schema. `reply` describes the reply's top-level keys and says which of them are lists; that
is all every method must give, because it is what `limit` and the first name of a `fields` path need. A method may
also describe its lists' entries and name the C# view classes the reply is built from in `x-views`; when it does, the
tests hold the two together. Entry schemas are added method by method, starting with the methods whose replies are
large. `x-shaping: "none"` marks a method whose reply is always small; MCP clients get no `output_file` or
`fields` for it, as `SmallReplies` does today (`Program.cs:680-685`). `x-paging` names the method's own paging
arguments and reply keys, so libraries can walk pages for any method the same way, and states the order of entries;
pages are not a snapshot, so things that appear or vanish between pages can be missed or seen twice. `x-costly` lists
reply parts the handler skips when `fields` leaves them out. `x-duration` names the argument that sets how long a
long-running method takes and its maximum. `x-job` marks a method that starts building jobs and names its polling
argument. `x-needs-mod` lists optional mods a method needs. `x-mcp: "hidden"` keeps a method out of the MCP tool list
(protocol methods). `x-read-by` names a parameter that the handler's own files do not read through `Args`, and the
file that reads it (shared code, or raw JSON access). `x-file-arguments` names MCP-only arguments the sidecar reads: a
path on its machine whose UTF-8 text it sends as the argument `into` (set_ic_source `source_file` into `source`), so a
large text never passes through the agent; the tool's schema gains the argument and `into` stops being required there,
while the mod's own parameters stay as declared. `deprecated_aliases` maps old argument names to new ones.

`shared_reply_keys` declares, once, the keys the mod adds to replies outside the methods' own views:
`resolved_networks` for methods that take network handles and `gas_hold` for methods that may touch pipe networks
(`src/StationGodMCP.Mod/Api/ApiHost.cs:166-170`; `Api/Shared/NetworkHandle.cs:188-203`; `Api/Shared/GasHoldReply.cs:59-74`).

### The schema subset

`params`, `reply` and `defs` use a subset of JSON Schema that the mod validates in full: `type` (one type or a list),
`properties`, `required`, `additionalProperties` (`false` or a schema), `items`, `minItems`, `maxItems`, `enum`,
`const`, `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `minLength`, `maxLength`, `pattern` (only
anchored character classes, so the netstandard regex engine and the Python one agree), `oneOf`, `anyOf`, `default`,
`description`, `minProperties`, `maxProperties`, `$ref` (in sources only). The mod's validator implements exactly
these and fails at load on anything else, so the catalogue cannot say more than the mod checks.

Two rules carry over from today's checks. JSON null is an omitted argument (`ArgumentCheck.cs:17`), and an integer
may be written with a zero fraction or an exponent (`3.0`, `1e2`), as the sidecar accepts now
(`ArgumentCheck.cs:12-14`). Strings in an `enum` are compared trimmed and ignoring case, as the mod compares words
(`ArgumentCheck.cs:379-400`).

## Method classes and costs

Which tools are cheat is the owner's decision (overview, *Questions for the owner*); the catalogue ships the
recommendation below. The class is information, not a permission: every connection can call every method. The
libraries use it to resend only reads, the scheduler to order writes, and agents to tell the owner when a tool is a
cheat.

| Class | Methods |
| --- | --- |
| read | `list_gateways`, `list_devices`, `describe_device`, `read_logic`, `read_logic_many`, `read_devices`, `read_memory`, `inspect_slots`, `network_snapshot`, `sample_logic`, `get_ic_source`, `get_ic_status`, `resolve_ic_selectors`, `read_console`, `game_clock`, `find_items`, `find_things`, `item_totals`, `list_containers`, `container_contents`, `player_vitals`, `consumables`, `atmosphere_contents`, `water_sources`, `trader_contacts`, `dish_aim`, `trader_inventory`, `plants`, `reagents`, `planet`, `deep_miner_spots`, `solar_aim`, `thing_health`, `outer_frames`, `rooms`, `weather`, `ignition_risk`, `looking_at`, `connections`, `mod_info`, `landing_pads`, `rocket_status`, `rocket_forecast`, `describe_prefab`, `wall_map`, `find_spot`, `lint_layout`, `lint_rules`, `check_replaceable`, `show_preview` (display), `highlight` (display), `grid_survey`, `plan_cable_route`, `plan_pipe_route`, `plan_chute_route`, `plan_removal`, `feed_paths`, `vault_contents` |
| write | `write_logic`, `write_logic_many`, `set_ic_source`, `control_ic_execution`, `set_ic_pins`, `label` (the Labeller), `paint` (a spray can), `move_item` (the game's own slot moves), `trader_buy`, `trader_sell` (the trade window's own calls), `vault_deposit`, `vault_withdraw` (the vault's own bookkeeping), `rocket_flight_log` (start, stop, clear; server state and files), and every building tool on a real run: `place_cables`, `place_pipes`, `place_chutes`, `remove_cables`, `remove_pipes`, `remove_chutes`, `upgrade_cables`, `upgrade_pipes`, `clean_cables`, `clean_pipes`, `replace_walls`, `replace_frames`, `place_structure`, `remove_structure`, `undo_job` |
| cheat | `run_console_command`, `move_gas`, `write_memory`, `plant_genes` when it edits, `paste_blueprint` when it pastes or undoes, `place_structure` with `free: true` |

The read list is exactly the tools the sidecar marks read-only today (`Program.cs:2161-2175`, `readOnly: true`); the
others are its 33 non-read-only tools. `write_memory` is cheat under the recommendation.

The argument rules that make this exact (the defaults are the handlers' own, in `src/StationGodMCP.Mod/Api/`: `PlaceRuns.cs:163-164`,
`Shared/BuildArgs.cs:584`, `Shared/StructureSwapArgs.cs:147`, `UpgradeNetwork.cs:153`, `UndoJob.cs:35`,
`VaultDeposit.cs:83` `WriteMode`, `TraderTrade.cs:231`, `MoveGas.cs:110`, `PlantGenes.cs:150-163`,
`PasteBlueprint.cs:46-54`, `RocketFlightLog.cs:29-44`):

| Method | Rules, in order |
| --- | --- |
| `place_structure` | `free` equals true and `dry_run` equals false: cheat; then as the next row |
| building tools whose dry run is the default (`place_*`, `remove_*`, `upgrade_*`, `clean_*`, `replace_*`, `place_structure`, `remove_structure`, `undo_job`, `vault_deposit`, `vault_withdraw`) | `dry_run` absent: read; `dry_run` equals true: read (this covers `job_id` polls, which carry no `dry_run`) |
| `move_gas`, `trader_buy`, `trader_sell` (dry run off by default) | `dry_run` equals true: read; `transfer_id` present (a `move_gas` poll): read |
| `plant_genes` | `genes` absent: read; otherwise cheat |
| `paste_blueprint` | `status` equals true: read; otherwise cheat |
| `rocket_flight_log` | `action` absent or `in` [`read`, `list`]: read; otherwise write |

The cost classes start from the method's nature: `instant` for single reads and writes, `bounded` for the batch forms
and `read_devices` (with `per_item` set), `world` for the scans (`list_devices` without a filter, `find_things`,
`find_items`, `item_totals`, `list_containers`, `thing_health` without ids, `grid_survey`, `lint_layout`, `rooms`,
`plants`, `outer_frames`, `deep_miner_spots`, `wall_map`, `consumables`, `water_sources`, `ignition_risk`), `plan` for
planners, dry runs of building tools and forecasts, `job` for real runs of building tools, `stream` for `sample_logic`.
The scheduler refines them at run time
([scheduling.md](scheduling.md)).

## The consistency tests

The catalogue is useful only if it cannot drift from the handlers. These tests run in the existing test project
(`tests/StationGodMCP.Tests`), which compiles the mod's `Pure`, `Api/Shared` and `Api/Views` folders and references the
sidecar, and already parses mod sources with Roslyn for checks of this kind (`tests/StationGodMCP.Tests/SnakeCaseTests.cs:31`,
`ReplaceStructuresTests.cs:569`). Any of them failing fails the test run.

1. **The file is current.** `catalogue.json` equals what the sources assemble to, and validates against
   `catalogue.schema.json`. `STATIONGOD_WRITE_CATALOGUE=1` rewrites it.
2. **Same methods.** The method names in `catalogue.json` equal the keys of `ApiHost.Methods`
   (`src/StationGodMCP.Mod/Api/ApiHost.cs:26-120`, read with Roslyn from the source), plus the protocol methods, which
   the protocol layer must handle (checked the same way against its dispatch table). While `sample_logic` still runs in
   the sidecar, it is the one allowed exception, marked so in its entry. The failure names every method on one side only.
3. **Same arguments.** For each method, the test finds its handler class from the `ApiHost.Methods` entry
   (`static args => ThingHealthApi.Handle(args)`) and collects the argument names its code reads through `Args`
   (`Args.cs:31-223`): the first argument of `Has`, `ThingId`, `OptionalThingId`, `ThingIds`, `String`,
   `OptionalString`, `OptionalBool`, `OptionalInt`, `OptionalDouble`, `Double`, `Int`, `OptionalPositiveDouble`,
   `Array`, `OptionalObject`, `IsWord`, `Optional`, `Objects` and `With`, and the `names` (not the first, descriptive
   `form` argument) of `Reject` (`Args.cs:189`; for example `PlantGenes.cs:144`), as string literals or `const string`
   fields. It reads the handler's file and every file under `Api/` that declares a type the handler file names, one
   level deep (so `WriteMode` in `Api/VaultDeposit.cs:83`, used by `vault_withdraw`, is found). Then it checks both
   directions: every name read exists somewhere in the method's `params`, and every top-level parameter is read, or is
   named in `x-read-by` with the file that reads it. The failure names the method, the argument and the file.
4. **Ranges agree.** Where a reader passes literal bounds (`OptionalInt("limit", 1, 500)`), the catalogue's `minimum`
   and `maximum` for that argument must be those numbers. Bounds held in constants are resolved from the same file.
5. **Reply views agree, where declared.** For each method with `x-views`, the test reflects over the named view classes
   (compiled into the test project from `Api/Views` and `Api/Shared`), takes the property names the mod's serialiser
   settings give them (`src/StationGodMCP.Mod/Api/Shared/ApiJson.cs:43-60`: snake case, `[JsonProperty]` names kept),
   adds the `shared_reply_keys` that apply, and checks the result against `reply`: the top level, and each list's
   entry type against that list's `items`, as deep as `items` describes. Many handlers return different views in
   different forms (for example `Api/MoveItem.cs:62`, `Api/Connections.cs:49`), so `x-views` lists each.
6. **Every error code is registered.** Every code literal passed to `ApiErrors.Refused`, `new ApiException` or
   `new ErrorView` in the mod's sources (Roslyn), and every `ApiErrors` constant
   (`src/StationGodMCP.Mod/Api/Shared/ApiErrors.cs:21-23`), is in `errors.json`, and every `tool` entry there is used.
7. **One version.** `catalogue.json`'s `mod_version` equals the version `build.ps1` checks in five places (owner notes,
   `CLAUDE.md`, Identity).

At run time the mod adds a drift counter. For each method it holds a precomputed set of its declared argument names;
when `Args` is asked for a name not in that set, it counts a miss for that method in
`mod_info.runtime.catalogue_drift` and logs the first one. A name that is declared costs one set lookup and no
allocation. The test server's live checks require the counter to stay empty ([stages.md](stages.md)).

While stage 2 moves the tool definitions out of the sidecar, one more test proves nothing changed for agents: the
sidecar's `tools/list` built from the catalogue equals the one built from today's `ToolDefinitions`, compared as parsed
JSON. In stage 2 the sidecar emits a projection of `params` without the keywords today's schemas leave out on purpose
(`minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `minLength`, `maxLength`, `pattern`), so the projection
must equal today's list exactly. The full schemas reach `inputSchema` in stage 9, when the mod's strict checking has
replaced the sidecar's. The test is deleted with `ToolDefinitions`.

## Who reads it

**The mod** embeds `catalogue.json` as a resource, as it embeds `tool-arguments.json` today (`ApiHost.cs:190-206`).
At load it parses it into a pure `Catalogue` (no game types, so the tests compile it): per method the compiled
argument validator, the class rules, the cost class, shaping data and costly parts. A catalogue that does not load is
a fatal error for the protocol: the mod logs it and answers every call with `internal_error` naming the problem,
rather than running unchecked. The protocol method `catalogue` returns the embedded file; `welcome` carries its hash.

**The sidecar** embeds the `catalogue.json` it was built with and answers `tools/list` from it at once. Each method
whose `x-mcp` is not `hidden` becomes a tool: `name`; `description`; `inputSchema` = `params` (projected as above until
stage 9), plus `output_file` and `fields` when `x-shaping` is `lists` (as `WithReplyShaping` adds them today,
`Program.cs:2178-2196`); annotations `readOnlyHint` true only for methods whose class is read with no class rules,
`destructiveHint` its opposite, `idempotentHint` equal to `readOnlyHint`, `openWorldHint` false, as today
(`Program.cs:2168-2174`). The server's `instructions` come from `server.json`. After connecting, if the mod's hash
differs, the sidecar calls `catalogue`, rebuilds its tool list and sends MCP's `notifications/tools/list_changed`
(advertising `listChanged: true`, which is `false` today, `Program.cs:132`).

**The Python library** ships the `catalogue.json` it was built with and fetches the mod's when the hash differs. It
uses the catalogue for effective classes (what is safe to resend), `x-effects`, paging and `x-duration` deadlines.
It does not generate typed stubs: `call(method, **params)` works for any method the mod has.

**The documentation.** The per-area pages in `docs/` keep their prose. A later, optional step can generate each page's
argument tables from the catalogue; it is not part of this plan.

## As built in stage 2

Where the code disagreed with the text above, the build followed the code; this records each difference.

- **No projection in `tools/list`.** Today's schemas already carry some `minimum` and `maximum` (`grid_survey`'s
  `limit`, for example), so stripping the range keywords would have changed today's list. `inputSchema` is `params` in
  full; the sidecar's `ArgumentCheck` still enforces no numeric range (`ArgumentCheck.cs:14-16`), so nothing an agent
  sends is judged differently. The comparison test therefore compares tool by tool, as parsed JSON, with `minimum`,
  `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `minLength`, `maxLength` and `pattern` removed from both sides.
  The catalogue added 25 bounds; none disagrees with a bound today's schemas gave. No `pattern` was added.
- **Order.** `catalogue.json` lists methods by name, so `tools/list` is in name order (it followed `ToolDefinitions`
  before); the comparison is keyed by name.
- **Layout and bytes.** The sources and `catalogue.json` share one layout (two-space indents, LF, any object or array
  that fits in 120 columns on one line), written by the tests; `.gitattributes` keeps `catalogue.json` byte for byte
  (`-text`). `mod_version` is taken from `StationGodMCP.csproj` when assembling, and `build.ps1` checks it as a sixth
  version place and copies `catalogue.json` into the package.
- **Schema additions.** The method schema gains `x-runs-in` (`mod`, default, or `sidecar`), which marks
  `sample_logic`, the one allowed exception of test 2. The schema subset also accepts the annotation `deprecated`,
  which today's schemas put on two old argument names (`thing_health` `min_ratio`, `water_sources` `min_moles`).
  `pattern` must be anchored (`^...$`) and use no groups, alternation or `\b`.
- **Shared reply keys** are written once, in `shared.json`; assembling adds each one to the `reply` of every method it
  applies to. `takes_network_handles` applies when `params` has, at any depth, `network_id`, `network_ids`, `join_to`
  or `allow_bridge`; `may_touch_pipe_networks` when `params` has `acknowledge_gas_lost` at the top level.
- **`protocol_methods` is empty** until a protocol layer dispatches methods of its own (stage 4); test 2 asserts it.
- **Test 3, names read.** Files reached one level down from a handler (`BuildArgs.cs`, `RunArgs.cs`, `AtResolver.cs`
  through them) are shared by many methods and read names for all of them, so "every name read exists in the method's
  `params`" cannot hold per method. The test holds every name read through `Args` anywhere under `Api/` to be declared
  by some method's `params`, as a property or, inside an object `params` describes only in prose (`at`,
  `relative_to`, `reroute.between`), named in a description. The exact per-method check is the run-time drift counter.
  The other direction is as specified: every top-level parameter is read in the handler's files or named in
  `x-read-by`, whose file must contain the name as a literal (12 methods use it).
- **Test 4.** `PageRequest.From(args, default, maximum)` counts as reading `limit` from 1 to `maximum`. A name read
  with bounds in the handler's own file takes those; otherwise the bound every other file of the handler agrees on;
  a name read with different bounds gets none (`thing_health`'s `limit`, which differs between the scan and the
  network form).
- **Test 5** compares the top level: the keys the views in `x-views` write plus the shared keys that apply, and which
  of them are lists. 89 methods declare `x-views`; `move_gas` (one of its views lives in a file that uses game types)
  and `sample_logic` (answered by the sidecar) have hand-written replies. Entry schemas are not described yet.
- **Test 6** also counts const strings named `...Code` and `...Code` properties that return a literal
  (`GasHoldRule`'s verdicts, `RunKind.ShortageCode`). A tool code's description is the message at its first refusal
  site, interpolations shown as `<name>`; the protocol codes come from protocol.md's table, and the sidecar's
  `game_unavailable` is a `client` code. Methods do not list their own `errors` yet.
- **Not filled yet** (all optional): `area`, `since`, per-method `errors`.
- **The mod.** It loads the catalogue when it starts and logs `Catalogue loaded: N methods`; a catalogue that does not
  load is logged as an error and every request is answered `internal_error` naming the problem. The drift counter is
  `mod_info.runtime.catalogue_drift`, a list of `{method, argument, reads}`, and the first read of each pair is logged.
- **The bootstrap** (`STATIONGOD_BOOTSTRAP_CATALOGUE=1`) ran once and was deleted with `ToolDefinitions`, which it read.
- **Tests** validate `catalogue.json` against `catalogue.schema.json` with JsonSchema.Net, a test-only package.

## As built in stage 14

- **Entry schemas.** A list in `reply` may describe its entries (`items` with `properties`); test 5 then holds them
  to the views the list holds, as deep as the items describe (`things[].networks[]` too): the same keys, and the same
  ones lists. A list's views come from its element type in the `x-views` classes. A batch's element type is abstract
  (`BatchItemView`), so the method names its entry views in the new optional `x-entry-views`, keyed by the list's path
  (`{"results": ["HealthItemView", "BatchErrorView"]}`); entry views are not top-level views, which is why they do not
  go in `x-views`.
- **`x-costly` test.** Each part names a list of the reply whose entry schema describes the key, and the handler's own
  file asks `Shape.Wants` about exactly the declared parts, both names literal or const strings. Only the handler's
  own file counts: the files one level down are shared (`grid_survey` reaches `ThingHealth.cs`), so an ask there
  cannot be told apart by method.
- The mod reads neither `x-costly` nor `x-entry-views`; the handlers ask with literals and the tests hold the two
  together. `thing_health` is the one method with entry schemas and `x-costly` so far.

## Help and the text rubric

Tool descriptions are short; the long help is read through the `tool_info` tool, one small node at a time. Both come
from the catalogue sources, and `CatalogueTextTests` holds every text to the rubric below, so `build.ps1` fails on a
tool, argument or error code that skips any of it.

### Where the text lives

- `methods/<name>.json`: `summary` (two or three lines: what the tool does, the arguments that matter, its safety
  flags) and `help` {`text`, `topics`, `shared`}. Assembly writes `description` = `summary` plus the last line
  `More: tool_info {tool: "<name>"}`. A topic is {`summary` (one line), `text`, `subtopics`}; a subtopic is
  {`summary`, `text`}. `shared` lists the shared topics the tool leads to. Argument descriptions stay in `params`, one
  line each.
- `help.json`: `intro` and the shared topics: `topics` (listed by `tool_info {}`: positions, turns, refunds, paging,
  shaping, truncation, cheats, jobs, gateways, player, networks, errors) and `families` (shared by a family of tools,
  listed by those tools: runs, routing, removals, swaps, pipes, gas_hold, materials, placing, doors, lint, logic,
  chips, items, slots, trading, vaults, rockets).
- `errors.json`: every code's `description` (a plain message: what went wrong, what to do) and `see`
  ({tool, topic, subtopic}, the node that explains it). The mod adds `see` to every error it answers.
- `text-allow.json`: exceptions to the banned patterns, each `{rule, text, reason}`. An entry that no longer matches
  anything fails the lint.

`tool_info` levels: `{}` intro and root topics; `{tool}` its text, its topics and the shared topics it lists;
`{tool, topic}` or `{topic}` a topic and its subtopics; `{tool, topic, subtopic}` or `{topic, subtopic}` the detail.
Three levels, no search. It is a catalogue method with `x-runs-in: sidecar`: the MCP server answers it from its
catalogue (the mod's when the hashes differ) with `ToolHelp` (`src/StationGodMCP.Client`), without the game.

Pointers in text: `(see X)` and `(see X/Y)` name a shared topic and subtopic; `(topic X)` names a topic of the same
tool; `tool_info {tool: "a", topic: "b"}` names any node.

### The rubric

- Present tense: say what the tool does now. No history.
- Banned:
  - version numbers and "since", "new in", "added in", "as of" (`1.4.3+`, `v1.5`);
  - change words describing the past: "now", "no longer", "was", "were", "used to", "previously", "changed",
    "renamed", "replaces", "formerly", "anymore"; phrase the text without them;
  - dates;
  - code internals: C# type and member names (`Foo.Bar`, `Foo()`), file and file:line citations, Harmony and patch
    talk, the game's internal method names;
  - person names (the lint refuses decisions credited to someone; set `STATIONGOD_BANNED_NAMES` to a comma list
    of names to refuse them too), URLs, em dashes.
- Allowed: game terms players see (prefab names, logic types, item names), units, JSON argument examples, limits and
  defaults, safety rules.
- Each node states its effect, its limits and defaults, and at most one example.
- A rule a check already enforces is not explained in advance: the error explains it when it fires.
- Every change note goes to CHANGELOG.md only.

### The lint (`tests/StationGodMCP.Tests/Catalogue/CatalogueTextTests.cs`)

- (a) Each tool description is 2 to 4 lines, at most 440 characters with the More line, ends with the More line, says
  "Cheat" when its class or a class rule is cheat, and "dry run" when it dry-runs by default.
- (b) Every `tool_info` node (root, each tool, topic and subtopic) is at most 1,536 bytes; argument descriptions are
  one line of at most 200 characters; topic summaries one line of at most 100.
- (c) No text breaks a banned pattern (descriptions, argument descriptions, help, error messages, server
  instructions, the sidecar's own argument texts), except `text-allow.json` entries; every entry names a rule and a
  reason and still matches something.
- (d) Every property in every tool's params, nested ones included, has a description; every tool has help.
- (e) Every pointer, `shared` entry and error `see` resolves; a family topic a tool's text points at is in its
  `shared`; no family topic is orphaned.
- (f) Every error code the mod's code can return (scanned as test 6 scans them) and the sidecar's own codes have a
  message and a `see`; error messages written in the mod's code name no code internals.
- (g) `catalogue.json` matches its sources (test 1).
- (h) The size of tools/list as the MCP server writes it is reported (`STATIONGOD_SIZE_REPORT=<file>` also writes the
  report and the list) and capped: tool descriptions at most 30,000 bytes, the whole list at most 200,000.

The schema structure (types, enums, bounds, `additionalProperties`) is about 70 KB of the list and is not text; the
caps hold the rest.
