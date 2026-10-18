def build(what: Literal['item'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    prefab: str | int,  # The item: a whole prefab name or hash (ItemIntegratedCircuit10, ItemKitConsole).
    quantity: float = 1,  # How many: a count of items, or of units for a stacking item (ore, ingot grams, coils), made in full stacks; at most 64 stacks.
    to: str = None,  # The holder that gets them: a container, device, suit or "player"; or give at.
    slot: int = None,  # A slot index of to; left out, matching stacks are topped up, then empty slots are filled.
    at: list = None,  # [x, y, z] in metres: the items appear there, loose; or give to.
) -> dict:  # main list: r['items']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Make items out of nothing (chips, boards, kits, ingots): by prefab and quantity, into a holder's slots or on the ground at a point.
    A cheat, for creative worlds only (not_creative elsewhere): no player makes items from nothing. Works with no player in the world. Into a holder, what its slots cannot take is refused (no_room) and nothing is made. The report lists each stack it would make; placed names the new items' ids.
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    placed: list[str] on a job reply, the new ids (a run's log.placed, a structure job's result.placed; a brief poll counts them in log.placed_count); int on a run report, the cells it would build.
    Reply:
      placed: list[str]
      prefab_name: str
      display_name: str | None
      quantity: float
      to: Thing | None
      at: Vec3 | None
      items: list[{where: str, slot: {id: str, slot: int} | None, quantity: float, reference_id: str | None}]
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.build('item', prefab='ItemIntegratedCircuit10', quantity=2, to='5200100')
    """
