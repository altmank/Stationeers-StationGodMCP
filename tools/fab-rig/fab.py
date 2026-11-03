"""write fabricator's in-game proof, through the sg client in a rendered client that has a landed save loaded.
Runs in the guest, started by vm/onpoll.ps1:

    python fab.py --pipe <test pipe> --hub <hub dir or exe> --done <out folder>

It builds a Tool Manufactory and a large battery on a floating floor of frames beside the player, powers the
fabricator from the battery, then steps the fabricator through its build states and calls `write fabricator` at each.
Every reply goes to replies.json beside the done file (`done-ok`, or `done-fail` holding the reason):

  1. build (free): the fabricator and the battery; the battery full and on, cabled to the fabricator; the fabricator off.
  2. each build state in turn: a `write fabricator` report, the tier logged; at the first finished TierOne state a
     confirm must answer wrong_tier; at the last state the tier must be TierTwo.
  3. off, an ingot in the output slot: confirm answers fabricator_busy (output_occupied); the ingot is moved away.
  4. off, a full load: confirm answers fabricator_unpowered (off); the stock is unchanged.
  5. on and powered, no load: confirm answers missing_reagents (Iron 3, Electrum 3).
  6. a load of 5 iron and 4 electrum: one ItemMKIIWrench in slot 1, consumed = recipe x the material multiplier,
     stock_after equal to `inspect reagents` read straight after; the made id is found.
  7. refusals: the battery (not_a_fabricator), ItemIronIngot (not_a_recipe), a load of ItemSteelSheets (not_accepted).
"""
import argparse
import json
import math
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "silo-rig"))
import sgcall  # noqa: E402

FAB = "StructureToolManufactory"
BATTERY = "StructureBatteryLarge"
RECIPE = "ItemMKIIWrench"
NEED = {"Iron": 3.0, "Electrum": 3.0}
LOAD = [{"prefab": "ItemIronIngot", "quantity": 5}, {"prefab": "ItemElectrumIngot", "quantity": 4}]
MAX_STATES = 8
LIFT = 12.0


def say(text):
    print(text, flush=True)


class Fail(Exception):
    pass


def odd(v):
    n = int(math.floor(v))
    return n if n % 2 else n + 1


def code_of(reply):
    return (reply.get("error") or {}).get("code") if isinstance(reply, dict) else None


def data_of(reply):
    return (reply.get("error") or {}).get("data") or {}


def must(reply, what):
    if code_of(reply):
        raise Fail(f"{what} refused: {reply['error']}")
    return reply


def one(reply):
    """A one-id reply's row: the reply itself, or the single row of a results list."""
    rows = reply.get("results") if isinstance(reply, dict) else None
    if isinstance(rows, list) and len(rows) == 1 and isinstance(rows[0], dict):
        return rows[0]
    return reply


def expect(log, key, reply, code, **data):
    """The reply must be the refusal `code`, with these data keys equal."""
    log[key] = reply
    if code_of(reply) != code:
        raise Fail(f"{key}: expected {code}, got {reply.get('error') or 'a reply'}")
    for name, want in data.items():
        got = data_of(reply).get(name)
        if got != want:
            raise Fail(f"{key}: data.{name} is {got!r}, expected {want!r}")
    say(f"{key}: {code} {data_of(reply)}")


def wait_player(timeout_s=900.0):
    end = time.monotonic() + timeout_s
    while time.monotonic() < end:
        reply = sgcall.call("find", "thing", runtime_type="Human", limit=1)
        things = reply.get("things") if isinstance(reply, dict) else None
        if things:
            return things[0]
        time.sleep(5.0)
    raise Fail(f"no player within {timeout_s:.0f} s")


# ---- the build: the few helpers it needs ----

def placed_ids(job):
    for holder in (job, job.get("result") or {}):
        rows = holder.get("placed")
        if isinstance(rows, list):
            return [str(r["reference_id"]) if isinstance(r, dict) else str(r) for r in rows]
    return []


def finish(reply, what, timeout_s=300.0):
    end = time.monotonic() + timeout_s
    final = {"applied", "applied_with_differences", "applied_unchecked", "stopped", "gas_lost", "refused"}
    while reply.get("status") not in final:
        job = reply.get("job_id")
        if job is None or time.monotonic() > end:
            raise Fail(f"{what}: no final status: {reply}")
        time.sleep(1.0)
        reply = must(sgcall.call("inspect", "job", id=job), "inspect job")
    if reply["status"] != "applied":
        raise Fail(f"{what} ended {reply['status']}: {reply}")
    return reply


def build_structures(placements):
    report = must(sgcall.call("build", "structure", placements=placements, free=True), "build structure report")
    if not report.get("ready"):
        raise Fail(f"build structure not ready: {report.get('problems')}")
    return finish(must(sgcall.call("build", "structure", placements=placements, free=True, confirm=True),
                       "build structure"), "build structure")


def label_id(label, prefab):
    rows = must(sgcall.call("find", "thing", name=label, limit=16), "find thing").get("things") or []
    rows = [t for t in rows if label in (t.get("custom_name"), t.get("display_name")) and
            t.get("prefab_name") == prefab]
    if len(rows) != 1:
        raise Fail(f"{label}: found {len(rows)} {prefab}")
    return str(rows[0]["reference_id"])


def power_port(thing_id, prefer):
    ends = must(sgcall.call("inspect", "connections", id=thing_id), "inspect connections").get("ends") or []
    ends = [e for e in ends if "power" in str(e.get("type_name") or e.get("type")).lower()]
    if not ends:
        raise Fail(f"{thing_id} has no power port")
    for e in ends:
        if prefer and prefer in str(e.get("role_name") or e.get("role") or "").lower():
            return e["index"]
    return ends[0]["index"]


def cable(start, end):
    plan = must(sgcall.call("plan", "route", kind="cable", grade="heavy", start=start, end=end, summarize=True),
                "plan route")
    if not plan.get("found"):
        raise Fail(f"plan route: {plan.get('failure')}")
    args = dict(plan.get("build_arguments") or {})
    args.pop("what", None)
    args.pop("materials", None)
    report = must(sgcall.call("build", "cable", free=True, **args), "build cable report")
    if not report.get("ready"):
        raise Fail(f"build cable not ready: {report.get('problems')}")
    return finish(must(sgcall.call("build", "cable", free=True, confirm=True, **args), "build cable"), "build cable")


def switch_on(thing_id):
    reply = must(sgcall.call("write", "logic", id=[thing_id], logic="On", value=1, confirm=True), "switch on")
    if not (reply.get("results") or [{}])[0].get("ok"):
        raise Fail(f"{thing_id} has no On: {reply}")


def wait_powered(thing_id, timeout_s=90.0):
    end = time.monotonic() + timeout_s
    while time.monotonic() < end:
        if sgcall.call("read", "logic", id=thing_id, logic="Power").get("value") == 1:
            return
        time.sleep(2.0)
    raise Fail(f"{thing_id} never read Power 1")


def stand_site(log):
    """The fabricator and the battery on a floor of frames beside the player (a 2 m cell grid, odd cell centres)."""
    player = wait_player()
    p = player["position"]
    x0, floor_y, z0 = odd(p["x"] + 4), odd(p["y"] + LIFT), odd(p["z"] + 4)
    width, depth = 9, 5
    frames = [{"prefab": "StructureFrame", "at": [x0 + 2 * i, floor_y, z0 + 2 * k]}
              for k in range(depth) for i in range(width)]
    for i in range(0, len(frames), 64):
        build_structures(frames[i:i + 64])
    machines = [
        {"prefab": FAB, "at": [x0 + 2 * 2, floor_y + 2, z0 + 2 * 1], "label": "FAB machine"},
        {"prefab": BATTERY, "at": [x0 + 2 * 6, floor_y + 2, z0 + 2 * 1], "label": "FAB battery"},
    ]
    build_structures(machines)
    ids = {"fab": label_id("FAB machine", FAB), "battery": label_id("FAB battery", BATTERY)}
    log["ids"] = ids
    must(sgcall.call("write", "battery", id=[ids["battery"]], ratio=1.0, confirm=True), "battery full")
    switch_on(ids["battery"])
    cable({"id": ids["battery"], "port": power_port(ids["battery"], "output")},
          {"id": ids["fab"], "port": power_port(ids["fab"], "")})
    return ids


# ---- the proof ----

def fabricate(ids, **options):
    return sgcall.call("write", "fabricator", id=ids["fab"], prefab=RECIPE, **options)


def tier_of(reply):
    return reply.get("tier") if "error" not in reply else data_of(reply).get("fabricator_tier")


def step_states(log, ids):
    """Walks the fabricator's build states: a report at each, the tier logged; the tier at the first finished TierOne
    state must refuse a confirm as wrong_tier, and the last state must be TierTwo."""
    states = []
    tested_tier_one = False
    last_tier = None
    for n in range(MAX_STATES):
        report = fabricate(ids)
        tier = tier_of(report)
        states.append({"n": n, "tier": tier, "code": code_of(report)})
        log[f"state_{n}"] = report
        say(f"state {n}: tier {tier}, report {code_of(report) or 'ready'}")
        last_tier = tier
        if tier == "TierOne" and code_of(report) in (None, "fabricator_unpowered") and not tested_tier_one:
            tested_tier_one = True
            confirm = fabricate(ids, confirm=True)
            log["tier_one_confirm"] = confirm
            if code_of(confirm) != "wrong_tier":
                raise Fail(f"a TierOne state's confirm answered {code_of(confirm)}, not wrong_tier")
            say(f"tier one: wrong_tier {data_of(confirm)}")
        step = one(sgcall.call("build", "finish", id=ids["fab"], free=True, confirm=True))
        log[f"finish_{n}"] = step
        if code_of(step) == "already_complete" or (step.get("build_state") or {}).get("complete"):
            if code_of(step) != "already_complete":
                states.append({"n": n + 1, "tier": tier_of(fabricate(ids)), "note": "after the last finish"})
                last_tier = states[-1]["tier"]
            break
        must(step, f"build finish {n}")
    log["states"] = states
    if not tested_tier_one:
        say("no state read TierOne: the wrong_tier game check is skipped (the unit tests cover it)")
    if last_tier != "TierTwo":
        raise Fail(f"the last state's tier is {last_tier}, not TierTwo: states {states}")
    return states


def stock_of(thing_id):
    reply = must(sgcall.call("inspect", "reagents", id=thing_id), "inspect reagents")
    row = (reply.get("reagents") or [])
    return {r["reagent"]: round(r["quantity"], 6) for r in row}


def run(log):
    sgcall.wait_ready()
    ids = stand_site(log)
    step_states(log, ids)

    # 3. output slot taken
    ingot = must(sgcall.call("build", "item", prefab="ItemIronIngot", quantity=1, to=ids["fab"], slot=1, confirm=True),
                 "ingot into the output slot")
    log["ingot_in_output"] = ingot
    item = (ingot.get("placed") or [None])[0]
    expect(log, "busy", fabricate(ids, load=LOAD, confirm=True), "fabricator_busy", reason="output_occupied")
    if item is not None:
        must(sgcall.call("move", "item", id=item, to="player"), "move the ingot away")

    # 4. off, a full load
    before = stock_of(ids["fab"])
    expect(log, "off", fabricate(ids, load=LOAD, confirm=True), "fabricator_unpowered", reason="off")
    after = stock_of(ids["fab"])
    log["stock_off"] = {"before": before, "after": after}
    if before != after:
        raise Fail(f"a refused call changed the stock: {before} -> {after}")

    # 5. on and powered, no load
    switch_on(ids["fab"])
    wait_powered(ids["fab"])
    missing = fabricate(ids, confirm=True)
    expect(log, "missing", missing, "missing_reagents")
    if data_of(missing).get("missing") != NEED:
        raise Fail(f"missing is {data_of(missing).get('missing')}, expected {NEED}")

    # 6. the make
    made = fabricate(ids, load=LOAD, confirm=True)
    log["make"] = made
    must(made, "the make")
    rows = made.get("made") or []
    if len(rows) != 1 or rows[0].get("prefab_name") != RECIPE or rows[0].get("slot") != 1:
        raise Fail(f"made is {rows}, expected one {RECIPE} in slot 1")
    m = made["multipliers"]["material"]
    want = {k: round(v * m, 6) for k, v in NEED.items()}
    if made.get("consumed") != want:
        raise Fail(f"consumed {made.get('consumed')}, expected {want} (material multiplier {m})")
    read_back = stock_of(ids["fab"])
    log["stock_read_back"] = read_back
    if {k: round(v, 6) for k, v in made["stock_after"].items()} != read_back:
        raise Fail(f"stock_after {made['stock_after']} differs from inspect reagents {read_back}")
    found = sgcall.call("find", "thing", made_by=ids["fab"], limit=8)
    log["found"] = found
    ids_found = [str(t.get("reference_id")) for t in (found.get("things") or [])]
    if rows[0]["reference_id"] not in ids_found:
        contents = sgcall.call("inspect", "contents", id=ids["fab"], depth=1)
        log["contents"] = contents
        if rows[0]["reference_id"] not in json.dumps(contents):
            raise Fail(f"the made item {rows[0]['reference_id']} was found neither by made_by nor in the contents")
    say(f"made {rows[0]['reference_id']} {RECIPE}, consumed {made['consumed']}, multipliers {made['multipliers']}")

    # 7. refusals
    battery = sgcall.call("write", "fabricator", id=ids["battery"], prefab=RECIPE, confirm=True)
    expect(log, "not_a_fabricator", battery, "not_a_fabricator")
    expect(log, "not_a_recipe", sgcall.call("write", "fabricator", id=ids["fab"], prefab="ItemIronIngot", confirm=True),
           "not_a_recipe")
    expect(log, "not_accepted", fabricate(ids, load=[{"prefab": "ItemSteelSheets", "quantity": 1}], confirm=True),
           "not_accepted")
    return {"made": rows[0]["reference_id"], "multipliers": made["multipliers"], "consumed": made["consumed"],
            "tiers": [s.get("tier") for s in log["states"]]}


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--pipe", required=True)
    ap.add_argument("--hub", required=True)
    ap.add_argument("--done", required=True)
    a = ap.parse_args(argv)
    os.makedirs(a.done, exist_ok=True)
    log = {}
    ok, reason = False, ""
    try:
        sgcall.connect(a.pipe, a.hub)
        summary = run(log)
        log["summary"] = summary
        ok = True
        reason = json.dumps(summary, default=str)
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
