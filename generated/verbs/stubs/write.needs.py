def write(what: Literal['needs'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    player: str = None,  # The player, by name or id, in the game or not; default the player.
    nutrition: float = None,  # Share of the body's nutrition storage (50 for a human).
    hydration: float = None,  # Share of 5, the game's hydration ratio; 1.75 is its cap.
    waste: float = None,  # The stomach's waste ratio.
) -> dict:
    """Set a player's nutrition, hydration and waste, as the console's player setnutrition does, and read them back.
    A cheat. Give at least one of nutrition, hydration, waste (missing_target names them); one left out stays. Every check runs before anything is set, so a refused call changes nothing. Waste is refused sanitation_off while Sanitation is off, and the whole call no_organ for a body with no stomach. The reply reads the values back (waste after the next atmospherics tick, at most 2 s): each as a share before and after. warnings: wets_next_tick (waste 1 wets the suit at the next life tick), drains (the values fall from the next life tick at inspect player's rates).
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Reply:
      player: Thing
      nutrition: {before: float, after: float, capacity: float}
      hydration: {before: float, after: float, capacity: float}
      waste: {before: float, after: float} | None
      sanitation: bool
      warnings: list[{code: str, message: str}]
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.write('needs', nutrition=0.5, hydration=0.5, waste=0.5)
    """
