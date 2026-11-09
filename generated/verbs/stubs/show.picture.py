def show(what: Literal['picture'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str = 'player',  # The thing to picture: its id, "looking_at", or "player" (the local player).
    yaw: float = 0,  # Degrees around the thing from its front (a player's or other entity's body, whichever way it faces; the reply's target.front): 0 front, 90 its right side, 180 behind, 270 its left side.
    pitch: float = 10,  # Degrees above the thing's centre (looking down at it); negative is below.
    distance: float = None,  # Metres from the thing's centre to the camera; left out, the thing's bounds fill the picture with a 10% margin.
    fov: float = 40,  # Vertical field of view in degrees.
    width: int = 1920,  # Picture width in pixels.
    height: int = 1080,  # Picture height in pixels.
    file: str,  # The file's name: a plain name ending .png (letters, digits, space, - _ . only).
    folder: str = None,  # A subfolder of the pictures folder ([Pictures] Folder in the mod's config), made when missing: plain names joined by /, no .. and no drive.
    overwrite: bool = False,  # Replace a file of that name; else the call is refused file_exists.
    lighting: Literal['scene', 'neutral'] = 'scene',  # scene: the world's own light as it is; neutral: for this picture only, the scene's directional lights off, a fixed white light from behind the camera and a flat grey ambient, so pictures taken at different times compare.
) -> dict:
    """Save a PNG of a thing or the player from a camera placed by yaw, pitch and distance (third person; the player's own view never moves).
    A temporary camera renders into a texture of width x height; the player's own camera and screen are never moved or changed, and the HUD is never in the picture.
    The player's own body and worn things are drawn as in third person, whatever view the player is in.
    The file is written after a few frames (the GPU's copy comes back without a stall); the reply comes once it is on disk. One picture at a time: a call while another is being taken is refused busy.
    occluded_by names the first thing one ray from the camera to the thing's centre meets, when it is not the thing itself; a thing seen only through a gap can read null, so check the picture when it matters.
    A game that draws nothing (-batchmode, -nographics, a dedicated server) is refused not_rendered.
    Reply:
      path: str
      width: int
      height: int
      bytes: int
      target: {id: str, prefab: str, centre: Vec3, radius_m: float, front: {x: float, z: float}}
      camera: {position: Vec3, look_at: Vec3, yaw: float, pitch: float, fov: float, distance_m: float}
      lighting: str
      occluded_by: str | None
      render_ms: float
      notes: list[str]
    Types: Vec3 = {x: float, y: float, z: float}
    Example: sg.show('picture', id='player', yaw=90, file='suit-side.png', folder='suits')
    """
