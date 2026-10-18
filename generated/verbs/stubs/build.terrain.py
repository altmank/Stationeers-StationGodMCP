def build(what: Literal['terrain'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    box: list,  # Two corners [[x, y, z], [x, y, z]] in metres: every 1 m voxel whose centre lies inside (at most 32768, a 32 m cube).
    mode: Literal['solid', 'original', 'level', 'voids'] = 'solid',  # solid: every voxel filled; original: back to the generated ground (dug ground filled, added ground taken away); level: a flat plane at level, ground under it filled and above it dug; voids: only the box's enclosed empty pockets filled.
    level: float = None,  # mode level: the plane's height in metres; voxels whose centre lies under it are filled, the rest dug.
    ores: Literal['drop', 'discard'] = 'drop',  # Ore in the ground it digs (level, original): drop leaves it as ore items, discard makes none.
    into_frames: int = 0,  # How far, in metres, the fill reaches into structure cells from the open ground beside them: 0 sets the ground up to each frame's face, 1 or more also sets that many 1 m voxels inside the frames, for a smooth meeting.
) -> dict:
    """Fill terrain in a box at once: solid, back to the ground the world generated, level to a flat plane, or only its enclosed voids.
    A cheat: no player fills ground by the box. Voxels inside structures (all eight 1 m cubes around a voxel built: frames, walls, large devices) are never touched, so ground meets frames at their faces, on every side; pipes, cables and chutes lying in open ground (outside frames) can be covered, and covers names each one: with mode level that makes a flat plane up to the frames' edges. into_frames lets it reach that many metres into the structures. Mode original also puts back each voxel's generated material (stone, ice, ore), as the console's voxelfillnear does; clients see the material after they rejoin, the shape at once. Bedrock (below y 2) is never changed. A fill that would bury a player is refused (player_in_fill); players_in_box names who stands in the box.
    The report counts the voxels it fills and digs, those already so, those kept at a structure or at bedrock, the pockets it fills (voids) and the ore it releases; its box is the voxel-aligned box, ready for show preview's boxes (xray: true shows it through the ground). Find pockets first with find void, whose fill_box is the box to give with mode voids. Each voxel is set as the game's own dirt placement sets one, over frames from the bottom layer up, one slice per state packet with clients; a call cut short keeps what it did.
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    placed: list[str] on a job reply, the new ids (a run's log.placed, a structure job's result.placed; a brief poll counts them in log.placed_count); int on a run report, the cells it would build.
    Reply:
      box: {min: Vec3, max: Vec3}
      cells: int
      mode: str
      level: float | None
      filled: int
      dug: int
      unchanged: int
      material_restored: int | None (mode original)
      structure_kept: int
      bedrock_kept: int
      voids: int | None (mode voids)
      ores_handling: str
      ores: list[{prefab_name: str, minables: int, quantity: int | None}]
      players_in_box: list[Thing] | None
      covers: list[Thing] | None (pipes, cables and chutes in open ground the fill covers)
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.build('terrain', box=[[-920, 146, -560], [-910, 152, -548]], mode='level', level=150)
    """
