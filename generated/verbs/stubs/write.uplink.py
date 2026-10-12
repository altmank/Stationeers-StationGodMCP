def write(what: Literal['uplink'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str,  # The uplink.
    downlink: str,  # The downlink to follow.
    gateway: str = 'world',  # "world" (default) or a gateway id: only devices on its data networks.
) -> dict:
    """Point a Logic Rocket Uplink at a Logic Rocket Downlink.
    One id only: a list is refused (x-list one: this write cannot be undone).
    Reply:
      uplink: Thing
      previous_downlink: {reference_id: str, prefab_name: str | None, display_name: str | None, rocket: {network_id: str, name: str, state: str,
          dry_mass_kg: float, hull_pieces: int, internals: int} | None} | None
      link: {downlink: {reference_id: str, prefab_name: str | None, display_name: str | None, rocket: {network_id: str, name: str, state: str,
          dry_mass_kg: float, hull_pieces: int, internals: int} | None} | None, connected: bool, choices: list[{reference_id: str, prefab_name: str |
          None, display_name: str | None, rocket: {network_id: str, name: str, state: str, dry_mass_kg: float, hull_pieces: int, internals: int} |
          None}]}
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}
    Example: sg.write('uplink', id='5100200', downlink='5100377')
    """
