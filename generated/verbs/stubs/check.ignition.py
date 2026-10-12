def check(what: Literal['ignition'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # A tank, canister or pipe network: its contents' autoignition margin; left out, the player's carried items.
    include: list[Literal['prefabs']] = None,  # prefabs: every prefab's ignition temperatures.
) -> dict:  # main list: r['items']; iterating or indexing it walks every page, in order
    """Whether carried items, or a tank or network by id, would catch fire in the air around them.
    Reply:
      player: {reference_id: str, display_name: str | None, position: Vec3} | None
      cell: {temperature_k: float, pressure_kpa: float, energy_j: float, inflamed: bool, oxygen_mol: float, in_room: bool} | None
      items: list[{reference_id: str, prefab_name: str | None, display_name: str | None, slot: str | None, in_hand: bool, holder: Thing | None,
          atmosphere: str, burning: bool, hidden: bool, flashpoint_k: float | None, flashpoint_effective_k: float | None, autoignition_k: float |
          None, ignites_now: bool, health_ratio: float | None}]
      prefabs: list[{prefab_name: str | None, display_name: str | None, flashpoint_k: float | None, autoignition_k: float | None}] | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.check('ignition', id='2005300')
    A list of ids answers {results: [{index, ok, reference_id, ...} or {index, ok, reference_id, error}], count, success_count, error_count}.
    """
