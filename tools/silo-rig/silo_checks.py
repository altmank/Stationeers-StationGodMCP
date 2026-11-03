"""SDB Silo checks in a running TEST game (a test world's copy): inspect contents, find item, move item in and out.

    py -3.12 silo_checks.py --pipe <test pipe> --hub <hub dir or StationGodMCP.Server.dll>
                            [--fixtures <world library>\\sg-silo\\fixtures.json] [--done <folder>]

It changes the world it runs in (moves items between silos, a locker, a suit and the ground), so run it on a fresh
copy of a test world, never a save anyone plays. It refuses the live pipe (StationGodMCP). Built for the sg-silo test
world (tools/test-worlds; vm-run-silo-checks.ps1 runs it on a VM rig). With --fixtures it takes what it uses from the
world's fixtures.json by role; without, it finds them by prefab:

- silo_ore: a silo of ore stacks with at least 5 entries (pooled withdrawals, the DispenseSlot refusal, no_room, ground);
- silo_room: a silo with room for 6 entries (deposits, the backpack round trip, the food);
- silo_full: a full or nearly full silo, 600 minus at most the number of spare things below (silo_full). With
  --fixtures and no silo_full fixture, that check SKIPs and says so;
- locker: an empty locker (withdrawals land there); suit: a suit the player carries, with no ore slot (no_room);
- backpack_full and backpacks_spare: a mining backpack holding ore, and spare things; tray: a plant growing (refused);
- food_fresh: two fresh stacks of food that rots (ItemCookedPumpkin). They go into silo_room; one comes out by a
  withdrawal, the other by the silo's own export (DispenseSlot), and the two must come out the same: whole and spoiled.

Each check prints PASS, FAIL (with what came back) or SKIP (with why); the exit code is the number of FAILs. With
--done, it also writes done-ok (no FAIL) or done-fail into that folder, holding every result line.
Not covered here: a client watching.
"""
import argparse
import json
import sys
import time

import sgcall

RESULTS = []
LINES = []


def record(name, ok, detail=""):
    state = "SKIP" if ok is None else "PASS" if bool(ok) else "FAIL"
    RESULTS.append((name, state))
    line = f"{state:4}  {name}" + (f"  -- {detail}" if detail else "")
    LINES.append(line)
    print(line, flush=True)


def progress(text):
    """A line before each check and during each wait, so the checks' log shows a healthy run is moving."""
    print(f"...   {text}", flush=True)


def summary_line():
    """The last line of a run (and the checks phase vm-hooks\\silo-vm-lib.ps1 Get-SiloPhases matches as summary)."""
    fails = sum(1 for r in RESULTS if r[1] == "FAIL")
    return f"{len(RESULTS)} checks: {len(RESULTS) - fails} passed or skipped, {fails} failed"


def short(reply, limit=600):
    text = json.dumps(reply)
    return text if len(text) <= limit else text[:limit] + "..."


def call(verb, what, **options):
    return sgcall.call(verb, what, **options)


def error_code(reply):
    return (reply.get("error") or {}).get("code")


def silo_view(silo_id, limit=50):
    reply = call("inspect", "contents", id=silo_id, limit=limit)
    return reply["results"][0]["silo"]


def quantity_logic(thing_id):
    reply = call("read", "logic", id=thing_id, logic=["Quantity"])
    return reply["results"][0]["value"]


def slots_of(holder_id, depth=1):
    return call("inspect", "contents", id=holder_id, depth=depth)["results"][0]["slots"]


def pick_things():
    silos = call("find", "thing", prefab="StructureSDBSilo", limit=50,
                 fields=["reference_id", "custom_name"])["things"]
    views = {s["reference_id"]: silo_view(s["reference_id"], limit=1) for s in silos}
    ore_silo = next((sid for sid, v in views.items() if v["count"] >= 5 and v["entries"]
                     and v["entries"][0]["prefab_name"].endswith("Ore") and v["entries"][0]["children"] == 0
                     and v["count"] < 590), None)
    roomy = next((sid for sid, v in views.items() if v["count"] <= 594 and sid != ore_silo), None)
    full = max(views, key=lambda sid: views[sid]["count"])
    lockers = call("find", "thing", prefab="StructureStorageLocker", limit=50,
                   fields=["reference_id", "custom_name"])["things"]
    locker = next((l["reference_id"] for l in lockers if not slots_of(l["reference_id"])), None)
    suit = (call("find", "item", prefab="ItemHardSuit", location="player", limit=1,
                 fields=["reference_id"])["items"] or [{}])[0].get("reference_id")
    packs = call("find", "item", prefab="ItemHardMiningBackPack", limit=50,
                 fields=["reference_id"])["items"]
    pack_ids = [p["reference_id"] for p in packs]
    full_pack = next((p for p in pack_ids if slots_of(p)), None)
    plant = (call("find", "item", prefab="ItemPumpkin", limit=1, fields=["reference_id"])["items"] or [{}])[0] \
        .get("reference_id")
    return {"ore_silo": ore_silo, "roomy_silo": roomy, "full_silo": full, "full_count": views[full]["count"],
            "locker": locker, "suit": suit, "full_pack": full_pack,
            "spare_packs": [p for p in pack_ids if p != full_pack], "plant": plant, "food": None}


def ids_of(fixture):
    """A fixture's ids as a list of strings (a fixture's id is one id, a list of them, or null)."""
    got = (fixture or {}).get("id")
    return [str(i) for i in (got if isinstance(got, list) else [got]) if i is not None]


def from_fixtures(fixtures):
    """What the checks use, by the test world's fixture roles (fixtures.json's "fixtures"). A role the world lacks is
    None; the checks that need it SKIP."""
    def one(role):
        got = ids_of(fixtures.get(role))
        return got[0] if got else None

    plant = None
    tray = one("tray")
    if tray:
        plant = next((p["reference_id"] for p in call("find", "plant")["plants"]
                      if p.get("planted") and (p.get("tray") or {}).get("reference_id") == tray), None)
    full = one("silo_full")
    return {"ore_silo": one("silo_ore"), "roomy_silo": one("silo_room"), "full_silo": full,
            "full_count": silo_view(full, limit=1)["count"] if full else None,
            "locker": one("locker"), "suit": one("suit"), "full_pack": one("backpack_full"),
            "spare_packs": ids_of(fixtures.get("backpacks_spare")), "plant": plant,
            "food": ids_of(fixtures.get("food_fresh"))}


FOOD = "ItemCookedPumpkin"   # sg-silo's fresh food (fixture food_fresh); decays, spoils into DecayedFood
SPOILED = "DecayedFood"


def food_items():
    """Every loose or held thing of the food or its spoiled form, by id: prefab, quantity, condition, decay left."""
    found = {}
    for prefab in (FOOD, SPOILED):
        for row in call("find", "item", prefab=prefab, limit=200,
                        fields=["reference_id", "prefab_name", "quantity", "condition", "held_in"])["items"]:
            found[row["reference_id"]] = row
    for row in call("find", "food")["food"]:
        if row["reference_id"] in found:
            found[row["reference_id"]]["decay_time_left_s"] = row.get("decay_time_left_s")
    return found


def outcome(row):
    """What a thing out of a silo is, as compared between a withdrawal and the silo's own export."""
    return {"prefab_name": row.get("prefab_name"), "quantity": row.get("quantity"),
            "condition": row.get("condition"), "decaying": row.get("decay_time_left_s") is not None}


def pick_fresh(found, ids=None):
    """Two stacks of the food, by fixture id when given, else any of its prefab not already in the locker. By prefab,
    not by decay time left: that is null wherever the decay rate is 0 (in a vacuum), and the food still rots on the
    way out of a silo (the export's rule is CanDecay, not the rate)."""
    rows = [found[i] for i in ids if i in found] if ids else list(found.values())
    return [r for r in rows if r.get("prefab_name") == FOOD][:2]


def food_checks(roomy, locker, fresh_ids=None, settle_s=3.0, export_wait_s=20.0):
    """Stored food that rots: in as an entry marked rots_when_taken, out whole and spoiled, as the silo's export."""
    names = ("deposit food that rots (marked rots_when_taken)",
             "withdraw food that rots: whole, spoiled as the silo's export lets it out")
    fresh = pick_fresh(food_items(), fresh_ids)
    if len(fresh) < 2:
        where = f"of fixture food_fresh {fresh_ids}" if fresh_ids else "in the world"
        for name in names:
            record(name, None, f"{len(fresh)} {FOOD} stacks {where}; the check needs 2 (sg-silo has them)")
        return
    quantity = fresh[0]["quantity"]
    dep = call("move", "item", to=roomy, id=[r["reference_id"] for r in fresh[:2]])
    rows = (dep.get("items") or {}).get("results", [])
    stored = [e for e in silo_view(roomy, limit=50).get("entries", []) if e["prefab_name"] == FOOD] \
        if all(r.get("ok") for r in rows) else []
    # silo_view pages 50 entries from the front; the deposits are at the back, so page to them.
    if not stored and all(r.get("ok") for r in rows):
        count = silo_view(roomy, limit=1)["count"]
        stored = [e for e in call("inspect", "contents", id=roomy, offset=max(0, count - 10), limit=10)
                  ["results"][0]["silo"]["entries"] if e["prefab_name"] == FOOD]
    record(names[0], len(rows) == 2 and all(r.get("ok") for r in rows) and len(stored) == 2
           and all(e["rots_when_taken"] for e in stored), short(stored or dep))
    if len(stored) != 2:
        record(names[1], None, "the food did not go in")
        return

    progress("food: withdrawal, then the silo's own export")
    # StationGod's withdrawal of the first entry, then the silo's own export of the second (an IC's DispenseSlot).
    taken = call("move", "item", source=roomy, prefab=FOOD, quantity=quantity, to=locker, confirm=True)
    made = (taken.get("placed") or [{}])[0].get("reference_id")
    # Settle first: a thing that spoils may be swapped for its spoiled form (a new id) in the withdrawal's slot, and that
    # new id must be in `before`, so the export below can never be our own withdrawn item.
    time.sleep(settle_s)
    before = set(food_items())
    left = [e for e in call("inspect", "contents", id=roomy, prefab=FOOD, limit=10)["results"][0]["silo"]["entries"]]
    if not left:
        record(names[1], False, f"no {FOOD} entry left to export after the withdrawal: {short(taken)}")
        return
    call("write", "logic", id=roomy, logic="DispenseSlot", value=left[0]["index"], confirm=True)
    exported = None
    end = time.monotonic() + export_wait_s
    while exported is None and time.monotonic() < end:
        time.sleep(1.0)
        progress("waiting for the silo's export")
        exported = pick_exported(food_items(), before, locker)
    time.sleep(settle_s)
    now = food_items()
    ours = pick_withdrawn(now, made, locker)
    if exported is not None:
        exported = now.get(exported["reference_id"], exported)
    if ours is None or exported is None:
        record(names[1], False, f"withdrawn {short(ours)}; exported {short(exported)}; reply {short(taken)}")
        return
    if ours["reference_id"] == exported["reference_id"]:
        record(names[1], False, f"the export found is the withdrawn item itself ({ours['reference_id']}): compared "
                                f"nothing; reply {short(taken)}")
        return
    got, want = outcome(ours), outcome(exported)
    record(names[1], same_as_export(taken, got, want), f"withdrawal {got}; silo export {want}")


def held_by(row, holder):
    return any(h.get("reference_id") == holder for h in row.get("held_in") or [])


def pick_withdrawn(items, made, locker):
    """What the withdrawal made: its id, or, once it spoiled into a new thing, the food or spoiled food in the locker."""
    return items.get(made) or next((r for r in items.values() if held_by(r, locker)), None)


def pick_exported(items, before, locker):
    """What the silo's export let out: a food or spoiled food that was not there before the dispense and is not in the
    locker (the withdrawal's own item, whatever its id now)."""
    return next((r for rid, r in items.items() if rid not in before and not held_by(r, locker)), None)


def same_as_export(taken, got, want):
    """The withdrawal took the entry whole and what it made is what the silo's own export made of the same food: same
    prefab, quantity and condition, and either the spoiled prefab or no longer the fresh food in good condition."""
    whole = (taken.get("taken") or [{}])[0].get("whole") is True
    spoiled = got["prefab_name"] == SPOILED or got["condition"] in ("damaged", "broken")
    return whole and got == want and spoiled


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pipe", required=True)
    ap.add_argument("--hub", required=True)
    ap.add_argument("--fixtures", help="the test world's fixtures.json: take what the checks use by role")
    ap.add_argument("--done", help="a folder to write done-ok or done-fail into, holding every result line")
    ap.add_argument("--world-timeout", type=int, default=600,
                    help="seconds to wait for the world to run before failing with 'world never loaded'")
    a = ap.parse_args()
    try:
        fails = run(a)
    except BaseException as e:   # the done file must say how the run ended, whatever stopped it
        record("run", False, f"{type(e).__name__}: {e}")
        fails = len([r for r in RESULTS if r[1] == "FAIL"])
        if not isinstance(e, Exception):
            write_done(a.done, fails)
            raise
    write_done(a.done, fails)
    return fails


def write_done(folder, fails):
    if not folder:
        return
    import os
    os.makedirs(folder, exist_ok=True)
    # Written under a name the runner's done-* glob does not match, then renamed: the runner never reads a part file.
    part = os.path.join(folder, "part-done")
    with open(part, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(LINES + [done_trailer(len(RESULTS), fails)]) + "\n")
    os.replace(part, os.path.join(folder, "done-ok" if fails == 0 else "done-fail"))


def done_trailer(checks, fails):
    """The done file's last line, which vm-hooks\\silo-vm-lib.ps1 Get-SiloVerdict requires."""
    return f"{checks} checks, {fails} failed"


WORLD_CHECK = "world loaded"


def wait_for_world(timeout_s):
    """The game's clock once the world runs, or None after recording a FAIL "world never loaded" (with the last
    reply), so a client left at the menu ends the run at once instead of every check failing on the pipe."""
    last = {}

    def waiting(reply):
        last["reply"] = reply
        progress(f"waiting for the world: {short(reply, 200)}")
    try:
        return sgcall.wait_ready(timeout_s=timeout_s, on_wait=waiting)
    except SystemExit:
        record(WORLD_CHECK, False, f"world never loaded within {timeout_s} s; last reply {short(last.get('reply'), 300)}")
        return None


def run(a):
    progress(f"connecting to {a.pipe}, then waiting for the world to run")
    sgcall.connect(a.pipe, a.hub)
    clock = wait_for_world(a.world_timeout)
    if clock is None:
        return len([r for r in RESULTS if r[1] == "FAIL"])
    progress(f"world running: {clock}")
    fixtures = None
    if a.fixtures:
        with open(a.fixtures, encoding="utf-8") as f:
            fixtures = json.load(f)["fixtures"]
    t = from_fixtures(fixtures) if fixtures is not None else pick_things()
    print("using", json.dumps(t), flush=True)
    ore_silo, roomy, locker = t["ore_silo"], t["roomy_silo"], t["locker"]
    if not (ore_silo and roomy and locker):
        record("setup", False, "no ore silo, roomy silo or empty locker in this world")
        return len([r for r in RESULTS if r[1] == "FAIL"])

    progress("inspect contents on a silo")
    # inspect contents on a silo: the store, agreeing with the silo's own count (logic Quantity).
    view = silo_view(ore_silo, limit=3)
    entry = view["entries"][0]
    record("inspect contents on a silo",
           view["entries_known"] and view["count"] == quantity_logic(ore_silo) and view["capacity"] == 600
           and entry["index"] == 0 and entry["reference_id"] is None and "rots_when_taken" in entry,
           short(view))
    ore = entry["prefab_name"]

    progress("find item location silo")
    # find item at location silo: every entry, held in its silo at slot -1, not movable.
    places = call("find", "item", location="silo", group_by="place")
    within = call("find", "item", location="silo", within=ore_silo, limit=2)
    row = within["items"][0]
    record("find item location silo",
           places["silo_entries"] >= view["count"] and within["total"] == view["count"]
           and row["held_in"][0]["reference_id"] == ore_silo and row["held_in"][0]["slot_index"] == -1
           and row["silo"]["movable"] is False and row["silo"]["entry"] == 0,
           f"silo_entries {places['silo_entries']}, within total {within['total']}")

    progress("withdraw pooled stacks")
    # Withdrawal, pooled stacks: a report first, then the move; 120 items into the empty locker.
    report = call("move", "item", source=ore_silo, prefab=ore, quantity=120, to=locker)
    taken = call("move", "item", source=ore_silo, prefab=ore, quantity=120, to=locker, confirm=True)
    placed = taken.get("placed", [])
    record("withdraw pooled stacks (report, then confirm)",
           report.get("report_only") is True and taken.get("stock", {}).get("change") == -120
           and sum(p["quantity"] for p in placed) == 120 and all(p["where"] in ("slot", "merged") for p in placed)
           and taken["count"]["after"] == quantity_logic(ore_silo),
           short(taken))

    progress("deposits into the roomy silo")
    # Deposit part of a stack, then a whole thing, into the roomy silo.
    first, second = placed[0]["reference_id"], placed[1]["reference_id"]
    before = silo_view(roomy, limit=1)["count"]
    part = call("move", "item", to=roomy, items=[{"id": first, "quantity": 10}])
    res = (part.get("items") or {}).get("results", [{}])[0]
    record("deposit part of a stack",
           res.get("ok") and res.get("quantity") == 10 and res.get("left_in_source") == placed[0]["quantity"] - 10
           and part["count"]["after"] == before + 1, short(part))
    whole = call("move", "item", to=roomy, id=[second])
    res = (whole.get("items") or {}).get("results", [{}])[0]
    record("deposit a whole thing", res.get("ok") and res.get("left_in_source") == 0
           and whole["count"]["after"] == before + 2 and quantity_logic(roomy) == before + 2, short(whole))

    progress("backpack round trip")
    # A backpack with contents: in as one entry with its children, out whole with them.
    pack = t["full_pack"]
    if pack:
        inside = len(slots_of(pack))
        dep = call("move", "item", to=roomy, id=[pack])
        res = (dep.get("items") or {}).get("results", [{}])[0]
        index = res.get("entry")
        stored = [e for e in silo_view(roomy)["entries"] if e["index"] == index]
        record("deposit a backpack with contents",
               res.get("ok") and res.get("children") == inside and stored and stored[0]["children"] == inside
               and stored[0]["contents"], short(dep))
        out = call("move", "item", source=roomy, prefab="ItemHardMiningBackPack", quantity=1, to=locker,
                   confirm=True)
        new_id = (out.get("placed") or [{}])[0].get("reference_id")
        record("withdraw a whole backpack with contents",
               out.get("taken", [{}])[0].get("whole") is True and new_id and len(slots_of(new_id)) == inside,
               short(out))
    else:
        record("deposit a backpack with contents", None, "no mining backpack holding anything")
        record("withdraw a whole backpack with contents", None, "no mining backpack holding anything")

    progress("a growing plant")
    # A growing plant is refused.
    if t["plant"]:
        planted = call("move", "item", to=roomy, id=[t["plant"]])
        res = (planted.get("items") or {}).get("results", [{}])[0]
        record("deposit refuses a growing plant", (res.get("error") or {}).get("code") == "planted", short(planted))
    else:
        record("deposit refuses a growing plant", None, "no pumpkin growing")

    progress("no_room and allow ground")
    # no_room into a holder with no slot for the item, then allow ground.
    if t["suit"]:
        count_before = silo_view(ore_silo, limit=1)["count"]
        refused = call("move", "item", source=ore_silo, prefab=ore, quantity=60, to=t["suit"], confirm=True)
        record("withdraw no_room (nothing changed)", error_code(refused) == "no_room"
               and quantity_logic(ore_silo) == count_before, short(refused))
        grounded = call("move", "item", source=ore_silo, prefab=ore, quantity=60, to=t["suit"], allow=["ground"],
                        confirm=True)
        record("withdraw allow ground", grounded.get("placed")
               and all(p["where"] == "ground" for p in grounded["placed"])
               and sum(p["quantity"] for p in grounded["placed"]) == 60, short(grounded))
    else:
        record("withdraw no_room (nothing changed)", None, "no suit on a player")
        record("withdraw allow ground", None, "no suit on a player")

    progress("DispenseSlot refusal")
    # A dispense an IC asked for is kept: a withdrawal taking that entry or one before it is refused.
    # The silo exports the asked entry as soon as its export slot is free, so the dispense may be gone before the
    # withdrawal is checked; the check is only meaningful while the silo still shows it pending.
    outcome = None
    for _ in range(5):
        count = silo_view(ore_silo, limit=1)["count"]
        if count < 2:
            break
        call("write", "logic", id=ore_silo, logic="DispenseSlot", value=count - 1, confirm=True)
        busy = call("move", "item", source=ore_silo, prefab=ore, quantity=1, to=locker)
        codes = [p.get("code") for p in busy.get("problems", [])] + [error_code(busy)]
        if "silo_busy" in codes:
            outcome = (True, short(busy))
            break
        pending = silo_view(ore_silo, limit=1)["dispense_slot"]
        if pending >= 0:
            outcome = (False, f"dispense_slot {pending} still pending, yet: {short(busy)}")
            break
        outcome = (None, "the silo exported the asked entry before each withdrawal was checked")
    if outcome is None:
        outcome = (None, "ore silo has fewer than 2 entries left")
    record("withdraw refused for an IC's DispenseSlot", *outcome)

    progress("silo_full")
    # silo_full: past 600 entries each further thing is refused.
    full_silo = t["full_silo"]
    spare = t["spare_packs"]
    room = 600 - silo_view(full_silo, limit=1)["count"] if full_silo else None
    if full_silo is None:
        record("deposit silo_full past 600 entries", None, "no silo_full fixture in fixtures.json")
    elif full_silo not in (ore_silo, roomy) and len(spare) > room:
        dep = call("move", "item", to=full_silo, id=spare[:room + 1])
        rows = (dep.get("items") or {}).get("results", [])
        record("deposit silo_full past 600 entries",
               sum(1 for r in rows if r.get("ok")) == room
               and (rows[-1].get("error") or {}).get("code") == "silo_full"
               and dep["count"]["after"] == 600 and quantity_logic(full_silo) == 600, short(rows[-1]))
    else:
        record("deposit silo_full past 600 entries", None, f"{room} entries free, {len(spare)} spare things")

    food_checks(roomy, locker, t.get("food"))

    fails = [r for r in RESULTS if r[1] == "FAIL"]
    print("\n" + summary_line(), flush=True)
    return len(fails)


if __name__ == "__main__":
    sys.exit(main())
