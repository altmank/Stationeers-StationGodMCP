# catalogue/verbs: the verb catalogue

The single source of StationGod's verb surface (PLAN v9 2.0): 15 verbs plus help and run_script, 91 whats. The MCP
tools array, the help pages and the Python stubs are generated from these files; edit them
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
| `reply` | `list` (the main list) and `variants` (typed `keys`, or a `job_view`), plus the small `types` used |
| `id_list` | where an id list is taken as the entries of another list option (move item's `moves`, read sample's `reads`) rather than one row per id |
| `write` | on every write what (below) |

Per write what (`write`):

| key | values |
|---|---|
| `route` | `acts` (typed ids act at once; refs, networks and spreads report until confirm) or `confirm` (reports until confirm) |
| `cheat` | `always`, or the option that makes it one (`free: true`, `force: true`) |
| `x-undo`, `x-undo-mark` | `undo`, `cannot` or `mixed`, and the footer's mark text |
| `x-target` | option paths whose values name existing things the write acts on (`acts-on`) or pays from (`pays-from`), or pick its targets (`spread`: a network, room, box, near, rocket or a filter; decision M6); every path is under the seen rule, a spread as a selector; `when` (option matchers as in `x-class-when`: `equals`, `in`, `present: true`, `absent: true`) makes a path a target only for calls where every one holds |

## Readings for the hub (agreed with the orchestrator)

- `x-target`:
  - `refund_to` is `pays-from`: a holder whose contents the job changes.
  - `join_to` and `remove_ids` (cable, pipe, chute builds) are `acts-on`.
  - `undo`'s `id` (job, bulk write or script run ids) is `acts-on`; scripts give it (`sg.undo(id=...)`).
  - build structure and build blueprint have no id targets: placement cells are not seen-checked.
  - `materials` is `pays-from` everywhere it appears.
- Selection options (C2b review): remove cable/pipe/chute waypoints and cells are `spread`; swap into, tidy, spare
  and riding are `spread` (they pick which pieces change); every `root` is `acts-on` (it decides what is cut off or
  redundant); write chip `pins.d0`..`pins.d5`, move player's `to` and `to_player` are `acts-on`; trade's `card` is
  `pays-from`. Not targets: gateway, allow_bridge and assume_removed (routing and consent), and build placements.
  The test EveryWriteOptionThatPicksATargetIsAnXTarget holds the reviewed list.
- `x-undo`: uplink and flight_log are `cannot` (SPEC-NOTES), over DESIGN 7.2's [undo].
- move item (PLAN 2.0): typed ids act at once, slot and store moves alike (note 27); a ref or a filter reports until
  confirm. An id list is one `moves` list, a slot per move (`auto` when left out).
- Spreads (decision M6): move item's filters (prefab, name, location, within, near, radius,
  category), swap's room, network, box, prefab, only and older_than, paint's network and kind, battery's rocket and
  within. Move item's prefab is a spread only into a store (catalogue C2a fix): with source it names what to take out
  of one, and with slot or moves the move is a slot move. Whether `to` is a vault or silo is the game's to know, so a
  call with neither counts its prefab as a spread.
- Hand-declared reply shapes (no typed view existed): help,
  inspect job, move gas, read sample, server stop, undo.
- Scripts (v9): two routes, no run repeat; in a script, per-id whats always answer `{results: [...]}` (note 28). The
  direct face keeps `repeat: "rN"` (call a ref again; face register D2).

## Generating and checking

Generated into `generated/verbs/`: `tools.json`, `help/`, `stubs/`, `sizes.json`. Rewrite them
with `STATIONGOD_WRITE_CATALOGUE=1` and the test `VerbCatalogueTests.GeneratedFilesAreCurrent` (the same switch
formats these sources). The tests are in `tests/StationGodMCP.Tests/Verbs/`; the generators are the C# classes beside
them. Sizes use one tokenizer calibrated against Claude Code (`tools/tool_list_cost.py`); the budget is the listing
plus the median loaded schemas per fresh context, at most today's figure on the same tasks.
