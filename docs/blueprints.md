# Blueprints

[Back to the README](../README.md)

With [BlueprintMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3672138641) loaded, `paste_blueprint`
pastes one of its blueprints at a position and quarter turn you give, with no player needed. BlueprintMod's console
command `bppaste` takes both from the local player, so it cannot run on a dedicated server; this tool makes the call
the D.B.P.U. makes instead.

BlueprintMod is optional: the mod finds it by name at run time. Without it the tool answers `mod_missing`.

## Three forms

**Paste:**

```json
{ "name": "airlock-3x3", "anchor": [701, 200, 655], "rotation": 90 }
```

- `name`: a file in BlueprintMod's Blueprints folder, with or without `.blueprint`, or an absolute path.
- `anchor` `[x, y, z]`: the world position in metres where the blueprint's reference point lands. That is the grid
  point BlueprintMod snapped the copying player to: x and z odd whole metres, y even. A paste lines up with the grid
  when the anchor is such a point.
- `rotation`: 0 (default), 90, 180 or 270, added to the angle the blueprint was copied at, so every piece stays on the
  grid.

The reply comes at once: `started`, `file`, `entries`, `anchor`, `rotation`, `copy_y_angle` and
`expected_duration_s`. BlueprintMod then places the pieces over 2 to 30 seconds (0.15 s per entry).

**Progress:** `{ "status": true }` reports the last paste this tool started: `active`, `complete`, `cancelled`,
`created`, `failed`, `skipped`, `pasted`, and `other_active` when a paste this tool did not start is running. The counts
stay readable after the paste ends.

**Undo:** `{ "undo": true }` runs BlueprintMod's `bpundo`: it cancels a running paste and removes what it placed, or
removes the last finished paste. Its answer comes back as `message`.

## Good to know

- **Rooms are not worked out again** after a paste or an undo. Run the console command `regeneraterooms`
  (`run_console_command`) before reading `rooms`.
- **Materials:** outside creative, BlueprintMod charges DeanamicMatter from the local player. A dedicated server has
  no local player, so use a creative world there.
- **Paused servers:** a dedicated server with no player connected may hold its world paused. BlueprintMod places pieces
  over game time, so the paste stops part way (the `status` counts stop moving) until the console command
  `pause false`.
- **Refused:** `paste_refused` with BlueprintMod's own message (the same blueprint already pasted at that position and
  turn, not enough DeanamicMatter); `blueprint_failed` (BlueprintMod threw, with its error, such as a file that is not a
  blueprint); `invalid_argument` (no such file, naming the path it looked at; a rotation other than 0, 90, 180 or 270);
  `game_changed` (BlueprintMod no longer has something the tool calls).
- **Multiplayer:** host only. BlueprintMod shows its paste effect to every player and the game sends them the new
  structures.

## Trying designs on a dedicated server

A creative dedicated server is a safe place to try a blueprint before pasting it into a real base: paste, look at the
result with `grid_survey`, `connections` and `rooms` (after `regeneraterooms`), undo, and paste again at another
position or turn. When that server runs on the same machine as your game, give it its own pipe name so the two do not
compete for one pipe; see [install.md](install.md#two-games-on-one-machine).
