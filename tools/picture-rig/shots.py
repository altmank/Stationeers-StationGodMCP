"""show picture's in-game proof: pictures of the player (front, side, back) through the sg client, in a rendered client
that hosts a saved test world whose player is already on the ground. Runs in the guest, started by vm/onpoll.ps1:

    python shots.py --pipe <test pipe> --hub <hub dir or exe> --done <out folder>

The game's [Pictures] Folder is the run's out folder\\shots (vm-pictures.ps1 writes it into the mod's config), so the
pictures come back with the run's results. First:
  - a player found seated is a FAIL: test worlds are staged after landing (LU, P51), there is nothing to fix up;
  - the suit, helmet and jetpack the player wears are read; an empty slot is filled by build item;
  - probe pictures at yaw 0, 90 and 180 where the player stands; if one is blocked, stand 8 m away on the first clear side;
  - read the body's front (inspect looking_at's view). No call can turn a player, so the save's own facing it is.
Then, in order:
  1. front in the scene's light (before any neutral picture);
  2. front, side and back in neutral light;
  3. front, side and back in the scene's light again (the front one to compare with 1: the scene is put back);
  4. a 4096x4096 front picture (the largest), for its main-thread time;
  5. refusals: a folder that leaves the pictures folder (bad_path), a device name (bad_path), the same file again
     without overwrite (file_exists).
Writes replies.json (every reply) beside the pictures; judge_shots.py decides done-ok or done-fail.
"""
import argparse
import json
import math
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "..", "silo-rig"))
import item_check  # noqa: E402
import judge_shots  # noqa: E402
import keyguard_check  # noqa: E402
import sgcall  # noqa: E402

FOLDER = "suit"
VIEWS = ((0, "front"), (90, "side"), (180, "back"))
WORN = ((2, "ItemSpaceHelmet"), (3, "ItemEvaSuit"), (4, "ItemJetpackBasic"))
CLEAR_M = 8.0
SIDES = ((1, 0), (-1, 0), (0, 1), (0, -1))
STABLE_M = 0.15


def say(text):
    print(text, flush=True)


def read_player():
    reply = sgcall.call("find", "thing", runtime_type="Human", limit=1)
    things = reply.get("things") if isinstance(reply, dict) else None
    return (things[0] if things else None), reply


def wait_player(timeout_s=900.0):
    """A new world's player: the client is at the menu first."""
    end = time.monotonic() + timeout_s
    last = None
    while time.monotonic() < end:
        thing, reply = read_player()
        if thing:
            return thing
        last = reply.get("error") if isinstance(reply, dict) else reply
        time.sleep(5.0)
    raise SystemExit(f"no player within {timeout_s:.0f} s (last: {last})")


def apart(a, b):
    return math.dist((a["x"], a["y"], a["z"]), (b["x"], b["y"], b["z"]))


def wait_still(what, timeout_s=240.0):
    """The player's position holds over two reads 5 s apart (after a move, the drop ended); returns the thing."""
    end = time.monotonic() + timeout_s
    thing = wait_player()
    while time.monotonic() < end:
        time.sleep(5.0)
        now = wait_player()
        moved = apart(thing["position"], now["position"])
        say(f"{what}: y {now['position']['y']}, moved {moved:.2f} m in 5 s")
        if moved <= STABLE_M:
            return now
        thing = now
    raise SystemExit(f"{what}: the player was still moving after {timeout_s:.0f} s")


def code_of(reply):
    return (reply.get("error") or {}).get("code") if isinstance(reply, dict) else None


def must(reply, what):
    if code_of(reply):
        raise SystemExit(f"{what} refused: {reply['error']}")
    return reply


def worn():
    return sgcall.call("inspect", "contents", id="player", depth=1)


def dress(log):
    """The vanilla helmet, suit and jetpack on the player: what the save has, the rest built into their slots."""
    occupied = {row.get("slot") for row in judge_shots.worn_slots(must(worn(), "player's contents"))}
    for slot, prefab in WORN:
        if slot not in occupied:
            log[f"built_{prefab}"] = must(sgcall.call("build", "item", prefab=prefab, to="player", slot=slot, confirm=True),
                                          f"build {prefab}")
            say(f"built {prefab} into slot {slot}")
    log["worn"] = worn()
    say(f"worn: {[row.get('prefab_name') for row in judge_shots.worn_slots(log['worn'])]}")


def probe(side):
    """Whether pictures at yaw 0, 90 and 180 are clear from where the player stands; the blockers."""
    blocked = {}
    for yaw, name in VIEWS:
        reply = must(sgcall.call("show", "picture", id="player", yaw=yaw, file=f"{side}-{name}.png", folder="probe",
                                 lighting="scene", overwrite=True), "probe picture")
        if reply.get("occluded_by") is not None:
            blocked[yaw] = reply["occluded_by"]
    return blocked


def stand_clear(log, start):
    """Where the player stands if the probes find it clear, else CLEAR_M away on the first clear side."""
    blocked = probe("start")
    say(f"start: blocked {blocked}")
    if not blocked:
        log["stood"] = {"side": "start", "away_m": 0.0}
        return
    for index, (dx, dz) in enumerate(SIDES):
        at = [start["x"] + dx * CLEAR_M, start["y"], start["z"] + dz * CLEAR_M]
        log[f"stand_{index}"] = must(sgcall.call("move", "player", at=at, safe_ground=True, confirm=True), "stand clear")
        thing = wait_still(f"stand {index}")
        away = math.hypot(thing["position"]["x"] - start["x"], thing["position"]["z"] - start["z"])
        blocked = probe(f"side{index}")
        say(f"stand {index}: {away:.1f} m from the start, blocked {blocked}")
        if away >= 4.0 and not blocked:
            log["stood"] = {"side": index, "away_m": away}
            return
    raise SystemExit("no side was clear for all three yaws")


def picture(log, key, **options):
    options.setdefault("id", "player")
    options.setdefault("folder", FOLDER)
    options.setdefault("overwrite", True)
    log.setdefault("players", {})[key] = wait_player()
    reply = sgcall.call("show", "picture", **options)
    log[key] = reply
    must(reply, f"show picture {key}")
    say(f"{key}: {reply['path']} {reply['bytes']} bytes, main thread {reply['render_ms']} ms, "
        f"occluded_by {reply.get('occluded_by')}, front {reply['target'].get('front')}")
    return reply


def read_front(log, key):
    reply = must(sgcall.call("inspect", "looking_at", include=["view"]), "inspect looking_at")
    log[key] = reply.get("view")
    say(f"{key}: {reply.get('view')}")


def run(log):
    log["world_seen_at"] = time.monotonic()
    log["player"] = start = wait_player()
    if judge_shots.seated(start):
        raise SystemExit(f"the player is seated at the start ({start.get('held_in')}): test worlds start after landing")
    sgcall.wait_ready()
    dress(log)
    stand_clear(log, start["position"])
    read_front(log, "view")
    log["player"] = wait_player()
    timings = {}
    timings["scene_front_before"] = picture(log, "scene_front_before", yaw=0, file="scene-front-before.png",
                                            lighting="scene")["render_ms"]
    for yaw, side in VIEWS:
        timings[f"neutral_{side}"] = picture(log, f"neutral_{side}", yaw=yaw, file=f"neutral-{side}.png",
                                             lighting="neutral")["render_ms"]
    for yaw, side in VIEWS:
        timings[f"scene_{side}"] = picture(log, f"scene_{side}", yaw=yaw, file=f"scene-{side}.png",
                                           lighting="scene")["render_ms"]
    timings["neutral_4096"] = picture(log, "neutral_4096", yaw=0, width=4096, height=4096, file="neutral-front-4096.png",
                                      lighting="neutral")["render_ms"]
    log["timings_ms"] = timings
    read_front(log, "view_after")
    log["refusals"] = {
        "escape": sgcall.call("show", "picture", file="escape.png", folder="../.."),
        "device": sgcall.call("show", "picture", file="con.png", folder=FOLDER),
        "exists": sgcall.call("show", "picture", yaw=0, file="neutral-front.png", folder=FOLDER),
    }
    return timings


def frame_reads(log, done, after_load_s=60.0, gap_s=30.0):
    """The game's whole frame time (server info counters, runtime.game_frame_ms, a 60 s window): one read at least
    after_load_s after the world was seen and one gap_s later, written to <done>/frames.json."""
    reads = []
    wait = log["world_seen_at"] + after_load_s - time.monotonic()
    if wait > 0:
        time.sleep(wait)
    for n in range(2):
        if n:
            time.sleep(gap_s)
        reply = sgcall.call("server", "info", include=["counters"])
        runtime = reply.get("runtime") if isinstance(reply, dict) else None
        reads.append({"seconds_after_load": round(time.monotonic() - log["world_seen_at"], 1),
                      "game_frame_ms": (runtime or {}).get("game_frame_ms"),
                      "error": reply.get("error") if isinstance(reply, dict) else None})
    log["frames"] = reads
    with open(os.path.join(done, "frames.json"), "w", encoding="utf-8") as f:
        json.dump(reads, f, indent=1)
    say("frames: " + json.dumps(reads))


def finish_items(done, log, pipe, hub):
    """The item check alone: connect, wait for the player and the clock, run it, write the done file (the failing
    cases and their messages are its text, and items.json's `failures`); the exit code."""
    ok, reason = False, ""
    try:
        sgcall.connect(pipe, hub)
        wait_player()
        sgcall.wait_ready()
        failures, notes = item_check.run(sgcall, done, log, say)
        ok = not failures
        reason = "; ".join(failures) if failures else "item check passed: " + "; ".join(notes)
    except BaseException as error:  # noqa: BLE001 - any end other than success is a done-fail for the watcher
        reason = f"{type(error).__name__}: {error}"
        log["failures"] = [reason]
    finally:
        with open(os.path.join(done, "items.json"), "w", encoding="utf-8") as f:
            json.dump(log, f, indent=1, default=str)
        with open(os.path.join(done, "done-ok" if ok else "done-fail"), "w", encoding="utf-8") as f:
            f.write(reason)
    say(("ok: " if ok else "fail: ") + reason)
    return 0 if ok else 1


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--pipe", required=True)
    ap.add_argument("--hub", required=True)
    ap.add_argument("--done", required=True)
    ap.add_argument("--frames", action="store_true", help="after the pictures, two game frame-time reads to frames.json")
    ap.add_argument("--keyguard", action="store_true",
                    help="after the pictures, the key guard check: ghost keys do nothing while the Stationpedia search has focus")
    ap.add_argument("--item-checks", action="store_true",
                    help="the item check alone (item_check.py: prefab slot rows against live slots, remove item cases); "
                         "the pictures are skipped")
    ap.add_argument("--game-log", default="", help="the guest's game log, Player.log (StationGod logs through Unity; the key guard check reads it)")
    a = ap.parse_args(argv)
    if a.keyguard and not a.game_log:
        ap.error("--keyguard needs --game-log")
    os.makedirs(a.done, exist_ok=True)
    log = {}
    if a.item_checks:
        return finish_items(a.done, log, a.pipe, a.hub)
    ok, reason = False, ""
    try:
        sgcall.connect(a.pipe, a.hub)
        timings = run(log)
        if a.frames:
            frame_reads(log, a.done)
        failures, notes = judge_shots.judge(log)
        if a.keyguard:
            guard_failures, guard_notes = keyguard_check.run(a.game_log, log, say)
            log["keyguard"] = {"failures": guard_failures, "notes": guard_notes}
            failures += guard_failures
            notes += guard_notes
        log["verdict"] = {"failures": failures, "notes": notes}
        ok = not failures
        reason = "; ".join(failures) if failures else (
            "main-thread ms " + ", ".join(f"{k} {v}" for k, v in timings.items()) + "; " + "; ".join(notes))
    except BaseException as error:  # noqa: BLE001 - any end other than success is a done-fail for the watcher
        reason = f"{type(error).__name__}: {error}"
    finally:
        with open(os.path.join(a.done, "replies.json"), "w", encoding="utf-8") as f:
            json.dump(log, f, indent=1, default=str)
        with open(os.path.join(a.done, "done-ok" if ok else "done-fail"), "w", encoding="utf-8") as f:
            f.write(reason)
    say(("ok: " if ok else "fail: ") + reason)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
