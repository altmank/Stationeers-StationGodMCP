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
panel's tilt range, and the panel's aim now. With the sun below the horizon it gives the pose closest to it. A flat
panel reports `can_turn: false`.

`dish_aim` tries angles on the dish's own model and puts it back within one frame, so nothing moves. Under 2 degrees
of `error_deg` the contact gets the dish's whole signal. Write the result with `write_logic`:

```json
{ "writes": [
  { "reference_id": "182200", "logic_type": "Horizontal", "value": 131.4 },
  { "reference_id": "182200", "logic_type": "Vertical", "value": 38.2 } ] }
```

## Landing pads

`landing_pads` measures each pad as the game does: the largest square it passes, whether its network has exactly one
centre, the runway threshold for planes, and per trader whether it fits, whether something above the pad is in the
way (the game itself does not check this), and whether it can land now with the game's own reason if not. The check
moves the pad's landing point and puts it back, so a landing in progress is never affected.

## Trading

- Only the trader that has landed and is ready at a pad trades (`not_landed` names the landed one).
- **Buying:** the card is charged, the trader's stock drops, and the goods go into the first empty tradable slots of
  the vending machines on the pad's data network, then into the card holder's inventory. Gas goes into the pad
  network's atmosphere.
- **Selling:** the goods come from the pad network's vending machines, then from the card holder's inventory; gas from
  the pad network's atmosphere. The trader must still want the item, and it must meet the trader's conditions (purity,
  moles per unit, temperature).
- Prices are the trader's, adjusted for respawn stress as the trade window shows them.
- `dry_run: true` checks each line on its own against stock, card and free slots.
- `trader_inventory` shows, for the landed trader, how many of each item it would take right now (`sellable`), the
  same count `trader_sell` checks. The game rolls a trader's inventory when the contact appears, so this works before
  the trader is interrogated.
- Host only.
