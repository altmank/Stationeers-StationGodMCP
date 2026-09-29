# Solar, dishes and traders

[Back to the README](../README.md)

Exact aiming for solar panels and satellite dishes, landing pad checks, and trading with a landed trader, all worked
out with the game's own geometry and checks. No gateway is needed.

## Tools

| Tool | What it does | Main arguments |
| --- | --- | --- |
| `solar_aim` | The Horizontal and Vertical that point a solar panel straight at the sun. Read only. | `reference_id` |
| `dish_aim` | The Horizontal and Vertical that point a satellite dish at a trader contact. Read only. | `dish_id`, `contact_id` |
| `trader_contacts` | Every trader in the sky: type, shuttle, pad size it needs, direction, power needed to resolve and contact, time left; and every dish with where it points. | none |
| `trader_inventory` | What each trader buys and sells, at what price and under what conditions, and how many of each item you have. | `contact_id` (omit for all) |
| `landing_pads` | Every landing pad, measured by the game's own checks, and which traders fit and can land now. | none |
| `trader_buy` | Buy from the landed trader, as the trade window's Buy button does. | `reference_id`, `items: [{name or prefab_name, quantity}]`, `credit_card_id`, `dry_run` |
| `trader_sell` | Sell to the landed trader, as the Sell button does. | as `trader_buy` |

## Aiming

`solar_aim` works from the panel's own pivots and the game's sun vector: no daylight sensor, no calibration. It returns
`horizontal` and `vertical` in logic degrees, ready for `write_logic`, the angle still off when the sun is outside the
panel's tilt range, and the panel's aim now. Every pose has a twin that faces the same way (horizontal + 180,
vertical 180 - vertical); when both reach the sun it answers the one nearer the panel's current aim, so a tracker that
writes every answer never swings the panel round. With the sun below the horizon it gives the pose closest to the sun
now: tilted toward where it set until midnight, toward where it will rise after. `alignment_ratio` is the panel's Ratio
at that pose from its facing alone, before shading; with the sun below the horizon the game's Ratio is 0 whatever it
says, since the ground shades the panel. The game stops a turning panel
within its rotation tolerance, up to about 0.4 degrees short of the written Horizontal. A flat panel reports
`can_turn: false`. `operable: false` means the panel is not finished (or is broken): the game generates
nothing from it at any angle. `sun.eclipse` is always false on a dedicated server: the game works out eclipses only in
a game with a screen. A missing id or a thing that is not a solar panel is refused with `not_solar_panel`.

`dish_aim` tries angles on the dish's own model and puts it back within one frame, so nothing moves. It works on the
Medium and the Small Satellite Dish (the Small one turns its pivots without an animator). Under 2 degrees of
`error_deg` the contact gets the dish's whole signal. `finished`, `powered`, `on` and `can_rotate` (all three) say
whether the dish turns now; `trader_contacts` gives the same per dish. Refusals: `thing_not_found` (no dish with that
id), `contact_not_found`, `dish_not_ready` (a dish model that cannot be posed). Write the result with `write_logic`:

```json
{ "writes": [
  { "reference_id": "182200", "logic_type": "Horizontal", "value": 131.4 },
  { "reference_id": "182200", "logic_type": "Vertical", "value": 38.2 } ] }
```

Power the dish and switch it on before writing. A dish that cannot rotate still stores the written angles as its
target but never turns toward them, and the game ignores a write of the target it already holds, so writing the same
angles again once it is powered does nothing: write another value first, then the aim. Panels turn without power.

## Landing pads

`landing_pads` measures each pad as the game does: the largest square it passes, whether its network has exactly one
centre, the runway threshold for planes, and per trader whether it fits, whether something above the pad is in the
way (the game itself does not check this), and whether it can land now with the game's own reason if not. The check
moves the pad's landing point and puts it back, so a landing in progress is never affected.

- Every 2 m cell of the landing square must be a pad tile or the centre. A Data And Power (or other connection)
  piece inside the square is not a tile and shrinks the largest square, so put connection pieces outside it.
- `obstructed` is the game's own check, reported as it answers; the game never uses it, and it may count the pad's
  own pieces, so treat it as advisory.

## Trading

- Only the trader that has landed and is ready at a pad trades (`not_landed` names the landed one).
- **Buying:** the card is charged, the trader's stock drops, and the goods go into the first empty tradable slots of
  the vending machines on the pad's data network, then into the card holder's inventory. Gas goes into the pad
  network's atmosphere.
- **Selling:** the goods come from the pad network's vending machines, then from the card holder's inventory; gas from
  the pad network's atmosphere. The trader must still want the item, and it must meet the trader's conditions (purity,
  moles per unit, temperature). The card must be carried by a player or held by a vending machine; any other card is
  refused with `card_not_usable`, because the game's sell reads the card holder's inventory and fails without one.
- **Selling several lines:** the lines of one call add up. Each is checked against what the trader still wants and what
  is available after the lines before it, in a dry run as in a real run, and the lines of one trader entry are sold
  together. The game takes sold stacks out of their slots only at the end of the frame, so without this a second line
  of iron ore sold the same ore again. Two entries that accept the same goods in one call: the second is refused with
  `sell_separately` when the game would take goods already sold; sell it in a call of its own.
- Prices are the trader's, adjusted for respawn stress as the trade window shows them. Credits in replies are rounded
  to the cent.
- `trader_buy`'s `dry_run: true` checks each line on its own against stock, card and free slots; it does not add lines
  up.
- `trader_inventory` shows, for the landed trader, how many of each item it would take right now (`sellable`), the
  trade window's own count: what the pad network's vending machines and you hold. `trader_sell` counts the card
  holder's inventory in place of yours, so the two agree when the card is yours. `have` counts every item of the line's prefab in the world (0 when none, null for
  gas), without the trader's conditions: every "Box of ..." line counts all cardboard boxes. The game rolls a trader's inventory when the contact appears, so this works before
  the trader is interrogated.
- Host only.
