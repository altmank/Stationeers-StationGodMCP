"""SDB Silo checks in a running TEST game (a rig's world copy): inspect contents, find item, move item in and out.

    py -3.12 silo_checks.py --pipe StationGodMCP-SGT5 --hub <hub dir or StationGodMCP.Server.dll>

It changes the world it runs in (moves items between silos, a locker, a suit and the ground), so run it on a fresh
copy of the rig save, never a save anyone plays. It refuses the live pipe (StationGodMCP). Built for the scoreboard
rig's prepared Vulcan2 save (run-silo-checks.ps1 resets the rig to it); it finds what it uses by name and prefab:

- a silo of ore stacks with at least 5 entries (pooled withdrawals, the DispenseSlot refusal, no_room and ground);
- a silo with room for 6 entries (deposits, the backpack round trip);
- a nearly full silo, 600 minus at most the number of spare things below (silo_full);
- an empty locker (withdrawals land there), a suit with no ore slot (no_room), a player's belt holding mining
  backpacks with ore (a backpack with contents, and spare things), a growing plant (refused).

Each check prints PASS, FAIL (with what came back) or SKIP (with why); the exit code is the number of FAILs.
Not covered here: stored food that rots (no such food in the rig save, and making one needs cheats, which only someone
at the game's console can turn on), and a client watching (a headless rig has no client).
"""
import argparse
import json
import sys

import sgcall

RESULTS = []


def record(name, ok, detail=""):
    state = "SKIP" if ok is None else "PASS" if bool(ok) else "FAIL"
    RESULTS.append((name, state))
    print(f"{state:4}  {name}" + (f"  -- {detail}" if detail else ""))


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
            "spare_packs": [p for p in pack_ids if p != full_pack], "plant": plant}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pipe", required=True)
    ap.add_argument("--hub", required=True)
    a = ap.parse_args()
    sgcall.connect(a.pipe, a.hub)
    sgcall.wait_ready()
    t = pick_things()
    print("using", json.dumps(t))
    ore_silo, roomy, locker = t["ore_silo"], t["roomy_silo"], t["locker"]
    if not (ore_silo and roomy and locker):
        record("setup", False, "no ore silo, roomy silo or empty locker in this world")
        return len([r for r in RESULTS if r[1] == "FAIL"])

    # inspect contents on a silo: the store, agreeing with the silo's own count (logic Quantity).
    view = silo_view(ore_silo, limit=3)
    entry = view["entries"][0]
    record("inspect contents on a silo",
           view["entries_known"] and view["count"] == quantity_logic(ore_silo) and view["capacity"] == 600
           and entry["index"] == 0 and entry["reference_id"] is None and "rots_when_taken" in entry,
           short(view))
    ore = entry["prefab_name"]

    # find item at location silo: every entry, held in its silo at slot -1, not movable.
    places = call("find", "item", location="silo", group_by="place")
    within = call("find", "item", location="silo", within=ore_silo, limit=2)
    row = within["items"][0]
    record("find item location silo",
           places["silo_entries"] >= view["count"] and within["total"] == view["count"]
           and row["held_in"][0]["reference_id"] == ore_silo and row["held_in"][0]["slot_index"] == -1
           and row["silo"]["movable"] is False and row["silo"]["entry"] == 0,
           f"silo_entries {places['silo_entries']}, within total {within['total']}")

    # Withdrawal, pooled stacks: a report first, then the move; 120 items into the empty locker.
    report = call("move", "item", source=ore_silo, prefab=ore, quantity=120, to=locker)
    taken = call("move", "item", source=ore_silo, prefab=ore, quantity=120, to=locker, confirm=True)
    placed = taken.get("placed", [])
    record("withdraw pooled stacks (report, then confirm)",
           report.get("report_only") is True and taken.get("stock", {}).get("change") == -120
           and sum(p["quantity"] for p in placed) == 120 and all(p["where"] in ("slot", "merged") for p in placed)
           and taken["count"]["after"] == quantity_logic(ore_silo),
           short(taken))

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

    # A growing plant is refused.
    if t["plant"]:
        planted = call("move", "item", to=roomy, id=[t["plant"]])
        res = (planted.get("items") or {}).get("results", [{}])[0]
        record("deposit refuses a growing plant", (res.get("error") or {}).get("code") == "planted", short(planted))
    else:
        record("deposit refuses a growing plant", None, "no pumpkin growing")

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

    # silo_full: past 600 entries each further thing is refused.
    full_silo = t["full_silo"]
    room = 600 - silo_view(full_silo, limit=1)["count"]
    spare = t["spare_packs"]
    if full_silo not in (ore_silo, roomy) and len(spare) > room:
        dep = call("move", "item", to=full_silo, id=spare[:room + 1])
        rows = (dep.get("items") or {}).get("results", [])
        record("deposit silo_full past 600 entries",
               sum(1 for r in rows if r.get("ok")) == room
               and (rows[-1].get("error") or {}).get("code") == "silo_full"
               and dep["count"]["after"] == 600 and quantity_logic(full_silo) == 600, short(rows[-1]))
    else:
        record("deposit silo_full past 600 entries", None, f"{room} entries free, {len(spare)} spare things")

    fails = [r for r in RESULTS if r[1] == "FAIL"]
    print(f"\n{len(RESULTS)} checks: {len(RESULTS) - len(fails)} passed or skipped, {len(fails)} failed")
    return len(fails)


if __name__ == "__main__":
    sys.exit(main())
