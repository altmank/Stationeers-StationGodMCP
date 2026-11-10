r"""Builds a test world on a running TEST server, saves it, and stores it in the world library.

    py -3.12 build_world.py build --world sg-silo --pipe <test pipe> --hub <sidecar dir> --manifest <file.json>
                                  [--saves <saves folder>] [--done <dir>]
    py -3.12 build_world.py store --world sg-silo --save-folder <server saves>\sg-silo --manifest <file.json>
                                  [--library <library>]

build: the game must be running a new creative world with StationGod loaded and a player in it: either a client
started with `-newgame Lunar Creative` (it hosts the world and is the player; vm-build-world.ps1 does this in a VM
rig), or a dedicated server started with `-new Lunar Creative` that one Steam-off client has joined once (a dedicated
server makes no player). It builds the world's fixtures through StationGod's verbs, sets the world's difficulty,
saves it as <world> (the console's save), and writes the manifest (each fixture's role and id). It refuses the live
pipe (StationGodMCP). --saves: the game's saves folder; build waits there for <world>\<world>.save to be written and
stop growing. --done: a folder where build writes done-ok, or done-fail with the reason, when it ends (the rig run
tool's watcher waits for that file).

smoke: the first run on a new rig. The same game as build, nothing built: it checks StationGod answers (its
welcome's role and dedicated, server info, the clock, the player), saves as sg-smoke and waits for the saved file; --done as for build.

store: a file step, no game. It copies the saved folder's <world>.save into <library>\<world>\ (flat) and writes
world.txt, README.md and fixtures.json beside it. The library is --library, else STATIONGOD_TEST_WORLDS; it writes
nothing outside it.

The game run around `build` (start the server, join the client, hold bt-game-lock.ps1, stop it all) belongs to the
VM run tool; see README.md here.
"""
import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import builder  # noqa: E402
import twlib  # noqa: E402


def connect(pipe, hub, world_wait_s=300.0):
    sys.path.insert(0, os.path.join(HERE, "..", "silo-rig"))
    import sgcall
    step(f"connect: hub {hub}, pipe {pipe}")
    transport = sgcall.connect(pipe, hub)
    step("connected")

    def game(verb, what=None, **options):
        reply = sgcall.call(verb, what, **options)
        builder.trace(verb, what, options, reply, limit=400)
        return reply
    game.welcome = getattr(transport, "welcome", None) or {}

    def rewelcome():
        """A fresh connection's welcome: the first one can come before the game answered the hub."""
        game.welcome = getattr(sgcall.connect(pipe, hub), "welcome", None) or {}
        return game.welcome
    game.rewelcome = rewelcome
    step("waiting for the world's player")
    wait_world(game, timeout_s=world_wait_s)
    step("waiting for a running clock")
    wait_ready(game)
    step("ready")
    return game


def step(text):
    """A STEP line on stderr (the rig's build.log, which the rig watches): where the builder is."""
    print(f"STEP {time.strftime('%H:%M:%S')} {text}", file=sys.stderr, flush=True)


def wait_ready(game, timeout_s=300.0, sleep=time.sleep, clock=time.monotonic):
    """sgcall.wait_ready through the traced game: server info answers and the clock runs (unpausing it)."""
    end = clock() + timeout_s
    last = None
    while clock() < end:
        info = game("server", "info")
        if "error" not in info:
            now = game("read", "clock")
            if "error" not in now and not builder.field(now, "paused"):
                return now
            game("server", "command", command="pause false", confirm=True)
            last = now
        else:
            last = info
        sleep(3.0)
    raise SystemExit(f"not ready after {timeout_s:.0f} s: {last}")


def wait_world(game, timeout_s=300.0, sleep=time.sleep, clock=time.monotonic):
    """Waits until the world has a player's body: a client started with -newgame is at the menu first, and a
    dedicated server has none until a client joins. server info carries no game state, so the body is the sign."""
    end = clock() + timeout_s
    last = None
    while clock() < end:
        reply = game("find", "thing", runtime_type="Human", limit=1)
        if "error" not in reply and builder.field(reply, "things"):
            return builder.field(reply, "things")[0]
        last = reply.get("error") or "no player yet"
        sleep(5.0)
    raise SystemExit(f"no world with a player within {timeout_s:.0f} s (last: {last})")


def build(game, world, lift, wait_s=60.0, sleep=time.sleep, trace=False):
    """Builds and saves; returns the manifest. trace: each call and the start of its reply on stderr."""
    b = builder.Builder(game, sleep=sleep, trace_calls=trace)
    manifest = builder.WORLDS[world](b, lift=lift) if world != "sg-minimal" else builder.minimal(b)
    reply = b.console(f"save {world}")
    if reply.get("completes_asynchronously"):
        sleep(min(wait_s, 15.0))     # the save finishes on a background task; store checks the file is there
    manifest["built"]["calls"] = len(b.log)
    manifest["built"]["builder"] = "tools/test-worlds/builder.py " + world
    return manifest


SMOKE = "sg-smoke"


def smoke(game, sleep=time.sleep):
    """StationGod answers and the world has a player; saves as sg-smoke. Returns what it saw."""
    b = builder.Builder(game, sleep=sleep)
    b.do("server", "info")
    person = builder.one_player(b)
    b.console(f"save {SMOKE}")
    server = (getattr(game, "welcome", None) or {}).get("server") or {}
    if server.get("role") is None and hasattr(game, "rewelcome"):
        server = (game.rewelcome() or {}).get("server") or {}
    return {"role": server.get("role"), "dedicated": server.get("dedicated"), "pipe": server.get("pipe_name"),
            "player": builder.player_name(person), "player_at": builder.pos(person)}


def live_prefabs(b, saved):
    """{prefab: count} of the loaded world: a count per prefab for the save's big blocks (over 100 of one prefab),
    a walk of everything else."""
    big = sorted(p for p, n in saved.items() if n > 100)
    live = {p: builder.field(b.do("find", "thing", prefab=p, limit=1), "total") or 0 for p in big}
    offset = 0
    while True:
        reply = b.do("find", "thing", exclude_prefab=big, limit=500, offset=offset)
        rows = builder.field(reply, "things") or []
        for row in rows:
            live[row.get("prefab_name")] = live.get(row.get("prefab_name"), 0) + 1
        offset += len(rows)
        if not rows or not builder.field(reply, "has_more"):
            return live


def landing_problems(b, watch_s=6.0):
    """P51, for a run that loaded a stored world: nothing may play the landing. What is wrong: a seated player, or a
    lander still moving."""
    problems = []
    for row in b.players():
        if builder.seated(row):
            problems.append(f"the player arrived seated: {row.get('held_in')}")
    first = builder.lander_at(b)
    b.sleep(watch_s)
    second = builder.lander_at(b)
    if abs(first[1] - second[1]) > builder.STILL_M:
        problems.append(f"a lander descended: y {first[1]} then {second[1]}")
    return problems


def check(game, expect, saved=None, sleep=time.sleep):
    """A loaded test world: no landing played, StationGod answers server info and the world's thing count. Returns
    what it saw; ok when nothing landed and the count is the save's (expect). saved ({prefab: count} from the save
    file) adds diff: {prefab: [saved, live]} for each prefab that differs, so a short count names what is short."""
    b = builder.Builder(game, sleep=sleep)
    info = b.do("server", "info")
    landing = landing_problems(b)
    total = builder.field(b.do("find", "thing", limit=1), "total")
    seen = {"ok": total == expect and not landing, "things": total, "expected": expect, "landing": landing,
            "mod_version": builder.field(info, "mod_version")}
    if saved is not None:
        live = live_prefabs(b, saved)
        seen["diff"] = {p: [saved.get(p, 0), live.get(p, 0)] for p in sorted(set(saved) | set(live))
                        if saved.get(p, 0) != live.get(p, 0)}
    return seen


def saved_prefabs(saves, world):
    """{prefab: count} of a stored world's save file."""
    counts = {}
    for _kind, prefab, _block in twlib.things(twlib.read_world_xml(os.path.join(saves, world, world + ".save"))):
        if not twlib.is_organ(prefab):
            counts[prefab] = counts.get(prefab, 0) + 1
    return counts


def wait_save(saves, world, timeout_s=180.0, sleep=time.sleep, clock=time.monotonic):
    """Waits until <saves>/<world>/<world>.save exists and its size holds for two looks; returns its path."""
    path = os.path.join(saves, world, world + ".save")
    end = clock() + timeout_s
    last = -1
    while clock() < end:
        size = os.path.getsize(path) if os.path.isfile(path) else -1
        if size > 0 and size == last:
            return path
        last = size
        sleep(3.0)
    raise SystemExit(f"the save {path} was not written (or kept growing) within {timeout_s:.0f} s")


def write_done(folder, ok, reason=""):
    """done-ok, or done-fail holding the reason: the rig watcher's end marker."""
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, "done-ok" if ok else "done-fail"), "w", encoding="utf-8") as f:
        f.write(reason)


def main(argv=None):
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="step", required=True)
    a_build = sub.add_parser("build")
    a_build.add_argument("--world", required=True, choices=sorted(builder.WORLDS))
    a_build.add_argument("--pipe", required=True)
    a_build.add_argument("--hub", required=True)
    a_build.add_argument("--manifest", required=True)
    a_build.add_argument("--lift", type=float, default=4.0, help="metres above the settled ground for the floor")
    a_build.add_argument("--saves", help="the game's saves folder: wait there for the saved world")
    a_build.add_argument("--done", help="folder for the done-ok / done-fail end marker")
    a_smoke = sub.add_parser("smoke")
    a_smoke.add_argument("--pipe", required=True)
    a_smoke.add_argument("--hub", required=True)
    a_smoke.add_argument("--saves", required=True)
    a_smoke.add_argument("--done")
    a_check = sub.add_parser("check")
    a_check.add_argument("--pipe", required=True)
    a_check.add_argument("--hub", required=True)
    a_check.add_argument("--expect", type=int, required=True, help="the save's thing count")
    a_check.add_argument("--done")
    a_check.add_argument("--world", help="the loaded world's name (with --saves: compare it with its save by prefab)")
    a_check.add_argument("--saves", help="the game's saves folder")
    a_check.add_argument("--world-wait", type=float, default=900.0,
                         help="seconds to wait for the loaded world (a large save loads for minutes; the pipe answers "
                              "game_unavailable until then)")
    a_verify = sub.add_parser("verify", help="a stored world's save is landed (P51): no game")
    a_verify.add_argument("--world", required=True)
    a_verify.add_argument("--library", help="the world library (default: STATIONGOD_TEST_WORLDS)")
    a_store = sub.add_parser("store")
    a_store.add_argument("--world", required=True, choices=sorted(builder.WORLDS))
    a_store.add_argument("--save-folder", required=True)
    a_store.add_argument("--manifest", required=True)
    a_store.add_argument("--library", help="the world library (default: STATIONGOD_TEST_WORLDS)")
    try:
        a = ap.parse_args(argv)
    except SystemExit as refused:
        # Arguments that do not parse still end in a done file, so a rig run never waits on a builder that quit.
        given = list(sys.argv[1:] if argv is None else argv)
        if refused.code and "--done" in given[:-1]:
            write_done(given[given.index("--done") + 1], False, f"arguments refused: {' '.join(given)}")
        raise

    if a.step == "verify":
        problems = twlib.verify_stored(a.world, twlib.library_root(a.library))
        print(json.dumps({"world": a.world, "landed": not problems, "problems": problems}))
        return 1 if problems else 0

    if a.step == "check":
        if a.pipe == "StationGodMCP":
            raise SystemExit("refusing the live pipe StationGodMCP")
        try:
            saved = saved_prefabs(a.saves, a.world) if a.saves and a.world else None
            seen = check(connect(a.pipe, a.hub, a.world_wait), a.expect, saved)
            print(json.dumps(seen), flush=True)
        except BaseException as error:  # noqa: BLE001 - as build
            if a.done:
                write_done(a.done, False, f"{type(error).__name__}: {error}")
            raise
        if a.done:
            write_done(a.done, seen["ok"], json.dumps(seen))
        return 0 if seen["ok"] else 1

    if a.step == "smoke":
        if a.pipe == "StationGodMCP":
            raise SystemExit("refusing the live pipe StationGodMCP")
        try:
            print(json.dumps(smoke(connect(a.pipe, a.hub))), flush=True)
            print("saved " + wait_save(a.saves, SMOKE), flush=True)
        except BaseException as error:  # noqa: BLE001 - as build
            if a.done:
                write_done(a.done, False, f"{type(error).__name__}: {error}")
            raise
        if a.done:
            write_done(a.done, True, SMOKE)
        return 0

    if a.step == "build":
        if a.pipe == "StationGodMCP":
            raise SystemExit("refusing the live pipe StationGodMCP")
        try:
            manifest = build(connect(a.pipe, a.hub), a.world, a.lift, trace=True)
            with open(a.manifest, "w", encoding="utf-8") as f:
                json.dump(manifest, f, indent=1)
            print(json.dumps(manifest["fixtures"], indent=1), flush=True)
            if a.saves:
                print("saved " + wait_save(a.saves, a.world), flush=True)
        except BaseException as error:  # noqa: BLE001 - any end other than success is a done-fail for the watcher
            if a.done:
                write_done(a.done, False, f"{type(error).__name__}: {error}")
            raise
        if a.done:
            write_done(a.done, True, a.world)
        return 0

    with open(a.manifest, encoding="utf-8") as f:
        manifest = json.load(f)
    library = twlib.library_root(a.library)
    size = twlib.store(a.world, a.save_folder, library, manifest, builder.SPECS[a.world])
    print(f"{a.world}: {size['structures']} structures, {size['things']} things -> {os.path.join(library, a.world)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
