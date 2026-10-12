def inspect(what: Literal['atmosphere'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # A thing, a pipe or landing pad network, or an atmosphere id.
) -> dict:  # main list: r['atmospheres']; iterating or indexing it walks every page, in order
    """The gas or liquid one thing holds: canister, tank, a rocket's tanks (rocket id or name), suit, pipe or pad network, a device's networks.
    A list of ids answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply below; fields name its keys.
    Reply:
      reference_id: str
      subject: Any
      atmospheres: list[{source: str, owner: Any, slot_index: int | None, slot_name: str | None, atmosphere: {reference_id: str, volume_l: float,
          pressure_kpa: float, temperature_k: float, total_mol: float, liquid_volume_l: float, contents: list[{gas: str, display_name: str | None,
          state: str, amount_mol: float, liquid_l: float | None}], water: {liquid_mol: float, liquid_l: float, hydration: float, polluted_mol: float,
          polluted_l: float, steam_mol: float, steam_if_condensed_l: float}} | None}]
      count: int
    Example: sg.inspect('atmosphere', id='2005300')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
