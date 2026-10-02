# The method catalogue

[Back to the overview](README.md)

The catalogue is the one description of everything the mod offers: every method, its arguments with types, ranges
and descriptions, the shape of its reply, its permission class and its cost class, plus the error codes and the text
the sidecar gives agents. The mod checks requests against it, the sidecar turns it into MCP tools, and the client
libraries turn it into calls. This page says what it looks like, where it lives, how it stays true to the code, and
who reads it.

## Where it comes from today

Three partial descriptions exist now, and they are kept in step by hand and by one test:

- The sidecar's tool definitions: names, descriptions and JSON Schema input schemas written as C# objects
  (`src/StationGodMCP.Server/Program.cs:583-678` for names, `:713` onward for the tools, `:2161-2196` for how each is
  wrapped), with a read-only flag per tool that becomes MCP annotations (`Program.cs:2170-2173`), and a hand-kept list
  of tools whose replies are small (`Program.cs:680-685`).
- `tool-arguments.json`, the top-level argument names per tool, written from the sidecar's schemas by a test
  (`src/StationGodMCP.Server/ToolArguments.cs:12-30`, `tests/StationGodMCP.Tests/ToolArgumentsFileTests.cs:16-28`) and
  embedded in the mod (`StationGodMCP.csproj`, `EmbeddedResource tool-arguments.json`), which uses it to refuse unknown
  names (`src/StationGodMCP.Mod/Api/ApiHost.cs:190-206`, `:165`).
- The handlers themselves, which read their arguments one by one with ranges written in code
  (`src/StationGodMCP.Mod/Api/Shared/Args.cs:31-223`, for example `OptionalInt(name, minimum, maximum)` at `:104`).
  Ranges live only there; the sidecar's schemas leave them out on purpose (`src/StationGodMCP.Server/ArgumentCheck.cs:14-16`).

Reply shapes are described only in prose inside each tool's description and in `docs/`.

## Where it lives

```text
catalogue/
  catalogue.schema.json     the format below, as JSON Schema
  server.json               server name and the instructions text for agents
  errors.json               every error code
  defs/<name>.json          shared pieces of argument and reply schemas (refund_to, network handles, item filters, ...)
  methods/<method>.json     one file per method
catalogue.json              assembled from catalogue/, checked in, embedded in the mod and the sidecar
```

`catalogue.json` sits at the repository root, where `tool-arguments.json` is today, and replaces it. It is assembled,
not hand-edited: a test writes it when `STATIONGOD_WRITE_CATALOGUE=1` is set and otherwise fails if it differs from
what the sources give, the same habit `ToolArgumentsFileTests` has today. The assembled file inlines every `$ref`, so
no reader ever resolves references.

One file per method, because a method is what changes together, and a diff of one JSON file per method reads well.
Plain JSON, because the mod has no other parser available without new dependencies.

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
  "x-cost-when": [{"when": {"reference_ids": {"present": true}}, "cost": "bounded"},
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
    "properties": {
      "results": {"type": "array", "items": {"$ref": "defs/health_entry.json"}},
      "things": {"type": "array", "items": {"$ref": "defs/health_entry.json"}},
      "count": {"type": "integer"},
      "has_more": {"type": "boolean"}
    }
  },
  "x-views": ["HealthScanView", "HealthByIdsView"],
  "x-shaping": "lists",
  "x-paging": {"list": "things", "offset": "offset", "limit": "limit", "total": "total", "has_more": "has_more"},
  "x-costly": [{"list": "things", "key": "networks"}, {"list": "results", "key": "networks"}],
  "deprecated_aliases": {"min_ratio": "min_damage_ratio"},
  "errors": ["thing_not_found", "invalid_argument"]
}
```

(The view class names, ranges and costs above illustrate the format; the real values come from the bootstrap in
stage 2 and the handlers' code.)

### The format as JSON Schema

`catalogue/catalogue.schema.json`:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "stationgod-catalogue-1",
  "type": "object",
  "additionalProperties": false,
  "required": ["catalogue_version", "mod_version", "server", "errors", "methods", "protocol_methods"],
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
          "resend": {"enum": ["never", "if_read", "always"], "default": "never"}
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
        "cost": {"$ref": "#/$defs/cost"}
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
        "x-reply-check": {"enum": ["views", "manual"], "default": "views"},
        "x-shaping": {"enum": ["lists", "none"]},
        "x-paging": {
          "type": "object", "additionalProperties": false, "required": ["list", "offset", "limit"],
          "properties": {"list": {"type": "string"}, "offset": {"type": "string"}, "limit": {"type": "string"},
                         "total": {"type": "string"}, "has_more": {"type": "string"}}
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
        "deprecated": {"type": "boolean"},
        "deprecated_aliases": {"type": "object", "additionalProperties": {"type": "string"}},
        "errors": {"type": "array", "items": {"type": "string"}}
      }
    }
  }
}
```

### What each part means

`class` and `x-class-when` give the permission class (see [protocol.md](protocol.md), *Sign-in and permissions*).
Rules are tried in order; the first whose `when` matches gives the class; if none matches, `class` applies. A `when`
matches when every argument it names matches its matcher (and, with `any_of`, at least one of those also matches).
Matchers: `equals` (JSON equality after the same trimming and case folding the mod applies to words), `in` (equals one
of), `present` (given and not null), `absent` (missing or null; the mod reads null as omitted, `DeclaredArguments.cs:61`).

`cost` and `x-cost-when` give the cost class the scheduler starts from ([scheduling.md](scheduling.md)):

| Cost | Meaning |
| --- | --- |
| `instant` | One thing, a handful of values. |
| `bounded` | Grows with the request, capped by the method's own bounds (at most 128 items, 256 ids, ...). |
| `world` | Walks a world-sized collection: every thing, every device, every cell of an area. |
| `plan` | Searches or simulates: route planning, dry runs, forecasts. |
| `job` | Starts or advances a building job that holds the game tick. |
| `stream` | Runs over time: `sample_logic`, subscriptions. |

`x-effects` names side effects that are not world changes: `display` (`highlight`, `show_preview` draw on the host's
screen), `server_state` (the flight recorder), `files` (CSV files in the save folder).

`params` is the argument schema. `reply` describes the reply's top-level object; its lists' `items` describe their
entries to the depth that `fields` paths may reach. `x-views` names the C# view classes the reply is built from, which
the tests check against `reply`. `x-shaping: "none"` marks a method whose reply is always small; MCP clients get no
`output_file` or `fields` for it, as `SmallReplies` does today (`Program.cs:680-685`). `x-paging` names the method's
own paging arguments and reply keys so libraries can walk pages for any method the same way. `x-costly` lists reply
parts the handler skips when `fields` leaves them out. `x-duration` names the argument that sets how long a
long-running method takes and its maximum. `x-job` marks a method that starts building jobs and names its polling
argument. `x-needs-mod` lists optional mods a method needs. `x-mcp: "hidden"` keeps a method out of the MCP tool list
(protocol methods). `x-read-by` names a parameter that the handler's own file does not read and the shared code that
does (see *The consistency tests*). `deprecated_aliases` maps old argument names to new ones.

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

The class of every method, as recommended to the owner (the overview's first question). Read methods are those the
sidecar marks read-only today (`Program.cs:2161-2175`, `readOnly: true`), plus dry runs and polls of the others.

| Class | Methods |
| --- | --- |
| read | `list_gateways`, `list_devices`, `describe_device`, `read_logic`, `read_logic_many`, `read_devices`, `read_memory`, `inspect_slots`, `network_snapshot`, `sample_logic`, `get_ic_source`, `get_ic_status`, `resolve_ic_selectors`, `read_console`, `game_clock`, `find_items`, `find_things`, `item_totals`, `list_containers`, `container_contents`, `player_vitals`, `consumables`, `atmosphere_contents`, `water_sources`, `trader_contacts`, `dish_aim`, `trader_inventory`, `plants`, `reagents`, `planet`, `deep_miner_spots`, `solar_aim`, `thing_health`, `outer_frames`, `rooms`, `weather`, `ignition_risk`, `looking_at`, `connections`, `mod_info`, `landing_pads`, `rocket_status`, `rocket_forecast`, `describe_prefab`, `wall_map`, `find_spot`, `lint_layout`, `lint_rules`, `check_replaceable`, `show_preview` (display), `highlight` (display), `grid_survey`, `plan_cable_route`, `plan_pipe_route`, `plan_chute_route`, `plan_removal`, `feed_paths`, `vault_contents` |
| write | `write_logic`, `write_logic_many`, `write_memory` (a chip's `put`), `set_ic_source`, `control_ic_execution`, `set_ic_pins`, `label` (the Labeller), `paint` (a spray can), `move_item` (the game's own slot moves), `trader_buy`, `trader_sell` (the trade window's own calls), `vault_deposit`, `vault_withdraw` (the vault's own bookkeeping), `rocket_flight_log` (start, stop, clear; server state and files), and every building tool on a real run: `place_cables`, `place_pipes`, `place_chutes`, `remove_cables`, `remove_pipes`, `remove_chutes`, `upgrade_cables`, `upgrade_pipes`, `clean_cables`, `clean_pipes`, `replace_walls`, `replace_frames`, `place_structure`, `remove_structure`, `undo_job` |
| cheat | `run_console_command`, `move_gas`, `plant_genes` when it edits, `paste_blueprint` when it pastes or undoes, `place_structure` with `free: true` |

The argument rules that make this exact:

| Method | Rules, in order |
| --- | --- |
| building tools whose dry run is the default (`place_*`, `remove_*`, `upgrade_*`, `clean_*`, `replace_*`, `place_structure`, `remove_structure`, `undo_job`, `vault_deposit`, `vault_withdraw`) | `dry_run` absent: read; `dry_run` equals true: read (this covers `job_id` polls, which carry no `dry_run`) |
| `place_structure` | before the above: `free` equals true and `dry_run` equals false: cheat |
| `move_gas`, `trader_buy`, `trader_sell` (dry run off by default, `Program.cs:1215`, `:1571`, `:1606`) | `dry_run` equals true: read; `transfer_id` present (a `move_gas` poll): read |
| `plant_genes` | `genes` absent: read; otherwise cheat |
| `paste_blueprint` | `status` equals true: read; otherwise cheat (GUESS that `status` is its only read form, from `Program.cs:1623-1624`) |
| `rocket_flight_log` | `action` absent or `in` [`read`, `list`]: read; otherwise write |

The cost classes start from the method's nature and are checked against stage 0's measurements when stage 2 writes them, and refined at run time by the scheduler (see
[scheduling.md](scheduling.md)): `instant` for single reads and writes, `bounded` for the batch forms and
`read_devices`, `world` for the scans (`list_devices` without a filter, `find_things`, `find_items`, `item_totals`,
`list_containers`, `thing_health` without ids, `grid_survey`, `lint_layout`, `rooms`, `plants`, `outer_frames`,
`deep_miner_spots`, `wall_map`, `consumables`, `water_sources`, `ignition_risk`), `plan` for planners, dry runs of
building tools and forecasts, `job` for real runs of building tools, `stream` for `sample_logic`.

## The consistency tests

The catalogue is useful only if it cannot drift from the handlers. These tests run in the existing test project
(`tests/StationGodMCP.Tests`), which compiles the mod's `Pure`, `Api/Shared` and `Api/Views` folders and references the
sidecar, and already parses mod sources with Roslyn for checks of this kind (`tests/StationGodMCP.Tests/SnakeCaseTests.cs:31`,
`ReplaceStructuresTests.cs:569`). Any of them failing fails the build's test run.

1. **The file is current.** `catalogue.json` equals what the sources assemble to, and validates against
   `catalogue.schema.json`. `STATIONGOD_WRITE_CATALOGUE=1` rewrites it.
2. **Same methods.** The method names in `catalogue.json` equal the keys of `ApiHost.Methods`
   (`src/StationGodMCP.Mod/Api/ApiHost.cs:26-120`, read with Roslyn from the source), plus the protocol methods, which
   the protocol layer must handle (checked the same way against its dispatch table). The failure names every method on
   one side only.
3. **Same arguments.** For each method, the test finds its handler class from the `ApiHost.Methods` entry
   (`static args => ThingHealthApi.Handle(args)`), collects every string literal or `const string` passed as the first
   argument to an `Args` reader (`Has`, `ThingId`, `OptionalThingId`, `ThingIds`, `String`, `OptionalString`,
   `OptionalBool`, `OptionalInt`, `OptionalDouble`, `Double`, `Int`, `OptionalPositiveDouble`, `Array`, `OptionalObject`,
   `IsWord`, `Reject`, `Optional`, `Objects`, `With`; `Args.cs:31-223`) in that class's file and in every `Api/Shared`
   file that declares a method taking `Args` that the handler file names, and then checks both directions:
   every name read exists somewhere in the method's `params`, and every top-level parameter is read, or is named in
   `x-read-by` with the file that reads it. The failure names the method, the argument and the file.
4. **Ranges agree.** Where a reader passes literal bounds (`OptionalInt("limit", 1, 500)`), the catalogue's `minimum`
   and `maximum` for that argument must be those numbers. Bounds held in constants are resolved from the same file.
5. **Reply views agree.** For each method with `x-views`, the test reflects over the named view classes (compiled into
   the test project from `Api/Views` and `Api/Shared`), takes the property names the mod's serialiser settings give them
   (`src/StationGodMCP.Mod/Api/Shared/ApiJson.cs:43-60`: snake case, `[JsonProperty]` names kept), and checks them
   against `reply`: the top level, and each list's entry type against that list's `items`, as deep as `items`
   describes. Only three handler files build top-level replies from anonymous objects (`Api/Highlight.cs`,
   `Api/PlaceRuns.cs`, `Api/ShowPreview.cs`); methods built there are marked `x-reply-check: "manual"`, and the test
   fails if any other method lacks `x-views`.
6. **Every error code is registered.** Every code literal passed to `ApiErrors.Refused`, `new ApiException` or
   `new ErrorView` in the mod's sources (Roslyn), and every `ApiErrors` constant
   (`src/StationGodMCP.Mod/Api/Shared/ApiErrors.cs:21-23`), is in `errors.json`, and every `tool` entry there is used.
7. **One version.** `catalogue.json`'s `mod_version` equals the version `build.ps1` checks in five places (owner notes,
   `CLAUDE.md`, Identity).

At run time the mod adds a drift counter. `Args` records each name a handler reads; after the handler returns, any
name read that the method's `params` does not declare is counted per method in `mod_info.runtime.catalogue_drift` and
logged once. The test server's live checks require it to stay at zero ([stages.md](stages.md)).

While stage 2 moves the tool definitions out of the sidecar, one more test proves nothing changed for agents: the
sidecar's `tools/list` built from the catalogue equals, byte for byte, the one built from today's `ToolDefinitions`.
It is deleted with `ToolDefinitions`.

## Who reads it

**The mod** embeds `catalogue.json` as a resource, as it embeds `tool-arguments.json` today (`ApiHost.cs:190-206`).
At load it parses it into a pure `Catalogue` (no game types, so the tests compile it): per method the compiled
argument validator, the class rules, the cost class, shaping data and costly parts. A catalogue that does not load is
a fatal error for the protocol: the mod logs it and answers every call with `internal_error` naming the problem,
rather than running unchecked. The protocol method `catalogue` returns the embedded file; `welcome` carries its hash.

**The sidecar** embeds the `catalogue.json` it was built with and answers `tools/list` from it at once. Each method
whose `x-mcp` is not `hidden` becomes a tool: `name`; `description`; `inputSchema` = `params` with `$ref`s already
inlined, plus `output_file` and `fields` when `x-shaping` is `lists` (as `WithReplyShaping` adds them today,
`Program.cs:2178-2196`); annotations `readOnlyHint` true only for methods whose class is read with no class rules,
`destructiveHint` its opposite, `idempotentHint` equal to `readOnlyHint`, `openWorldHint` false, as today
(`Program.cs:2168-2174`). The server's `instructions` come from `server.json`. After connecting, if the mod's hash
differs, the sidecar calls `catalogue`, rebuilds its tool list and sends MCP's `notifications/tools/list_changed`
(advertising `listChanged: true`, which is `false` today, `Program.cs:132`).

**The Python library** ships the `catalogue.json` it was built with and a generated module of typed call stubs
(keyword-only arguments with the catalogue's names and defaults, docstrings from the descriptions, return type a plain
dict). At connect it fetches the mod's catalogue when the hash differs and uses that for every decision it makes
from the catalogue (classes for resending, paging, shaping lists); the stubs are a convenience, and `call(method,
**params)` works for any method the mod has.

**The documentation.** The per-area pages in `docs/` keep their prose. A later, optional step can generate each page's
argument tables from the catalogue; it is not part of this plan.
