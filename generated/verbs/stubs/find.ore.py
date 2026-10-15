def find(what: Literal['ore'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    near: list = None,  # The point to report and search from; default the player's position.
    ores: list[str] = None,  # Reagents a spot must all hold (["Cobalt"]).
    radius: float = 500,  # Search radius.
    separation: float = 100,  # Least distance between two spots.
    step: float = None,  # Sample spacing; default the finest within the budget.
    limit: int = 5,  # The first n in order (spots returned, nearest first); left out, every one.
) -> dict:  # main list: r['spots']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Deep miner spots: the ore profile at a point, and the nearest spots holding given ores.
    Reply:
      at: {position: Vec3, regions: list[{set: str, region: str, name: str | None}], profile_id: str | None}
      profiles: list[{id: str, reagents: list[{reagent: str, grams: float}], quantity: list[int], time_s: list[int], regions: list[str]}]
      search: {ores: list[str], profiles: list[str], radius_m: float, step_m: float, min_separation_m: float, samples: int, matched: int,
          searched_radius_m: float, truncated: bool} | None
      spots: list[{at: Vec3, distance_m: float, bearing_deg: float, compass: str, profile_id: str, regions: list[{set: str, region: str, name: str |
          None}], terrain: {ground_y: float | None, cell_volume_l: float | None, terrain_ok: bool | None, note: str}}] | None
      beacons: list[{at: list[float], label: str}] | None
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.find('ore', ores=['Cobalt'])
    """
