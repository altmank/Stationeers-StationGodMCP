"""The verdict on a show picture proof's log (shots.py's replies.json): a list of failures, empty for a pass. It reads
nothing but the log, so a finished run's replies can be judged again offline (test_judge_shots.py).

A run passes only if the player wears a suit and a jetpack and every picture has: the player out of any seat, the player as its target, occluded_by null, and a
camera on the side its yaw asks for, within 5 degrees of the body's front (yaw 0 on it, 180 opposite, 90 on its right).
The front is read independently of the picture when the log has it: inspect looking_at's view.forward, the camera's
heading, which the game copies to a standing player's body each frame (CameraController.cs). Without that read,
target.front stands in, and a note says so. A body within 20 degrees of north is a note, not a failure: there the old
defect (a front always north) looks the same as the fix. A save holds no body facing, so a loaded player faces north
and this run cannot tell them apart; the fix rests on the decompile and the unit tests.
"""
import math

PICTURES = ("scene_front_before", "neutral_front", "neutral_side", "neutral_back", "scene_front", "scene_side",
            "scene_back", "neutral_4096")
REFUSALS = {"escape": "bad_path", "device": "bad_path", "exists": "file_exists"}
MOST_DEGREES = 5.0
NORTH = (0.0, 1.0)
LEAST_FROM_NORTH = 20.0


def level(x, z):
    length = math.hypot(x, z)
    return (x / length, z / length) if length > 1e-6 else None


def degrees_between(a, b):
    dot = max(-1.0, min(1.0, a[0] * b[0] + a[1] * b[1]))
    return math.degrees(math.acos(dot))


def around(front, yaw):
    """The level direction from the thing to the camera at yaw, up x front being its right."""
    radians = math.radians(yaw)
    right = (front[1], -front[0])
    return (front[0] * math.cos(radians) + right[0] * math.sin(radians),
            front[1] * math.cos(radians) + right[1] * math.sin(radians))


def body_front(log):
    """(front, source): the independent read when the log has one, else None; a seated or third-person view is none."""
    view = log.get("view") or {}
    forward = view.get("forward") or {}
    if forward and not view.get("seated") and not view.get("third_person"):
        return level(forward.get("x", 0), forward.get("z", 0)), "inspect looking_at view.forward"
    return None, None


def worn_slots(contents):
    """The player's occupied slots, from inspect contents' reply (one result or the list form)."""
    result = ((contents or {}).get("results") or [contents or {}])[0]
    return (result.get("payload") or result).get("slots") or []


def seated(thing):
    return bool(thing.get("held_in")) or thing.get("location") == "stored"


def judge(log):
    """(failures, notes)."""
    failures, notes = [], []
    independent, source = body_front(log)
    if independent is None:
        notes.append("no independent front read: target.front stands in; judge the pictures by eye")
    else:
        notes.append(f"front read from {source}")
    prefabs = [str(row.get("prefab_name")) for row in worn_slots(log.get("worn"))]
    if not any("Suit" in p and not p.startswith("Uniform") for p in prefabs) or not any("Jetpack" in p for p in prefabs):
        failures.append(f"the player does not wear a suit and a jetpack: {prefabs}")
    if independent is not None and degrees_between(independent, NORTH) <= LEAST_FROM_NORTH:
        notes.append(f"facing north ({independent}, within {LEAST_FROM_NORTH:.0f} degrees): yaw fix not "
                     "distinguishable in this run")
    players = log.get("players") or {}
    for key in PICTURES:
        reply = log.get(key)
        if not isinstance(reply, dict) or "error" in reply or "camera" not in reply:
            failures.append(f"{key}: no picture ({reply})")
            continue
        thing = players.get(key) or log.get("player") or {}
        if seated(thing):
            failures.append(f"{key}: the player is in a seat ({thing.get('location')}, {thing.get('held_in')})")
        if reply.get("occluded_by") is not None:
            failures.append(f"{key}: occluded by {reply['occluded_by']}")
        target = reply.get("target") or {}
        if str(target.get("id")) != str(thing.get("reference_id")):
            failures.append(f"{key}: pictured {target.get('id')}, not the player {thing.get('reference_id')}")
        stated = target.get("front")
        front = independent
        if front is None and stated:
            front = level(stated["x"], stated["z"])
        if front is None:
            failures.append(f"{key}: no front to check the camera against")
            continue
        if stated and degrees_between(front, level(stated["x"], stated["z"])) > MOST_DEGREES:
            failures.append(f"{key}: target.front {stated} is not the body's front {front}")
        camera = reply["camera"]
        side = level(camera["position"]["x"] - camera["look_at"]["x"], camera["position"]["z"] - camera["look_at"]["z"])
        off = degrees_between(side, around(front, camera["yaw"])) if side else 180.0
        if off > MOST_DEGREES:
            failures.append(f"{key}: camera {off:.1f} degrees off yaw {camera['yaw']} from the body's front")
    for name, want in REFUSALS.items():
        got = (((log.get("refusals") or {}).get(name) or {}).get("error") or {}).get("code")
        if got != want:
            failures.append(f"refusal {name}: {got}, not {want}")
    return failures, notes
