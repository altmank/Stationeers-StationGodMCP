# catalogue/verbs: the verb catalogue

The single source of StationGod's verb surface (PLAN v9 2.0): 15 verbs plus help and run_script, 91 whats. The MCP
tools array, the help pages, the Python stubs and LegacyVerbMap's table are generated from these files; edit them
here, never the generated copies.

## Files

- `<verb>.json`, one per verb: `description` (the tool description), `text` (more lines for help {verb}), `tool`
  (the tool list's own short texts: `options` by name), `reply_top` (keys every reply of the verb starts with, such as
  build's `placed`), and `whats`.
- `_shared.json`: the verb order, shared options (an option with `"use": "<name>"` takes that definition, its own keys
  replacing the shared ones), the instructions, the five small types, help topics, and `report_top` (`report_only`).
- `_errors.json`: today's error codes in verb names, each with a pointer to verb help.

Per what:

| key | meaning |
|---|---|
| `summary`, `brief` | the help line; `brief` (when present) is the shorter text in the tool's what enum |
| `options` | per option: `schema`, `description` (the help text), `default` (as help states it), `required`, `x-script` |
| `x-script` | `same` (the stub uses the description), `drop` (not in scripts: D7 and the shaping options), or the stub's own text |
| `text`, `x-script-text` | the what's notes on the direct page, and on the stub when they differ |
| `example` | arguments; rendered as `verb {what, ...}` and as `sg.verb('what', ...)` |
| `handlers` | today's methods: `name`, `when` it is the one, `args` (each handler argument, the option it comes from, and how: renamed, merged, moved, kept, default-changed), `pending` for 1a handlers, `hub:` names for the hub's own |
| `reply` | `list` (the main list) and `variants` (typed `keys`, or a `job_view`), plus the small `types` used |
| `x-list` | on reading whats that take an id: how an id list is taken |
| `write` | on every write what (below) |

Per write what (`write`):

| key | values |
|---|---|
| `report` | where the report form comes from: `dry_run` (the handler's own), `mod` (the mod's 1a report path), `hub` (the hub's instant-write form), `none` |
| `route` | `acts` (typed ids act at once; refs, networks and spreads report until confirm) or `confirm` (reports until confirm) |
| `cheat` | `always`, or the option that makes it one (`free: true`, `force: true`) |
| `x-undo`, `x-undo-mark` | `undo`, `cannot` or `mixed`, and the footer's mark text |
| `x-list` | `native` (the handler takes the list whole), `split` (one call per id), `native-shape`, `one` (a list is refused; also a what with no id option) |
| `x-target` | option paths whose values name existing things the write acts on (`acts-on`) or pays from (`pays-from`); every path is under the seen rule |

## Readings for the hub (agreed with the orchestrator)

- `x-target`:
  - `refund_to` is `pays-from`: a holder whose contents the job changes.
  - `join_to` and `remove_ids` (cable, pipe, chute builds) are `acts-on`.
  - `undo`'s `id` (job, bulk write or script run ids) is `acts-on`.
  - build structure and build blueprint have no id targets: placement cells are not seen-checked.
  - `materials` is `pays-from` everywhere it appears.
- `x-undo`: uplink and flight_log are `cannot` (SPEC-NOTES), over DESIGN 7.2's [undo].
- Report class: move item is `mod` (the 1a path) for refs, filters and store moves; typed ids act at once (note 27).
  server stop and profiling are write whats with report `none`.
- `x-list: one` also marks write whats with no id option (build structure, move gas, server command...): there is no
  id list to give.
- inspect controllers' handler `controllers` is pending (1a). Hand-declared reply shapes (no typed view existed): help,
  inspect job, move gas, read sample, server stop, undo.
- Scripts (v9): two routes, no run repeat; in a script, per-id whats always answer `{results: [...]}` (note 28). The
  direct face keeps `repeat: "rN"` (call a ref again; face register D2).

## Generating and checking

Generated into `generated/verbs/`: `tools.json`, `help/`, `stubs/`, `legacy-verb-map.json`, `sizes.json`. Rewrite them
with `STATIONGOD_WRITE_CATALOGUE=1` and the test `VerbCatalogueTests.GeneratedFilesAreCurrent` (the same switch
formats these sources). The tests are in `tests/StationGodMCP.Tests/Verbs/`; the generators are the C# classes beside
them. Sizes use one tokenizer calibrated against Claude Code (`tools/tool_list_cost.py`); the budget is the listing
plus the median loaded schemas per fresh context, at most today's figure on the same tasks.
