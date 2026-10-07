# Reply budget

Every tool's default reply on a large world stays under 8 KB (8,192 bytes of the mod's compact JSON). A test holds
every tool to it, so a heavy default cannot come back unnoticed. This page says why the rule exists, how the test
works, what keeps replies small, and what each tool lists by default.

## Why the earlier passes missed heavy replies

Three earlier passes slimmed replies (1.7.0, 1.9.0, the shaping work of architecture-v2 stage 1). Each answered the
replies someone had just seen live, one tool at a time:

- 1.7.0 fixed "63 KB grid_survey, 22 KB get_ic_status, 20 KB place_structure polls" (CHANGELOG 1.7.0) with an
  opt-in switch per tool (`sections`, `include_source`, `include_footprint_cells`, `include_notes`), and promised to
  keep each default as it was: "every switch leaves the default reply as it was where that was promised"
  (`tests/.../HeavyPayloadTests.cs`, its summary). get_ic_status kept sending a 60 KB Lua source by default.
- 1.9.0 fixed a 112 KB find_things, a 32 KB chute network and 277 KB of misspelt filters, again per tool
  (`location`, connections filters, device lists as counts), and added `fields` for list entries only.
- No pass measured every tool, so tools nobody had called on a big base stayed heavy: 395 KB consumables, 408 KB
  network_snapshot, 230 KB item_totals, 222 KB plants, 172 KB thing_health scans, 138 KB upgrade reports (all on
  the large world below), and the 15 KB of riding items in a place_chutes dry run that the 1.9.0 device-list switch
  did not cover.
- `fields` reached only the keys of top-level list entries (protocol.md, Field selectors: "It does not touch
  top-level keys that are not lists"; "A selector whose first name is not a top-level list of the reply matches
  nothing"), so a caller could not drop a source, a register dump or a nested key such as `occupant.prefab_name`.
- Tools marked small (x-shaping none) were never checked: paint answers up to 256 results and set_ic_source echoed
  the whole source it was sent.
- Fixed explanations went out in every reply: notes in place_structure, remove_structure, upgrade, clean and replace
  reports and in eight read tools, `how_to_get` in every machine-stock entry, a gene's meaning in every gene of every
  plant.

Tested on small worlds is part of it (the test server's saves are a few rooms), but the method gap is the main one:
fixes followed reports, nobody inventoried every tool, and no test held a default reply to a size.

## The test

`tests/StationGodMCP.Tests/Budget/`:

- `LargeWorld`: the world every reply is measured on (a 1,257-member cable network, a 60 KB Lua hub chip, 150
  devices, 6,000 things, 4 rockets, 48 plants, ...).
- `ReplyShapes`: per method, its default reply's view and how long each list is on the large world, with the
  reason: a page size from `ReplyDefaults`, a fixed count (6 pins, 18 registers), a request's size, or the world's.
  This table is the inventory below in code.
- `ReplySynth`: builds the view behind its constructors with every field set, each list as long as its declaration
  says and each list nobody declared 64 entries long, so a new list fails the budget until its bound is declared.
- `ReplyBudgetTests`: every catalogue method has a shape or a reasoned exemption (a new tool fails until it is
  placed), and every shaped method's default reply is at most 8,192 bytes, its top-level lists cut by the method's
  `x-default-limits` as the mod cuts them.
- `ProbeTests`: idle unless an environment variable is set; `SG_SIZES=<file>` writes every method's size,
  `SG_DETAIL=<method> SG_DETAIL_OUT=<file>` breaks one reply down by key, `SG_TREE=<file>` lists every view's fields.

The synthetic replies are an upper bound: every optional key is filled, every string is 20 characters, every number
has four decimals. A real reply is usually smaller.

Adding a tool: add its shape to `ReplyShapes` (run `SG_TREE` to see its view's lists), or an exemption with the
reason. Adding a list to a view: declare its bound, or the test fails naming it.

## Nothing held back silently

Every reply ends with `truncated` (protocol.md, Truncation notice): per list it holds back entries of, the list, how
many it returned, the real total (or `at_least`), and the argument that gets the rest; `[]` when nothing was held
back. `TruncationNoticeTests` enforces it: on the large world every list a method's `x-default-limits` cuts is named
with its whole length, every source file that sets a default or a maximum for a list (a `ReplyDefaults` constant, a
page) notes its cut or hands the list to a view that does (`ITruncatingView`), and the notice survives `fields`,
`omit` and `output_file`. Opt-in parts left out by default (rooms' devices, a chip's source, report notes) are not
truncations: the description names the switch that adds them.

## What keeps replies small

- Page sizes in one place, `src/StationGodMCP.Mod/Api/Shared/ReplyDefaults.cs`, read by the handlers and the test.
- `x-default-limits` in a method's catalogue entry: the mod keeps that many entries of a top-level list when the
  call does not limit it, and names the list in `truncated` with its full length
  (`ShapeRequest.WithDefaultLimits`, applied in `CallSession`). The sidecar's `list_limits` argument lifts it.
- Selectors: `fields` keeps keys, read from each list entry at any depth or from a named list; `omit` drops keys by
  path from the reply's top, top-level keys included (protocol.md, Shaping).
- Fixed explanations belong in the tool description. The read tools' notes are gone from replies; report notes and
  gene meanings come only with `include_notes`, the rocket tools' explanations only with `explain`.
- Compact forms where a list's entries are heavy: plants in short unless `verbose` or one plant; a brief job poll's
  log counts placed pieces; chute items follow `include_network_devices`; a place_structure report that is not ready
  leaves its layouts out unless `verbose`; get_ic_status and set_ic_source leave the source out unless
  `include_source`, and a Lua chip has no IC10 runtime.
- Files: `output_file` for a whole large reply, `source_file` for a large chip source sent in.

## Inventory

Sizes are the synthetic default reply on the large world, largest of each tool's shapes, in bytes. "Before" is the
same reply with the 1.13.0 defaults. What grows: the parts of the reply that scale with the world, a page, a request
or a text. A tool whose sizes match changed nothing.

| Tool | What grows | Bound now | Before | After |
| --- | --- | --- | ---: | ---: |
| atmosphere_contents | a device's atmospheres; a fixed note | 4 atmospheres; note in the description | 5,559 | 5,241 |
| check_replaceable | one result per id asked | the request | 1,165 | 1,165 |
| clean_cables, clean_pipes, upgrade_cables, upgrade_pipes | pieces, kept, unmatched, dead ends, a network's devices (twice); notes | limit 5 (was 200); devices 5 (x-default-limits); networks give device_count; notes with include_notes | 138,554 | 7,484 |
| connections | a network's members | page 40 (was 200); area filter (min/max, near/radius_m) | 37,882 | 7,930 |
| consumables | every food, drink and package in the world; a note | 3 of each, 5 not counted (x-default-limits) | 395,109 | 7,954 |
| container_contents | slots, nested to depth 3; a silo's stored entries | depth (default 3); silo entries 10 (entries_limit) | 6,908 | 6,908 |
| control_ic_execution | none | | 621 | 621 |
| deep_miner_spots | profiles, spots, beacons | count 5 | 6,838 | 6,838 |
| describe_device | logic types (45 to 75), an uplink's choices; a fixed note | count and writable names; the list with include_logic_types; note in the description | 5,448 | 2,854 |
| describe_prefab | rotations, small cells, ports | the prefab; small cells counted, listed with include_small_cells (1.28.3) | 5,852 | 5,132 |
| dish_aim | none | | 411 | 411 |
| feed_paths | every device on the network, rooms | devices 15, unreached 10 (x-default-limits) | 35,047 | 6,907 |
| find_items | a page of items with their holders; how_to_get per stock entry | page 10 (was 100); how_to_get in the description; prefab and an area narrow it | 61,306 | 6,316 |
| find_spot | spots, reasons | limit 5, reasons 8 | 2,833 | 2,833 |
| find_things | a page of things | page 8 (was 100); prefab and an area (min/max, near/radius_m) narrow it | 94,020 | 7,724 |
| game_clock | none | | 84 | 84 |
| get_ic_source | the source | exempt: the source is the reply; output_file | 60 KB | 60 KB |
| get_ic_status | the source; an IC10 runtime on a Lua chip; the Lua log | source with include_source; runtime null for Lua; log_lines 5 (was 20) | about 66,000 | 6,059 |
| grid_survey | cells, every piece and device in them | exempt; page 8 cells (was 27), pieces 40, devices 20, kinds filter | 332,757 | 47,361 |
| highlight | targets asked; fixed notes | the request; notes in the description | 1,219 | 1,174 |
| ignition_risk | carried burnable items | items 12 (x-default-limits) | 9,389 | 5,773 |
| inspect_slots | a device's slots, each with its logic values | slots 5 (x-default-limits); slot_index | 17,454 | 7,486 |
| item_totals | item types, each with its top holders; a note | 12 types (was 200), 1 holder (was 5) | 230,775 | 5,497 |
| label, move_item, paint, plant_genes write | one result per item asked | the request | 2,556 | 2,556 |
| landing_pads | pads, ships, contacts | the world's pads | 6,390 | 6,390 |
| lint_layout | findings | limit 25 (was 100) | 25,929 | 6,804 |
| lint_rules | rules, each with its file | file and expressions with full or rule_id | 9,583 | 7,363 |
| list_containers | a page of containers; a note | page 12 (was 100) | 60,175 | 7,387 |
| list_devices | every device | devices 25 (x-default-limits) | 38,760 | 6,510 |
| list_gateways | gateways | the world's | 979 | 979 |
| looking_at | player, interactable, view, the full hit, a structure's body | target and a brief hit; the rest with include | 2,655 | 858 |
| mod_info | methods, reflected members, runtime per method | methods 12, reflection 10 (x-default-limits), runtime methods 10 | 35,526 | 7,316 |
| move_gas, sample_logic | the request's atmospheres; the samples asked | exempt: built from JSON trees, sized by the request | | |
| network_snapshot | devices, each with every readable value | max_devices 2 (was 256) | 407,976 | 6,894 |
| outer_frames | a page of frames | page 20 (was 200) | 66,263 | 6,863 |
| place_cables, place_pipes, place_chutes, remove_cables, remove_pipes, remove_chutes, plan_removal | cells, chute riding items, a job's placed pieces | limit 5 (was 200); items with include_network_devices; poll log counts | 121,895 | 6,915 |
| plan_cable_route, plan_pipe_route, plan_chute_route | the dry run's cells | as place_*; summary: the dry run in short, 1,544 (1.28.3) | 122,727 | 7,747 |
| place_structure, remove_structure | layouts on a refusal; fixed notes | layouts on a refusal with verbose; notes with include_notes | 3,941 | 3,839 |
| planet | gases, reservoirs | the planet | 5,160 | 5,160 |
| plant_genes | 19 genes per plant, each with its meaning and four fixed range numbers | meaning with include_notes; range numbers in the description | 57,116 | 7,488 |
| plants | every plant in full | in short unless verbose or reference_id; 15 plants (x-default-limits) | 222,234 | 6,750 |
| player_vitals | a fixed note | note in the description | 1,742 | 1,289 |
| read_console | lines | 40 (was 50) | 8,223 | 6,593 |
| read_devices, read_logic_many, write_logic_many | one result per item asked | the request | 2,205 | 2,205 |
| read_logic, write_logic, read_memory, write_memory, reagents | none worth bounding | | 1,523 | 1,523 |
| replace_walls, replace_frames | pieces with their faces; notes | limit 5 (was 200); notes with include_notes | 199,378 | 7,201 |
| resolve_ic_selectors | a selector per device on the data network; a note | 12 (x-default-limits); note in the description | 35,176 | 6,393 |
| rocket_flight_log | rows | 12 (was 20) | 10,988 | 7,156 |
| rocket_forecast | legs, profiles; the model's fixed rules in assumptions | the route; fixed rules with explain | 5,233 | 5,210 |
| rocket_mining_options | every site; fixed notes | sites 3 (x-default-limits); fixed notes with explain | 58,160 | 7,431 |
| rocket_status | every rocket in full; fixed explanations | exempt, left to the rocket tools (compact, rocket_id); explanations with explain | 28,933 | 28,933 |
| rooms | every room with its devices | devices with include_devices (default was true); rooms 10 (x-default-limits) | 71,068 | 7,598 |
| run_console_command | output lines | max_output_lines 100 | 873 | 873 |
| set_ic_pins, set_uplink | pins, choices | fixed | 1,686 | 1,686 |
| set_ic_source | the source echoed back | source with include_source; source_file | about 61,000 | 981 |
| show_preview | the dry run; a fixed legend | as place_structure; legend in the description | 4,123 | 3,999 |
| silo_deposit, silo_withdraw | one result per thing asked; the entries a withdrawal takes and where they went | the request | | 2,137 |
| solar_aim, trader_buy, trader_sell | the request | | 1,823 | 1,823 |
| undo_job | the removal's and the placement's dry runs | as remove_structure and place_structure | 7,122 | 7,122 |
| thing_health | a scan page or a network's pieces | page 8 (was 200) | 172,677 | 7,173 |
| trader_contacts | contacts, dishes | the sky's | 2,773 | 2,773 |
| trader_inventory | contacts, each with what it buys and sells | contacts 2 (x-default-limits); contact_id | 16,898 | 5,642 |
| vault_contents, vault_deposit, vault_withdraw | a vault's stock | the vault | 8,144 | 8,144 |
| wall_map | a 17 x 17 map | radius 4 | 4,460 | 4,460 |
| water_sources | every water source; a note | 15 (x-default-limits); note in the description | 25,861 | 6,643 |
| weather | events | the world's | 2,543 | 2,543 |

vault_contents measures 8,144 bytes with two vaults of 20 kinds each, under the budget by a little; batch replies
are measured at eight items (two plants for plant_genes, which answers every gene of each).
