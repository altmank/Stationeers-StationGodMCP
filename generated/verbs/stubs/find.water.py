def find(what: Literal['water'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    min_mol: float = 1,  # Skip sources holding less water, polluted water and steam together.
) -> dict:  # main list: r['sources']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Canisters, tanks, devices and pipe networks holding water or steam, largest first.
    Reply:
      sources: list[{kind: str, owner: Any, atmosphere_id: str, volume_l: float, pressure_kpa: float, temperature_k: float, water: {liquid_mol: float,
          liquid_l: float, hydration: float, polluted_mol: float, polluted_l: float, steam_mol: float, steam_if_condensed_l: float}}]
      count: int
      total: int
      offset: int
      has_more: bool
      totals: {liquid_mol: float, liquid_l: float, hydration: float, polluted_mol: float, polluted_l: float, steam_mol: float, steam_if_condensed_l:
          float}
      min_mol: float
      as_of: float
      local_player: {reference_id: str, display_name: str | None, position: Vec3} | None
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.find('water', min_mol=50)
    """
