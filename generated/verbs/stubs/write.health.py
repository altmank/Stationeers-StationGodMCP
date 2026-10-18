def write(what: Literal['health'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    player: str = None,  # The player to heal, by name or id, in the game or not; default the player.
) -> dict:  # main list: r['parts']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Heal a player: every kind of damage to the body and its organs (brain and lungs) set to none.
    A cheat: no item or machine heals all of it at once. Heals as the game's own healers do (a cryo tube, a medical effect): the body's and each organ's damage set to none, so the brain's damage goes too. The report gives each part's damage now. A dead player is refused: a heal does not revive. Hunger and thirst are not damage and stay as they are (inspect player).
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Reply:
      player: Thing
      online: bool
      state: str
      parts: list[{part: str, thing: Thing, damage_percent_before: int, damage_percent_after: int, before: {brute: float, burn: float, oxygen: float,
          hydration: float, starvation: float, toxic: float, radiation: float, decay: float, stun: float}, after: {brute: float, burn: float, oxygen:
          float, hydration: float, starvation: float, toxic: float, radiation: float, decay: float, stun: float}}]
      healed: bool
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.write('health', player='xceled')
    """
