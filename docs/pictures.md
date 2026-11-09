# Pictures

[Back to the README](../README.md)

`show picture` saves one PNG of a thing, or of the player, from a camera placed around it. It is made for checking a
mod's looks the same way every time: a worn suit from the front, side and back at fixed framing, a machine before
and after a change. Nothing in the world changes and nothing is sent to other players; the file is the only thing
written.

## Tools

| Call | What it does | Main options |
| --- | --- | --- |
| `show picture` | Places a temporary camera around the thing, renders it into a texture and writes a PNG. | `id` (default `"player"`), `yaw`, `pitch`, `distance`, `fov`, `width`, `height`, `file` (required), `folder`, `overwrite`, `lighting` |

```python
for yaw, side in ((0, "front"), (90, "side"), (180, "back")):
    sg.show('picture', yaw=yaw, pitch=10, fov=40, file=f"suit-{side}.png", folder="suits", overwrite=True)
```

## The camera

- **Where it stands.** On a sphere around the thing's centre (the middle of its visible meshes, worn and held things
  with it). `yaw` turns from the thing's own level front toward its right: 0 front, 90 right side, 180 behind, 270
  left side. A player's or other entity's front is the way its body faces; the reply's `target.front` gives the vector
  used. `pitch` raises the camera above the centre, looking down (default 10); negative is below. `distance`
  is in metres; left out, the thing's bounds fill the picture with a 10% margin. `fov` is the vertical field of view
  (default 40).
- **What it sees.** The player's own body and worn things, as in third person, whatever view the player is in. The
  game hides them from the first-person camera by layer (worn things on `PlayerInvisible`, the suit on
  `PlayerImmune`), and the picture camera uses the game's third-person layers instead. The first-person helmet
  overlay is left out. The HUD is never in the picture: it draws only to the screen.
- **What it never touches.** The player's own camera, view and screen. Nothing is moved, so nothing has to be put
  back.
- **Light.** `lighting: "scene"` (default) is the world as it is. `lighting: "neutral"` turns the scene's
  directional lights (the sun) off for this one render, lights the thing with a fixed white light from behind the
  camera and a flat grey ambient, then puts everything back, even if the render fails. Lamps (point and spot lights)
  stay as they are. Use it when pictures taken at different times of day must compare.
- **Size.** 1920x1080 by default; each side 16 to 4096 pixels.
- **What is in the way.** `occluded_by` in the reply names the first thing one ray from the camera to the centre meets
  when it is not the pictured thing (or `"terrain"`). Pick another yaw or a shorter distance. It tests that one ray
  only: a thing seen just through a gap, such as a player in a capsule behind its window, can read `null`, so look at
  the picture when it matters.

## Where the file goes

Every picture is written under one folder, `[Pictures] Folder` in the mod's config (default
`BepInEx\StationGodMCP\pictures`; see [Configuration](configuration.md)). A call names only:

- `file`: a plain name ending `.png`, of letters, digits, space, `-`, `_` and `.`;
- `folder` (optional): a subfolder of plain names joined by `/`, made when missing, at most 4 deep.

`..`, a drive, a rooted or network path, a Windows device name (`CON`, `NUL`, `COM1`, `LPT1`... with any extension),
a path too long for the system, a junction or symbolic link on the way, or any other character is refused `bad_path`,
so no caller (a remote one over TCP included) can write anywhere else. An existing file is refused `file_exists`
unless `overwrite: true`.

The PNG is written to a hidden temporary file beside it and given its name only when the call answers, so a call that
is cancelled, times out or outlives its world leaves no file (and frees its texture and its turn at once).

## The reply

`path` (the full path written), `width`, `height`, `bytes`, `target` (`id`, `prefab`, `centre`, `radius_m`, `front`), `camera`
(`position`, `look_at`, `yaw`, `pitch`, `fov`, `distance_m`), `lighting`, `occluded_by`, `render_ms` (the main-thread
milliseconds the picture took, all its steps together) and `notes`.

## Cost

The render is one extra camera pass of the scene at the picture's size, on the main thread, in one frame
(anti-aliased no more than the player's own view). The pixels come back from the GPU into a buffer of the picture's
own without a stall over the next frames; a pool thread copies them out, encodes the PNG and writes it, so the main
thread copies nothing and the reply comes a few frames after the call. `render_ms` reports the main-thread time;
measured in a VM run (vanilla suit, 1920x1080): the first picture 2.6 ms, later ones 2.0 to 6.4 ms, and a
4096x4096 one 2.4 ms. Two other runs (a modded suit) saw a first picture of 50 ms to 1.5 s, likely one-time first-use
setup; later pictures there were a few ms. One picture at a time: a call while another is being taken is refused `busy`.

## Refusals

| Code | When |
| --- | --- |
| `not_rendered` | The game draws nothing: `-batchmode`, `-nographics` or a dedicated server. |
| `bad_path` | `file` or `folder` is not plain, or would leave the pictures folder. |
| `file_exists` | The file is there and `overwrite` is not true. |
| `busy` | Another picture is being taken. |
| `thing_not_found`, `no_local_player` | No thing has that id, or there is no player to picture. |
| `render_failed` | The render, the GPU readback or the write failed; the message says why. |

Not a cheat: it changes nothing in the world and syncs nothing, and the read verbs already tell an agent what any
thing is and where.
