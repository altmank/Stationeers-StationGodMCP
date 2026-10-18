def remove(what: Literal['terrain'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    box: list,  # Two corners [[x, y, z], [x, y, z]] in metres: every 1 m voxel whose centre lies inside (at most 32768, a 32 m cube).
    ores: Literal['drop', 'discard'] = 'drop',  # drop: each ore in the box falls as an ore item where it lay, as a mining charge leaves it; discard: none is made.
) -> dict:
    """Dig out every terrain voxel in a box at once, the ore in it dropped where it lay or discarded.
    A cheat: no player digs a box out at once. The report counts the voxels the dig takes, the ore it would release (each ore's minables), the voxels already empty, those it keeps at bedrock (below y 2, which the game never digs), and the players standing in the box, who fall once it is dug. Its box is the voxel-aligned box the dig takes, {min, max}, ready for show preview's boxes: show it to the player, then dig on their go.
    Each voxel is dug as the game's own diggers dig one (a quarry or deep miner: density 0, as terrain is removed), so the cells it opens join the air next to them and the change reaches every client. It digs from the top down over frames; on a server with clients each slice waits for the state packet that carries it. A call cut short by its deadline keeps what it dug: report again to see what is left.
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Reply:
      box: {min: Vec3, max: Vec3}
      cells: int
      dug: int
      already_empty: int
      bedrock_kept: int
      ores_handling: str
      ores: list[{prefab_name: str, minables: int, quantity: int | None}]
      players_in_box: list[Thing] | None
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.remove('terrain', box=[[100, 40, 200], [110, 45, 210]])
    """
