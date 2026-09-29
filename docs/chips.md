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
| `set_ic_source` | Write the program, as the IC editor's export does. | `reference_id`, `source` |
| `get_ic_status` | Current line, registers, stack, aliases, defines, jump tags, power and pause state, errors, and pins `d0` to `d5`; for Lua, compile state, last error and print log. | `reference_id`, `stack_start`, `stack_count` (default 64), `log_lines` (default 20) |
| `control_ic_execution` | Pause, step one instruction, resume (IC10); restart (Lua). | `reference_id`, `action`: `pause`, `step`, `resume`, `restart` |
| `resolve_ic_selectors` | What a chip's `db` and `d0`... pins and its aliases point at, and a unique prefab or name-hash selector for each device it can see. | `reference_id`, `target_reference_ids` |
| `set_ic_pins` | Set an IC Housing's pins, as turning its screws would. | `reference_id`, `pins: {d0: "<id>", d3: null}`, `allow_off_network` |

## IC10

- `set_ic_source` compiles the program at once and restarts it at line 0. Compile errors show in `get_ic_status`.
- `control_ic_execution` `pause` holds the chip; `step` runs exactly one instruction and stays paused; `resume` lets it
  run. Pausing holds IC Housings and suits only: other holders report paused but keep running.
- `get_ic_status` shows the stack as a window: `stack_start` and `stack_count` choose it.
- A holder with no chip: `get_ic_status` answers only `has_chip: false`, the holder and its pins (no `housing`, power
  or runtime fields); the other tools refuse with `no_programmable_chip`, `resolve_ic_selectors` too. Arguments are
  checked before the chip, so a bad `action`, `source` or `target_reference_ids` is `invalid_argument` on any holder.

Load a program and check it, `set_ic_source` then `get_ic_status`:

```json
{ "reference_id": "151020", "source": "alias sensor d0\nloop:\nl r0 sensor Temperature\nyield\nj loop" }
```

## Lua chips

- `set_ic_source` writes Lua the way the IC editor's export does. There is no IC10 line or byte limit; the tool takes up
  to 262144 characters (`source_too_large` above that).
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
