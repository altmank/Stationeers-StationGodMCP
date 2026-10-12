def survey(what: Literal['feeds'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # The root device (an APC output, a generator).
    network: str | dict = None,  # A network: its id, a piece or device on it, or {id, port}.
    port: int = None,  # Or the root's port whose network to trace.
    kind: Literal['cable', 'pipe', 'chute'] = 'cable',  # The network's kind when root is a device on several.
) -> dict:  # main list: r['devices']; iterating or indexing it walks every page, in order
    """How a network feeds its devices from a root device: pieces and rooms each feed passes, daisy chains, unreached devices.
    Reply:
      root: Thing
      network_id: str
      kind: str
      root_room_id: str | None
      daisy_chains: int
      multiple_feeds: int
      devices: list[{reference_id: str, prefab_name: str | None, display_name: str | None, room_id: str | None, pieces: int, rooms: list[str],
          through: list[str]}]
      rooms: list[{room_id: str, devices: int, entries: list[str], entry_at: list[Vec3]}]
      unreached: list[Thing]
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.survey('feeds', id='720259')
    A list of ids answers {results: [{index, ok, reference_id, ...} or {index, ok, reference_id, error}], count, success_count, error_count}.
    """
