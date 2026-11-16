"""The stand check, in the guest: move player {stand} beside a battery clear of its neighbour, refused when the target is
boxed in by frames, and write interact pressing a battery's switch, against a real world (the VM's throwaway one).

Pure parts (the verdicts) are tested offline in test_stand_check.py; the guest parts call StationGod through the sg client
(sgcall) and gather evidence into out\\stand.json. Every expected value is read from a reply (positions, the interactable
rows, the reach asked is the one the call passed), never typed.

Cases: stand-clear, stand-none, interact.
"""
import json
import math
import os
import time

import item_check
from item_check import code_of, message_of

BATTERY = "StructureBatteryLarge"  # the battery tools/test-worlds/builder.py builds on its frame floor (sg-silo, 11-02)
FRAME = "StructureFrame"
CASES = ("stand-clear", "stand-none", "interact")
MARKER = "stand-check ran: " + ",".join(CASES)
REACH_M = 2.5
NONE_REACH_M = 1.0
FAR_M = 10.0
NO_SUCH = "NoSuchInteractable"
MOVED_EPS = 0.25


# ---- pure parts ----

def body(reply):
    """The reply of a one-id call: its first result row when the hub wrapped it as {results: [...]}, else the reply."""
    rows = (reply or {}).get("results")
    if rows and isinstance(rows[0], dict):
        return rows[0].get("payload") or rows[0]
    return reply or {}


def xyz(point):
    if isinstance(point, dict):
        return (point.get("x", 0.0), point.get("y", 0.0), point.get("z", 0.0))
    return tuple(point)


def level_distance(a, b):
    a, b = xyz(a), xyz(b)
    return math.hypot(a[0] - b[0], a[2] - b[2])


def thing_positions(found):
    """{reference_id: position} of a find thing reply."""
    return {str(t["reference_id"]): t["position"] for t in (found or {}).get("things") or [] if t.get("position")}


def interactable_rows(reply):
    """The interactables rows of an inspect device reply made with include interactables."""
    return list(body(reply).get("interactables") or [])


def pressable(rows):
    """The row a plain click flips: a live state and two states, the first such row. None when no row qualifies."""
    for row in rows:
        if row.get("state") is not None and row.get("states") == 2:
            return row
    return None


def clear_failures(evidence):
    clear = evidence.get("clear")
    if not clear:
        return ["stand-clear: not run"]
    reply = clear.get("reply")
    if code_of(reply):
        return [f"stand-clear: move player stand refused {code_of(reply)} {message_of(reply)}"]
    failures = []
    stand = body(reply).get("stand")
    if not stand:
        return ["stand-clear: the reply has no stand record"]
    position = stand.get("position")
    for name in ("position", "eye_to_target_m", "floor", "tried", "rejected"):
        if stand.get(name) in (None, ""):
            failures.append(f"stand-clear: stand.{name} is missing")
    if failures:
        return failures
    if stand["eye_to_target_m"] > clear["reach_m"] + 1e-6:
        failures.append(f"stand-clear: the eye is {stand['eye_to_target_m']} m from the target, over the reach "
                        f"{clear['reach_m']} m asked")
    for thing, (centre, inner) in clear["bodies"].items():
        if level_distance(position, centre) < inner:
            failures.append(f"stand-clear: the feet are inside body {thing} (level {level_distance(position, centre):.2f} m "
                            f"from its centre, body half-width {inner:.2f} m)")
    after = (body(reply).get("after") or {}).get("position")
    if not after or level_distance(after, position) > MOVED_EPS:
        failures.append(f"stand-clear: the player after the move is at {after}, not at the stand {position}")
    return failures


def none_failures(evidence):
    none = evidence.get("none")
    if not none:
        return ["stand-none: not run"]
    failures = []
    reply = none.get("reply")
    if code_of(reply) != "no_clear_stand":
        failures.append(f"stand-none: a boxed-in target answered {code_of(reply) or 'ok'} {message_of(reply)}, "
                        "not no_clear_stand")
    elif not any(ch.isdigit() for ch in message_of(reply)):
        failures.append("stand-none: no_clear_stand states no counts")
    before, after = none.get("before"), none.get("after")
    if before is None or after is None or level_distance(before, after) > MOVED_EPS or \
            abs(xyz(before)[1] - xyz(after)[1]) > MOVED_EPS:
        failures.append(f"stand-none: the player moved from {before} to {after} on a refusal")
    return failures


def interact_failures(evidence):
    press = evidence.get("interact")
    if not press:
        return ["interact: not run"]
    failures = []
    rows = press.get("rows") or []
    row = pressable(rows)
    if row is None:
        return [f"interact: no interactable of the battery lists a live state and two states: {rows}"]
    pressed = press.get("pressed")
    if code_of(pressed):
        failures.append(f"interact: the press refused {code_of(pressed)} {message_of(pressed)}")
    else:
        got = body(pressed)
        if (got.get("interactable") or {}).get("name") != row["name"]:
            failures.append(f"interact: the reply names {got.get('interactable')}, not {row['name']!r}")
        if got.get("before") != row["state"]:
            failures.append(f"interact: before is {got.get('before')}, the row read {row['state']}")
        if got.get("after") == got.get("before"):
            failures.append(f"interact: the state did not change ({got.get('before')} -> {got.get('after')})")
        later = press.get("rows_after") or []
        read = next((r for r in later if r.get("index") == row["index"]), None)
        if read is None or read.get("state") != got.get("after"):
            failures.append(f"interact: the state read back is {None if read is None else read.get('state')}, "
                            f"the reply said {got.get('after')}")
    unknown = press.get("unknown")
    if code_of(unknown) != "interactable_not_found":
        failures.append(f"interact: an unknown interactable answered {code_of(unknown) or 'ok'}, "
                        "not interactable_not_found")
    elif not all(str(r["name"]) in message_of(unknown) for r in rows):
        failures.append("interact: interactable_not_found does not list the names present")
    far = press.get("far")
    if code_of(far) != "out_of_range":
        failures.append(f"interact: a press from {press.get('far_m')} m answered {code_of(far) or 'ok'}, not out_of_range")
    return failures


def verdict(evidence):
    """(failures, notes) of a whole run's stand.json content: exactly the three cases and the marker."""
    failures = clear_failures(evidence) + none_failures(evidence) + interact_failures(evidence)
    if evidence.get("marker") != MARKER:
        failures.append("the stand check did not run: its marker is missing from the evidence")
    notes = [MARKER] if evidence.get("marker") == MARKER else []
    press = evidence.get("interact") or {}
    row = pressable(press.get("rows") or [])
    if row is not None:
        notes.append(f"a fresh battery's {row['name']} read {row['state']} before the press")
    stand = body((evidence.get("clear") or {}).get("reply")).get("stand") or {}
    if stand.get("eye_to_target_m") is not None:
        notes.append(f"eye to target {stand['eye_to_target_m']} m")
    return failures, notes


# ---- guest-only parts ----

FINAL = ("applied", "applied_with_differences", "applied_unchecked", "stopped", "gas_lost", "refused")
JOB_WAIT_S = 60.0


# ---- guest-only parts ----
# The scene is laid the way tools/test-worlds/builder.py lays a site (proven in a real rig run): a floor of StructureFrame on
# the game's 2 m grid (cell centres on odd metres), then each fixture at the centre of the cell one up from the floor.

WIDTH, DEPTH = 9, 6
LIFT_M = 4.0
LIFT_STEPS = (0.0, 4.0, 8.0, 12.0)


def _call(sgcall, verb, what=None, **options):
    return sgcall.call(verb, what, **options)


def odd(value):
    """The nearest odd whole metre: a 2 m cell's centre (twlib.odd)."""
    n = int(math.floor(value))
    return n if n % 2 else n + 1


class Floor:
    """Cell (i, k) is the 2 m cell at x0 + 2i, z0 + 2k on the floor; fixtures stand one cell above it (twlib.Site)."""

    def __init__(self, x0, floor_y, z0):
        self.x0, self.floor_y, self.z0 = odd(x0), odd(floor_y), odd(z0)

    def cell(self, i, k, up=1):
        return [self.x0 + 2 * i, self.floor_y + 2 * up, self.z0 + 2 * k]

    def frames(self):
        return [self.cell(i, k, up=0) for k in range(DEPTH) for i in range(WIDTH)]


def placed_ids(job):
    for holder in (job, (job or {}).get("result") or {}):
        rows = holder.get("placed")
        if isinstance(rows, list):
            return [str(r["reference_id"]) if isinstance(r, dict) else str(r) for r in rows]
    return []


def build_job(sgcall, sleep=time.sleep, **options):
    """build structure confirmed, waited to its end: the reply is a queued job until its status is final (inspect job;
    max_wait is the hub's own option and the game refuses it over the pipe)."""
    reply = _call(sgcall, "build", "structure", confirm=True, **options)
    end = time.monotonic() + JOB_WAIT_S
    while not code_of(reply) and reply.get("status") not in FINAL and reply.get("job_id") and time.monotonic() < end:
        sleep(1.0)
        reply = _call(sgcall, "inspect", "job", id=reply["job_id"])
    return reply


def build_rows(sgcall, rows, log, key):
    """Placements reported first and built only when the report is ready, as builder.py does. Returns the ids placed, or
    None with the report's problems verbatim in log[key]."""
    report = _call(sgcall, "build", "structure", placements=rows, free=True)
    entry = {"rows": len(rows), "ready": report.get("ready"), "code": code_of(report),
             "problems": report.get("problems") if report.get("ready") is not True else None,
             "message": message_of(report)}
    log.setdefault(key, []).append(entry)
    if report.get("ready") is not True or code_of(report):
        return None
    job = build_job(sgcall, placements=rows, free=True)
    entry["status"] = job.get("status")
    if code_of(job) or job.get("status") != "applied":
        entry["job"] = job
        return None
    ids = placed_ids(job)
    entry["placed"] = len(ids)
    return ids


def lay_floor(sgcall, at, log):
    """The frame floor LIFT_M above the player (a step higher each time the report refuses it, as builder.floor_site)."""
    for step in LIFT_STEPS:
        floor = Floor(at[0] + 4, at[1] + LIFT_M + step, at[2] + 4)
        rows = [{"prefab": FRAME, "at": cell} for cell in floor.frames()]
        ids = []
        for start in range(0, len(rows), 64):
            made = build_rows(sgcall, rows[start:start + 64], log, "floor")
            if made is None:
                ids = None
                break
            ids += made
        if ids is not None:
            log["floor_at"] = {"x0": floor.x0, "floor_y": floor.floor_y, "z0": floor.z0, "frames": len(ids)}
            return floor
    raise SystemExit("stand check: the frame floor was refused at every height: " + json.dumps(log, default=str)[:3500])


def place_one(sgcall, prefab, cell, log, key, label):
    """The id of one thing built at a cell centre, or None (the report's problems are in log[key])."""
    made = build_rows(sgcall, [{"prefab": prefab, "at": cell, "label": label}], log, key)
    return made[0] if made else None


def player_position(sgcall):
    found = _call(sgcall, "find", "thing", runtime_type="Human", limit=1)
    things = found.get("things") if isinstance(found, dict) else None
    if not things:
        raise SystemExit("stand check: no player")
    return things[0]["position"]


def body_of(sgcall, thing_id, centre):
    """(centre, half-width) of a placed thing: its position from find thing near its cell, and the smaller horizontal
    half-size of its prefab's render box (read from inspect prefab, unturned, so the smaller half is a turn-proof lower
    bound of its reach)."""
    box = body(_call(sgcall, "inspect", "prefab", prefab=BATTERY)).get("render_box") or {}
    size = box.get("size") or {}
    half = min(size.get("x", 0.0), size.get("z", 0.0)) / 2.0
    found = thing_positions(_call(sgcall, "find", "thing", near=list(xyz(centre)), radius=3, limit=50))
    return found.get(thing_id, centre), half


def clear_part(sgcall, log, floor):
    clear = log["clear"] = {"reach_m": REACH_M}
    cell_a, cell_b = floor.cell(1, 1), floor.cell(2, 1)
    first = place_one(sgcall, BATTERY, cell_a, log, "clear_build_a", "SC a")
    second = place_one(sgcall, BATTERY, cell_b, log, "clear_build_b", "SC b") if first else None
    if not (first and second):
        raise SystemExit("stand check: two batteries could not be built for stand-clear: "
                         + json.dumps(log, default=str)[:3500])
    clear["target"], clear["neighbour"] = first, second
    clear["bodies"] = {first: body_of(sgcall, first, cell_a), second: body_of(sgcall, second, cell_b)}
    clear["reply"] = _call(sgcall, "move", "player", to=first, stand={"reach_m": REACH_M}, confirm=True)


def none_part(sgcall, log, floor):
    none = log["none"] = {}
    ci, ck = 6, 1
    target = place_one(sgcall, BATTERY, floor.cell(ci, ck), log, "none_build_target", "SC boxed")
    if not target:
        raise SystemExit("stand check: the battery for stand-none could not be built: " + json.dumps(log, default=str)[:3500])
    ring = [{"prefab": FRAME, "at": floor.cell(ci + di, ck + dk)} for di in (-1, 0, 1) for dk in (-1, 0, 1)
            if (di, dk) != (0, 0)]
    made = build_rows(sgcall, ring, log, "none_build_ring")
    if made is None:
        raise SystemExit("stand check: the frame ring around the battery was refused: "
                         + json.dumps(log.get("none_build_ring"), default=str)[:3500])
    none["frames"] = len(made)
    none["target"] = target
    none["before"] = player_position(sgcall)
    none["reply"] = _call(sgcall, "move", "player", to=target, stand={"reach_m": NONE_REACH_M}, confirm=True)
    none["after"] = player_position(sgcall)


def interact_part(sgcall, log, floor):
    press = log["interact"] = {}
    battery = place_one(sgcall, BATTERY, floor.cell(3, 4), log, "interact_build", "SC press")
    if not battery:
        raise SystemExit("stand check: no battery could be built for interact: " + json.dumps(log, default=str)[:3500])
    press["thing"] = battery
    press["rows"] = interactable_rows(_call(sgcall, "inspect", "device", id=battery, include=["interactables"]))
    row = pressable(press["rows"])
    if row is None:
        return
    press["standing"] = _call(sgcall, "move", "player", to=battery, stand={"reach_m": REACH_M}, confirm=True)
    press["pressed"] = _call(sgcall, "write", "interact", id=battery, interactable=row["name"])
    press["rows_after"] = interactable_rows(_call(sgcall, "inspect", "device", id=battery, include=["interactables"]))
    press["unknown"] = _call(sgcall, "write", "interact", id=battery, interactable=NO_SUCH)
    here = player_position(sgcall)
    press["far_m"] = FAR_M
    far_at = [xyz(here)[0] + FAR_M, xyz(here)[1], xyz(here)[2]]
    press["moved_away"] = _call(sgcall, "move", "player", at=far_at, confirm=True)
    press["far"] = _call(sgcall, "write", "interact", id=battery, interactable=row["name"])


def run(sgcall, done, log, say=print):
    """The whole check against the live guest game. Returns (failures, notes); evidence goes to <done>\\stand.json."""
    position = player_position(sgcall)
    at = [xyz(position)[0], xyz(position)[1], xyz(position)[2]]
    floor = lay_floor(sgcall, at, log)
    say("stand check: the frame floor is built")
    for name, part in (("stand-clear", clear_part), ("stand-none", none_part), ("interact", interact_part)):
        part(sgcall, log, floor)
        say(f"stand check: {name} done")
    log["marker"] = MARKER
    failures, notes = verdict(log)
    log["verdict"] = {"failures": failures, "notes": notes}
    with open(os.path.join(done, "stand.json"), "w", encoding="utf-8") as f:
        json.dump(log, f, indent=1, default=str)
    return failures, notes
