def find(what: Literal['void'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    box: list,  # Two corners [[x, y, z], [x, y, z]] in metres to search (at most 262144 voxels, a 64 m cube).
    min_cells: int = 8,  # Leave out pockets of fewer voxels.
) -> dict:  # main list: r['voids']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """Enclosed empty pockets underground in a box: dug or natural cavities no structure reaches.
    A pocket is a set of joined empty voxels (under half density, joined face to face) that reaches neither the box's outer layer nor any cell holding a structure: empty space that reaches the edge may go on to the surface or a dug hallway (open_to_edge counts it: search a bigger box), and space joined to a frame, wall, pipe or device is part of the base (touching_structures). Each pocket gives its voxels, bounds, a voxel inside it, the players inside it, and fill_box, the box to give build terrain with mode voids. Read only: nothing changes.
    Reply:
      searched: {min: Vec3, max: Vec3}
      searched_cells: int
      voids: list[{index: int, cells: int, min: Vec3, max: Vec3, inside: Vec3, fill_box: list[list[float]], players_inside: list[Thing] | None}]
      count: int
      smaller_than_min: int
      open_to_edge: int
      open_to_edge_cells: int
      touching_structures: int
    Types: Thing = {reference_id: str, prefab_name: str | None, display_name: str | None}; Vec3 = {x: float, y: float, z: float}
    Example: sg.find('void', box=[[-885, 145, -570], [-860, 175, -552]])
    """
