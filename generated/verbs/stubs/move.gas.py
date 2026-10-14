def move(what: Literal['gas'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    source: str | dict = None,  # A thing id, "looking_at", "planet" (with delete), or a room {room} or {room_of}.
    to: str | dict = None,  # A thing id, "looking_at" or a room; left out with delete.
    delete: bool = False,  # Destroy the gas instead of moving it.
    gases: list[str] = None,  # Gas names as inspect atmosphere gives them; left out, every gas and liquid.
    amount_mol: float = None,  # Moles of each gas, capped at what is there; left out, all.
    allow: list[Literal['burst']] = None,  # burst: move past a burst rating.
    joined: bool = True,  # Treat atmospheres the game mixes every tick as one.
) -> dict:
    """Move gas and liquid between atmospheres or rooms, or delete it, with no pipe or pump.
    Applied at the next atmospherics tick. A confirmed move waits for it (max_wait, default 30 s) and answers status applied or failed; past max_wait it answers queued and its id (inspect job).
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Reply:
      transfer_id: str | None
      status: str
      predicted: bool
      joined: bool
      from: {members: list[{owner: {kind: str, reference_id: str, prefab_name: str | None, display_name: str | None}, atmosphere_id: str, before:
          {pressure_kpa: float, temperature_k: float, total_mol: float, gases: list[{gas: str, amount_mol: float}]}, after: {pressure_kpa: float,
          temperature_k: float, total_mol: float, gases: list[{gas: str, amount_mol: float}]}}], total: {before: {pressure_kpa: float, temperature_k:
          float, total_mol: float, gases: list[{gas: str, amount_mol: float}]}, after: {pressure_kpa: float, temperature_k: float, total_mol: float,
          gases: list[{gas: str, amount_mol: float}]}, after_boiling: bool | None}, joined_by: list[{reference_id: str, prefab_name: str | None,
          display_name: str | None}], room: str | None} | None
      to: {members: list[{owner: {kind: str, reference_id: str, prefab_name: str | None, display_name: str | None}, atmosphere_id: str, before:
          {pressure_kpa: float, temperature_k: float, total_mol: float, gases: list[{gas: str, amount_mol: float}]}, after: {pressure_kpa: float,
          temperature_k: float, total_mol: float, gases: list[{gas: str, amount_mol: float}]}}], total: {before: {pressure_kpa: float, temperature_k:
          float, total_mol: float, gases: list[{gas: str, amount_mol: float}]}, after: {pressure_kpa: float, temperature_k: float, total_mol: float,
          gases: list[{gas: str, amount_mol: float}]}, after_boiling: bool | None}, joined_by: list[{reference_id: str, prefab_name: str | None,
          display_name: str | None}], room: str | None} | None
      deleted: bool
      moved: float | None
      error: Error | None
      before: Any | None
      amount_mol: float | None
      ticks: int | None
      note: str | None
    Types: Error = {code: str, message: str, data: Any | None, see: {verb: str | None, what: str | None, topic: str | None, subtopic: str | None} |
        None}
    Example: sg.move('gas', source='2005300', gases=['Hydrogen'], delete=True)
    """
