# Chips: IC10 and Lua

[Back to the README](../README.md)

The chip tools read and write the program on a programmable chip, show what it is doing, pause and step it, and set
an IC Housing's device pins. They work on IC10 chips and, with
[StationeersLua](https://steamcommunity.com/sharedfiles/filedetails/?id=3659911735) loaded, on its Integrated Circuit
(Lua).

## Where a chip can be

`reference_id` names a **holder**, the thing whose slot holds the chip:

- IC10: an IC Housing, a suit or another worn holder (toolbelts, tablets, mining robots, logic I/O devices).
- Lua (StationeersLua): an IC Housing, or a [ScriptedScreens](https://steamcommunity.com/sharedfiles/filedetails/?id=3666779631)
  holder: a Console or Computer board, a tablet cartridge, a Programmable Visor.

You may also pass the Console, Computer or held tablet the holder sits in, or the chip itself. Replies name all three:
`reference_id` (what the call reached), `holder` and `chip`, plus `language` (`ic10` or `lua`) and `source_length`.
A holder in a tablet or visor is reached only while a player wears or holds it.

StationeersLua is optional: the mod finds it by name at run time and works the same without it.

## Tools

| Tool | What it does | Main arguments |
| --- | --- | --- |
| `get_ic_source` | Read the program. | `reference_id` |
| `set_ic_source` | Write the program, as the IC editor's export does. | `reference_id`, `source` or `source_file`, `include_source` |
| `get_ic_status` | Current line, registers, stack, aliases, defines, jump tags, power and pause state, errors, and pins `d0` to `d5`; for Lua, compile state, last error and print log. | `reference_id`, `stack_start`, `stack_count` (default 64), `log_lines` (default 20) |
| `control_ic_execution` | Pause, step one instruction, resume (IC10); restart (Lua). | `reference_id`, `action`: `pause`, `step`, `resume`, `restart` |
| `resolve_ic_selectors` | What a chip's `db` and `d0`... pins and its aliases point at, and the prefab and name-hash selector (`lbn`, `sbn`) of each device on its data network. | `reference_id`, `target_reference_ids` |
| `set_ic_pins` | Set an IC Housing's pins, as turning its screws would. | `reference_id`, `pins: {d0: "<id>", d3: null}`, `allow_off_network` |

## IC10

- `set_ic_source` compiles the program at once and restarts it at line 0. A paused chip stays paused; registers and the
  stack are kept (`sp` goes back to 0). A compile error sets `compilation_error`, `compile_error_line` (0-based) and
  `compile_error_type`; `error_line` and `error_type` are the runtime error's. `error_line` is the game's own text, a
  string such as `"0"`, while `compile_error_line` is an integer; both are kept as they are so existing readers do not
  break. `error_code` is the game's error text as plain text (the game colours it with rich-text tags; they are
  removed).
- The chip stores its program as ASCII and runs what it stores: CRLF and lone CR line ends become LF (the chip splits
  lines on LF only, so a CR-only program would be one line) and each non-ASCII character `?`. The reply's `warnings`
  say so (`crlf_normalised`, `cr_normalised`, `non_ascii_replaced`).
- The chip runs a program of any length, but the in-game editor holds 128 lines of up to 90 characters and 4096
  characters in all, and cuts a longer program when a player opens and submits it. The program is still written;
  `warnings` has `over_editor_lines`, `over_editor_line_length` or `over_editor_size`. `over_editor_line_length` names
  every long line (0-based); past 10 it gives their count and the first 10.
- `control_ic_execution` `pause` holds the chip; `step` runs exactly one instruction and stays paused (a running chip is
  paused first); `resume` lets it run. `step` is refused, with nothing changed, while the program has a compile error
  (`ic_compile_error`) or the holder is off or unpowered (`ic_not_operable`). Pausing holds IC Housings and suits only:
  other holders report paused but keep running. A pause lasts until `resume` or until the world is left: loading a
  save, starting a new game or going to the menu lets every paused chip run again (1.9.1+).
- `resolve_ic_selectors` lists the devices the chip's batch instructions (`lb`, `lbn`, `sb`, `sbn`) reach: those on the
  holder's data network (`batch_device_count`; null when it has none). A selector is `unique` when exactly one device
  on that network has its prefab and name hash. A device named in `target_reference_ids` that is off the network is
  listed with `reachable: false` and no `ic10_example`: the chip cannot read it, and an `lbn` with its pair reads the
  reachable devices that share the pair instead, or none. `collision_count` counts reachable devices only, so for an
  unreachable device it is how many other devices that pair would read. An id that is not a device is
  `device_not_found`.
- A register or stack value that is not finite reads as a string: `"NaN"`, `"Infinity"`, `"-Infinity"`.
- `get_ic_status` shows the stack as a window: `stack_start` and `stack_count` choose it. The program is left out
  (`source` null, `source_length` still its length) unless `include_source: true`, so polling a chip's log or state
  never reads a long Lua source; `get_ic_source` reads the program alone (with `output_file` for a long one). The same
  goes for `set_ic_source`'s reply: it echoes the stored program only with `include_source: true`.
- `source_file` (MCP only) instead of `source`: an absolute path, on the machine the MCP server runs on, to a UTF-8
  text file whose text is written as the source. A 60 KB Lua hub then never passes through the agent's context.
- A holder with no chip: `get_ic_status` answers only `has_chip: false`, the holder and its pins (no `housing`, power
  or runtime fields); the other tools refuse with `no_programmable_chip`, `resolve_ic_selectors` too. Arguments are
  checked before the chip, so a bad `action` or `source`, or a malformed id in `target_reference_ids`, is
  `invalid_argument` on any holder; an unknown or repeated id is looked up only after the holder and its chip.

Load a program and check it, `set_ic_source` then `get_ic_status`:

```json
{ "reference_id": "151020", "source": "alias sensor d0\nloop:\nl r0 sensor Temperature\nyield\nj loop" }
```

## Lua chips

- `set_ic_source` writes Lua the way the IC editor's export does. There is no IC10 line or byte limit; the tool takes up
  to 262144 characters (`source_too_large` above that).
- `get_ic_status`'s `runtime` (registers, stack, aliases, defines, jump tags) is IC10's; for a Lua chip it is null.
- StationeersLua compiles on a worker thread, so the reply usually shows `lua.compiling: true`. Call `get_ic_status`
  until it is false, then read `lua.running` and `lua.last_error`. No separate restart is needed.
- `lua` in the replies: `compiling`, `has_runtime`, `init_complete` (module code done, `tick(dt)` running), `running`,
  `library` (a `--@module` chip other chips require), `source_version`, `last_error` (`kind` compile or runtime,
  `line`, `message`, `traceback`) and, in `get_ic_status`, the last `log_lines` lines of the chip's `print()` log.
- `control_ic_execution` `restart` compiles the chip's source again and runs it from the start, clearing a latched
  error. Lua chips cannot be paused or stepped (`lua_chip_unsupported`).
- `housing.kind` in `get_ic_status` says what holds the chip, and `operable` whether it runs now: a board runs while its
  computer is on, powered and built; a cartridge while its tablet is on and powered; a suit or visor with a charged
  battery.
- A holder that is off or unpowered compiles the new source when it runs again.

## Pins

`set_ic_pins` sets any of `d0` to `d5` to a device's reference id, or `null` to clear it; pins left out keep their
device. When the housing is on a data network, the device must be on that network too, because the chip only reaches
a pin's device there; `allow_off_network: true` stores it anyway. Every pin is checked before any is written, so a
refused call changes nothing. A running chip uses the new devices from its next instruction; nothing is recompiled.
Rocket IC Housings work too; suits and other worn holders are refused.

Refusal codes: `not_ic_housing` (not an IC Housing, e.g. a suit), `device_not_found` (the housing or a pin's device is
outside the scope), `thing_not_found` (nothing has a pin's id), `not_logic_device` (a pin's id is a frame, a pipe or
another non-device), `logic_not_readable` (a device with no readable logic), `not_on_data_network` (off the housing's
data network without `allow_off_network`), `invalid_argument` (a bad `pins` object, pin name or id, a pin listed
twice, the housing itself).

## Multiplayer

Writes happen on the host. `set_ic_source` sends the chip to the other players and `set_ic_pins` sends the new pins,
through the game's own sync.
