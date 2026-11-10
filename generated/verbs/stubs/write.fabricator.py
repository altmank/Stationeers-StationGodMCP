def write(what: Literal['fabricator'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str,  # One fabricator's reference id (Autolathe, Tool Manufactory, Electronics Printer, Pipe Bender...).
    prefab: str | int = None,  # The item to make: a whole prefab name or hash (ItemMKIIWrench). Needed unless make is false.
    load: list = None,  # Ingots added to the fabricator's stock first, as its import would: [{prefab, quantity}], quantity in ingot units.
    make: bool = True,  # false: only load; nothing is made.
) -> dict:  # main list: r['made']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Make a recipe's item on a fabricator at once (time and energy skipped), optionally loading ingots first: replies with the item made, the stock before and after, and what was consumed.
    A cheat: the item is made at once; the recipe's time and energy are skipped, and no power is drawn. The reply's `skipped` says how much.
    Uses the fabricator's own recipes, tier and build-state multipliers, and the game's own make (stock subtracted, item created in the output slot). The game then exports the item as usual; find it later by id or `find thing {made_by}`.
    Refused, with nothing loaded or made, when: the fabricator has no such recipe or its tier is too low; it is busy (making, open, or its output slot taken); it is off, unpowered, in error or unfinished; or its stock (after load) lacks an ingredient.
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Reply:
      ready: bool
      fabricator: Thing
      tier: str
      recipe: {prefab_name: str, display_name: str | None, tier: str, time_s: float, energy_j: float, inputs: dict[str, float]} | None
      multipliers: {time: float, energy: float, material: float, output: int}
      loaded: list[{prefab_name: str, reagent: str, quantity: float}]
      stock_before: dict[str, float]
      stock_after: dict[str, float]
      consumed: dict[str, float]
      missing: dict[str, float]
      made: list[{reference_id: str, prefab_name: str, display_name: str | None, quantity: float, slot: int}]
      skipped: {time_s: float, energy_j: float} | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.write('fabricator', id='5200100', prefab='ItemMKIIWrench', load=[{'prefab': 'ItemIronIngot', 'quantity': 3}, {'prefab': 'ItemElectrumIngot', 'quantity': 3}])
    """
