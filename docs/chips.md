# Chips: IC10 and Lua

[Back to the README](../README.md)

The chip tools read and write the program on a programmable chip, show what it is doing, pause and step it, and set
an IC Housing's device pins. They work on IC10 chips and, with
[StationeersLua](https://steamcommunity.com/sharedfiles/filedetails/?id=3659911735) loaded, on its Integrated Circuit
(Lua).

## Where a chip can be

`id` names a **holder**, the thing whose slot holds the chip:

- IC10: an IC Housing, a suit or another worn holder (toolbelts, tablets, mining robots, logic I/O devices).
- Lua (StationeersLua): an IC Housing, or a [ScriptedScreens](https://steamcommunity.com/sharedfiles/filedetails/?id=3666779631)
  holder: a Console or Computer board, a tablet cartridge, a Programmable Visor.

You may also pass the Console, Computer or held tablet the holder sits in, or the chip itself. Replies name all three:
`reference_id` (what the call reached), `holder` and `chip`, plus `language` (`ic10` or `lua`) and `source_length`.
A holder in a tablet or visor is reached only while a player wears or holds it.

StationeersLua is optional: the mod finds it by name at run time and works the same without it.

## Tools

| Call | What it does | Main options |
| --- | --- | --- |
| `inspect chip` (`source`) | Read the program. | `id`, `parts: ["source"]` |
| `write chip` (`code`) | Write the program, as the IC editor's export does. | `id`, `code` or `code_file`, `include: ["code"]` |
| `inspect chip` (`status`) | Current line, registers, stack, aliases, defines, jump tags, power and pause state, errors, and pins `d0` to `d5`; for Lua, compile state, last error and print log. | `id`, `start`, `count` (default 64), `log` (default 5) |
| `write chip` (`run`) | Pause, step one instruction, resume (IC10); restart (Lua). | `id`, `run`: `pause`, `step`, `resume`, `restart` |
| `inspect chip` (`selectors`) | What a chip's `db` and `d0`... pins and its aliases point at, and the prefab and name-hash selector (`lbn`, `sbn`) of each device on its data network. | `id`, `targets` |
| `write chip` (`pins`) | Set an IC Housing's pins, as turning its screws would. | `id`, `pins: {d0: "<id>", d3: null}`, `allow: ["off_network"]` |
| `inspect screen` | What a ScriptedScreens screen shows: its surfaces and one surface's elements, each with its box, text and props; a picture where the game draws. | `id`, `surface`, `type`, `text`, `shown_only`, `include: ["style"]`, `include: ["image"]` |
| `write screen` | Touch a control on a ScriptedScreens screen as a player does: a click, a change or a toggle. | `id`, `element` or `pixel: [x, y]`, `event`, `text`, `as_player` |

## IC10

- `write chip` with `code` compiles the program at once and restarts it at line 0. A paused chip stays paused; registers and the
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
- `write chip` with `run: "pause"` holds the chip; `run: "step"` runs exactly one instruction and stays paused (a
  running chip is paused first); `run: "resume"` lets it run. `step` is refused, with nothing changed, while the
  program has a compile error
  (`ic_compile_error`) or the holder is off or unpowered (`ic_not_operable`). Pausing holds IC Housings and suits only:
  other holders report paused but keep running. A pause lasts until `resume` or until the world is left: loading a
  save, starting a new game or going to the menu lets every paused chip run again (1.9.1+).
- `inspect chip`'s `selectors` part lists the devices the chip's batch instructions (`lb`, `lbn`, `sb`, `sbn`)
  reach: those on the
  holder's data network (`batch_device_count`; null when it has none). A selector is `unique` when exactly one device
  on that network has its prefab and name hash. A device named in `targets` that is off the network is
  listed with `reachable: false` and no `ic10_example`: the chip cannot read it, and an `lbn` with its pair reads the
  reachable devices that share the pair instead, or none. `collision_count` counts reachable devices only, so for an
  unreachable device it is how many other devices that pair would read. An id that is not a device is
  `device_not_found`.
- A register or stack value that is not finite reads as a string: `"NaN"`, `"Infinity"`, `"-Infinity"`.
- `inspect chip`'s `status` part shows the stack as a window: `start` and `count` choose it. The program is left out
  (`source` null, `source_length` still its length) unless `parts: ["status", "source"]` is given, so polling a chip's
  log or state never reads a long Lua source; the `source` part reads the program alone (with `output_file` for a long
  one). The same goes for `write chip`'s reply: it echoes the stored program only with `include: ["code"]`.
- `code_file` (MCP only) instead of `code`: an absolute path, on the machine the MCP server runs on, to a UTF-8
  text file whose text is written as the source. A 60 KB Lua hub then never passes through the agent's context.
- `pins` lists only pins with a device or an alias (each keeps its `name`, `d0`...); `inspect chip`'s `selectors`
  part only pins with a device.
- A holder with no chip: `inspect chip`'s `status` part answers only `has_chip: false`, the holder and its pins (no
  `housing`, power or runtime fields); the other parts refuse with `no_programmable_chip`, `selectors` too. Arguments
  are checked before the chip, so a bad `run` or `code`, or a malformed id in `targets`, is
  `invalid_argument` on any holder; an unknown or repeated id is looked up only after the holder and its chip.

Load a program and check it, `write chip` then `inspect chip`:

```json
{ "id": "151020", "code": "alias sensor d0\nloop:\nl r0 sensor Temperature\nyield\nj loop" }
```

## Lua chips

- `write chip` writes Lua the way the IC editor's export does. There is no IC10 line or byte limit; the tool takes up
  to 262144 characters (`source_too_large` above that).
- `inspect chip`'s `runtime` (registers, stack, aliases, defines, jump tags) is IC10's; for a Lua chip it is null.
- StationeersLua compiles on a worker thread, so the reply usually shows `lua.compiling: true`. Call `inspect chip`
  until it is false, then read `lua.running` and `lua.last_error`. No separate restart is needed.
- `lua` in the replies: `compiling`, `has_runtime`, `init_complete` (module code done, `tick(dt)` running), `running`,
  `library` (a `--@module` chip other chips require), `source_version`, `last_error` (`kind` compile or runtime,
  `line`, `message`, `traceback`) and, in `inspect chip`, the last `log` lines of the chip's `print()` log.
- `write chip` with `run: "restart"` compiles the chip's source again and runs it from the start, clearing a latched
  error. Lua chips cannot be paused or stepped (`lua_chip_unsupported`).
- `housing.kind` in `inspect chip` says what holds the chip, and `operable` whether it runs now: a board runs while its
  computer is on, powered and built; a cartridge while its tablet is on and powered; a suit or visor with a charged
  battery.
- A holder that is off or unpowered compiles the new source when it runs again.

## Screens (ScriptedScreens)

A Lua chip on a ScriptedScreens board, cartridge or visor draws its screen from a model the host keeps: every element
the script has built, with its type, its place and its props. `inspect screen {id}` reads that model, so it
works on a dedicated server too. It lists the surfaces (`shown_on` says which screen shows each) and the elements of one
(default: the one the main screen shows), each with `box {x, y, w, h}` in surface pixels from the top-left corner,
`shown`, `touch` (a control a press acts on) and `props` (`text`, `value`, `z_index`...). `text` and `type`
narrow the list. A vector element's motion is drawn by each player's game; its props hold the scene, not the frame.

`image: true` adds ScriptedScreens' own PNG of the drawn surface. Only a game that draws can give one: on a dedicated
server the answer is `not_rendered`; read the elements instead.

`write screen` sends a touch into the host's input path, as a player's touch arrives: `click` (default), `change` with
`text`, or `toggle`, on `element`, or on the shown control at `pixel: [x, y]` (the highest `z_index` there; scroll
positions are not applied). The script's handler runs on its next tick and sees `player` (default your name) as the one
who touched it, so read the screen again about half a second later to see what it did.

## Pins

`write chip` with `pins` sets any of `d0` to `d5` to a device's reference id, or `null` to clear it; pins left out
keep their device. When the housing is on a data network, the device must be on that network too, because the chip
only reaches a pin's device there; `allow: ["off_network"]` stores it anyway. Every pin is checked before any is written, so a
refused call changes nothing. A running chip uses the new devices from its next instruction; nothing is recompiled.
Rocket IC Housings work too; suits and other worn holders are refused.

Refusal codes: `not_ic_housing` (not an IC Housing, e.g. a suit), `device_not_found` (the housing or a pin's device is
outside the scope), `thing_not_found` (nothing has a pin's id), `not_logic_device` (a pin's id is a frame, a pipe or
another non-device), `logic_not_readable` (a device with no readable logic), `not_on_data_network` (off the housing's
data network without `allow: ["off_network"]`), `invalid_argument` (a bad `pins` object, pin name or id, a pin listed
twice, the housing itself).

## Who writes a device

A device written by a chip is written back on the chip's next pass. `write logic` says so in
`driven_by` when a chip on the device's data network writes the same logic type to it, and `inspect controllers`
lists every controller of a device (`help {verb: "inspect", what: "controllers"}`). A chip counts when its code
writes the device through a pin (`s d0 On 1`, also through `alias`), `db`, its
id (`sd`), or a batch to its prefab hash (`sb`, and `sbn` with its name), with defines and `HASH("...")` resolved; a
Lua chip through `ic.write`, `ic.write_id` or `batch_write` with constant arguments. A device register (`dr0`) or
anything held in a register is not followed. Each program is parsed once and kept until its source changes.

## Multiplayer

Writes happen on the host. `write chip` sends the chip's new source and pins to the other players, through the game's
own sync.
