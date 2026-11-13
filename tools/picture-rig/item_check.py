"""The item check, in the guest: `inspect prefab` slot rows against a live thing's slots, and `remove item` in a real world.

Pure parts (row reading, template-versus-live compare, the general-flag rule, the verdicts) are tested offline in
test_item_check.py; the guest parts call StationGod through the sg client (sgcall) and only gather evidence into
out\\items.json. Everything built here is made in the VM's throwaway world.

Slots part: for each prefab of SLOT_PREFABS that the game has, the template rows (`inspect prefab`) and, for the suit,
hard suit, backpack, hard backpack, tool belt and uniform, a live instance's rows (`inspect contents` with empty and
detail). A row fails when index, name or class differ, when a special slot reads general, or when a slot that takes any
item reads not general.

Remove part: the six cases of the brief (loose, in a container, carried, a backpack's contents, a structure, a script).
"""
import json
import os

# Prefabs whose template rows are recorded; the first found in each group is the one compared with a live instance.
SLOT_GROUPS = {
    "suit": ("ItemEvaSuit",),
    "hard_suit": ("ItemHardSuit",),
    "backpack": (),  # found by pick_backpack from a *Pack* list: the game's plain pack has no fixed name we know
    "hard_backpack": ("ItemHardBackpack", "ItemHardMiningBackPack"),
    "tool_belt": ("ItemToolBelt",),
    "uniform": ("UniformOrangeJumpSuit", "UniformCommander", "UniformScientist", "UniformMarine"),
}
NO_SLOTS = "ItemIronIngot"
# A live slot name containing one of these takes only that kind of thing, so it must not read general.
SPECIAL_WORDS = ("water", "food", "waste", "cable", "ammo", "ammunition", "tank", "battery", "canister", "filter",
                 "helmet", "suit", "jetpack", "tool", "plant", "seed", "ore", "cartridge", "chip", "eject")
STRUCTURE_REFUSAL = "item_not_removable"


# ---- pure parts ----

def class_name(slot_class):
    """A slot class as text: the template gives {id, name}, a live row a plain string."""
    if isinstance(slot_class, dict):
        return str(slot_class.get("name") or "")
    return str(slot_class or "")


def template_rows(reply):
    """[{index, name, cls, general}] of an inspect prefab reply, in its order; [] for a prefab with no slots."""
    rows = []
    for row in (reply or {}).get("slots") or []:
        rows.append({"index": row.get("index"), "name": row.get("display_name") or "",
                     "cls": class_name(row.get("slot_class")), "general": bool(row.get("general"))})
    return rows


def live_rows(contents):
    """[{index, name, cls}] of an inspect contents reply made with include empty and detail (every slot, one level)."""
    result = ((contents or {}).get("results") or [contents or {}])[0]
    rows = []
    for row in (result.get("payload") or result).get("slots") or []:
        index = row.get("index", row.get("slot"))
        rows.append({"index": index, "name": row.get("display_name") or row.get("name") or "",
                     "cls": class_name(row.get("slot_class", row.get("class")))})
    return rows


def is_special(name):
    lowered = name.lower()
    return any(word in lowered for word in SPECIAL_WORDS)


def compare_rows(template, live):
    """Failures where the template's rows and the live thing's slots differ in count, index, name or class."""
    failures = []
    if len(template) != len(live):
        failures.append(f"template has {len(template)} slots, the live thing {len(live)}")
    for want, got in zip(template, live):
        for field in ("index", "name", "cls"):
            if want[field] != got[field]:
                failures.append(f"slot {want['index']}: template {field} {want[field]!r}, live {got[field]!r}")
    return failures


def general_failures(template, live):
    """Failures where the general flag disagrees with the live slot's own name and class."""
    failures = []
    for want, got in zip(template, live):
        if is_special(got["name"]) and want["general"]:
            failures.append(f"slot {want['index']} {got['name']!r} is special but reads general: true")
        elif got["cls"] in ("None", "") and not is_special(got["name"]) and not want["general"]:
            failures.append(f"slot {want['index']} {got['name']!r} takes any item but reads general: false")
    return failures


def slot_failures(group, template, live):
    """Every failure of one compared prefab, each starting with the group's name."""
    if not template:
        return [f"{group}: the template lists no slots"]
    if not live:
        return [f"{group}: no live slots to compare"]
    return [f"{group}: {text}" for text in compare_rows(template, live) + general_failures(template, live)]


def error_of(reply):
    """The refusal in a reply: top level, or the first row of a results list (a one-id call may answer either way)."""
    reply = reply or {}
    if reply.get("error"):
        return reply["error"]
    rows = reply.get("results")
    return (rows[0].get("error") if rows and isinstance(rows[0], dict) else None) or {}


def code_of(reply):
    return error_of(reply).get("code")


def message_of(reply):
    return str(error_of(reply).get("message") or "")


def pick_backpack(listing):
    """The name of a plain (not hard) pack with slots in an inspect prefab list reply, else any pack with slots, else None."""
    packs = [row for row in (listing or {}).get("prefabs") or []
             if row.get("slots") and str(row.get("prefab_name")).startswith("Item")]
    plain = [row for row in packs if "hard" not in str(row.get("prefab_name")).lower()]
    chosen = (plain or packs or [None])[0]
    return chosen["prefab_name"] if chosen else None


def gone(reply):
    """An inspect by id of a deleted thing answers thing_not_found."""
    return code_of(reply) == "thing_not_found"


def reference_ids(contents):
    """Every reference_id in an inspect contents reply, at any depth."""
    found = set()

    def walk(node):
        if isinstance(node, dict):
            for key, value in node.items():
                if key == "reference_id":
                    found.add(str(value))
                else:
                    walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(contents)
    return found


def remove_failures(evidence):
    """Failures of the remove cases, from their recorded replies; a case that did not run is a failure."""
    failures = []
    for case in ("loose", "container", "carried", "contents", "structure", "script"):
        if case not in evidence:
            failures.append(f"remove {case}: not run")
    loose = evidence.get("loose")
    if loose:
        if code_of(loose.get("report")) or not loose.get("report"):
            failures.append(f"remove loose: the report was refused ({message_of(loose.get('report'))})")
        if code_of(loose.get("confirm")):
            failures.append(f"remove loose: confirm was refused ({message_of(loose.get('confirm'))})")
        if not gone(loose.get("after")):
            failures.append("remove loose: the item is still there")
    box = evidence.get("container")
    if box:
        if code_of(box.get("confirm")):
            failures.append(f"remove container: refused ({message_of(box.get('confirm'))})")
        if not gone(box.get("after")):
            failures.append("remove container: the item is still there")
        if box.get("item_id") in reference_ids(box.get("holder_after")):
            failures.append("remove container: the slot still holds it")
    carried = evidence.get("carried")
    if carried:
        if code_of(carried.get("refused")) != STRUCTURE_REFUSAL:
            failures.append(f"remove carried: without allow the answer was {code_of(carried.get('refused'))}, "
                            f"not {STRUCTURE_REFUSAL}")
        if code_of(carried.get("allowed")):
            failures.append(f"remove carried: allow carried was refused ({message_of(carried.get('allowed'))})")
        if not gone(carried.get("after")):
            failures.append("remove carried: the item is still there")
        if carried.get("item_id") in reference_ids(carried.get("player_after")):
            failures.append("remove carried: the player's slot still holds it")
    contents = evidence.get("contents")
    if contents:
        if code_of(contents.get("refused")) != STRUCTURE_REFUSAL:
            failures.append(f"remove contents: without allow the answer was {code_of(contents.get('refused'))}, "
                            f"not {STRUCTURE_REFUSAL}")
        if code_of(contents.get("allowed")):
            failures.append(f"remove contents: allow contents was refused ({message_of(contents.get('allowed'))})")
        for thing_id, after in (contents.get("after") or {}).items():
            if not gone(after):
                failures.append(f"remove contents: {thing_id} is still there")
        if len(contents.get("after") or {}) != 3:
            failures.append(f"remove contents: checked {len(contents.get('after') or {})} items, not 3")
    structure = evidence.get("structure")
    if structure:
        reply = structure.get("reply")
        if code_of(reply) != STRUCTURE_REFUSAL or "remove structure" not in message_of(reply):
            failures.append(f"remove structure: answered {code_of(reply)} {message_of(reply)!r}, not a refusal naming "
                            "remove structure")
    script = evidence.get("script")
    if script:
        text = json.dumps(script.get("reply"), default=str)
        if "untargeted_write" in text:
            failures.append("remove script: stopped with untargeted_write")
        if code_of(script.get("reply")):
            failures.append(f"remove script: refused ({message_of(script.get('reply'))})")
        if not gone(script.get("after")):
            failures.append("remove script: the item is still there")
    return failures


def verdict(evidence):
    """(failures, notes) of a whole run's items.json content."""
    failures, notes = [], []
    slots = evidence.get("slots") or {}
    for group in ("suit", "backpack", "tool_belt"):
        if group not in slots:
            failures.append(f"{group}: no slots evidence")
    for group, row in slots.items():
        failures += slot_failures(group, row.get("template") or [], row.get("live") or [])
        notes.append(f"{group} {row.get('prefab')}: {len(row.get('template') or [])} slots, "
                     f"{row.get('general_slots')} general")
    empty = evidence.get("no_slots") or {}
    if code_of(empty) or not empty or empty.get("slots") or empty.get("general_slots") is not None:
        failures.append(f"no-slots prefab {NO_SLOTS} has slots: {empty}")
    failures += remove_failures(evidence.get("remove") or {})
    return failures, notes


# ---- guest-only parts ----

def _call(sgcall, verb, what=None, **options):
    return sgcall.call(verb, what, **options)


def placed_ids(reply):
    """The ids of the items a build item reply placed (`placed` is a list of id strings)."""
    return [str(item) for item in (reply or {}).get("placed") or []]


def build(sgcall, prefab, **where):
    reply = _call(sgcall, "build", "item", prefab=prefab, confirm=True, **where)
    ids = placed_ids(reply)
    if code_of(reply) or not ids:
        raise SystemExit(f"build item {prefab} {where}: {reply}")
    return ids[0]


def live_instance(sgcall, prefab, at):
    """(id, built): an instance the world already has, else one built loose at `at`."""
    found = _call(sgcall, "find", "item", prefab=prefab, limit=1, fields=["reference_id"])
    items = found.get("items") if isinstance(found, dict) else None
    if items:
        return str(items[0]["reference_id"]), False
    return build(sgcall, prefab, at=at), True


def detailed(sgcall, thing_id):
    return _call(sgcall, "inspect", "contents", id=thing_id, depth=1, include=["empty", "detail"])


def slots_part(sgcall, log, at):
    slots, notes = {}, []
    listing = _call(sgcall, "inspect", "prefab", prefab="*Pack*")
    log["pack_listing"] = listing
    log["backpack_prefab"] = pick_backpack(listing)
    for group, names in SLOT_GROUPS.items():
        names = (log["backpack_prefab"],) if group == "backpack" and log["backpack_prefab"] else names
        for name in names:
            reply = _call(sgcall, "inspect", "prefab", prefab=name)
            if code_of(reply):
                continue
            log.setdefault("prefab_replies", {})[name] = reply
            thing_id, built = live_instance(sgcall, name, at)
            live = detailed(sgcall, thing_id)
            log.setdefault("live_replies", {})[name] = live
            slots[group] = {"prefab": name, "template": template_rows(reply), "live": live_rows(live),
                            "general_slots": reply.get("general_slots"), "built": built, "id": thing_id}
            break
        else:
            notes.append(f"{group}: none of {names} is loaded")
    log["slots"] = slots
    log["slot_notes"] = notes
    log["no_slots"] = _call(sgcall, "inspect", "prefab", prefab=NO_SLOTS)


def first_general_indexes(row, count):
    return [slot["index"] for slot in row["template"] if slot["general"]][:count]


def remove_part(sgcall, log, at, say):
    remove = log["remove"] = {}
    # a. loose
    loose = build(sgcall, "ItemIronIngot", at=at)
    case = remove["loose"] = {"item_id": loose}
    case["report"] = _call(sgcall, "remove", "item", id=loose)
    case["confirm"] = _call(sgcall, "remove", "item", id=loose, confirm=True)
    case["after"] = _call(sgcall, "inspect", "contents", id=loose)
    say(f"remove loose: {case['confirm']}")
    # b. in a container slot: a locker the world has, else a backpack built on the ground
    lockers = _call(sgcall, "find", "thing", prefab="StructureStorageLocker", limit=1, fields=["reference_id"])
    rows = lockers.get("things") if isinstance(lockers, dict) else None
    if rows:
        holder, kind = str(rows[0]["reference_id"]), "locker"
    else:
        holder, kind = build(sgcall, log["backpack_prefab"], at=at), "built " + log["backpack_prefab"]
    item = build(sgcall, "ItemIronIngot", to=holder, slot=_first_free_slot(sgcall, holder))
    case = remove["container"] = {"item_id": item, "holder": holder, "holder_kind": kind}
    case["confirm"] = _call(sgcall, "remove", "item", id=item, confirm=True)
    case["after"] = _call(sgcall, "inspect", "contents", id=item)
    case["holder_after"] = detailed(sgcall, holder)
    say(f"remove container ({kind}): {case['confirm']}")
    # c. carried: a loose ingot moved into the player's inventory
    carried = build(sgcall, "ItemIronIngot", at=at)
    case = remove["carried"] = {"item_id": carried}
    case["moved"] = _call(sgcall, "move", "item", id=carried, to="player", slot="auto")
    case["refused"] = _call(sgcall, "remove", "item", id=carried, confirm=True)
    case["allowed"] = _call(sgcall, "remove", "item", id=carried, allow=["carried"], confirm=True)
    case["after"] = _call(sgcall, "inspect", "contents", id=carried)
    case["player_after"] = _call(sgcall, "inspect", "contents", id="player", depth=4)
    say(f"remove carried: refused {code_of(case['refused'])}, allowed {case['allowed']}")
    # d. a backpack holding two items
    pack = build(sgcall, log["backpack_prefab"], at=at)
    first, second = _first_two_free(sgcall, pack)
    held = [build(sgcall, "ItemIronIngot", to=pack, slot=first), build(sgcall, "ItemCopperIngot", to=pack, slot=second)]
    case = remove["contents"] = {"pack": pack, "held": held}
    case["refused"] = _call(sgcall, "remove", "item", id=pack, confirm=True)
    case["allowed"] = _call(sgcall, "remove", "item", id=pack, allow=["contents"], confirm=True)
    case["after"] = {thing: _call(sgcall, "inspect", "contents", id=thing) for thing in [pack] + held}
    say(f"remove contents: refused {code_of(case['refused'])}, allowed {case['allowed']}")
    # e. a structure id
    structure = _call(sgcall, "find", "thing", category="structure", limit=1, fields=["reference_id"])
    things = structure.get("things") if isinstance(structure, dict) else None
    if things:
        remove["structure"] = {"id": str(things[0]["reference_id"]),
                               "reply": _call(sgcall, "remove", "item", id=str(things[0]["reference_id"]), confirm=True)}
        say(f"remove structure: {remove['structure']['reply']}")
    # f. a script, confirmed, with the id seen as a constant in the code
    scripted = build(sgcall, "ItemIronIngot", at=at)
    code = f"sg.remove('item', id=['{scripted}'])"
    script = remove["script"] = {"item_id": scripted, "code": code}
    script["report"] = run_script(sgcall, code, confirm=False)
    script["reply"] = run_script(sgcall, code, confirm=True)
    script["after"] = _call(sgcall, "inspect", "contents", id=scripted)
    say(f"remove script: {script['reply']}")


def run_script(sgcall, code, confirm):
    """run_script through the sg client's own call (it is a hub verb with no wrapper of its own)."""
    sg = sgcall.sg
    options = {"code": code}
    if confirm:
        options["confirm"] = True
    try:
        reply = sg._calls.verb_call("run_script", None, {}, options)
        return json.loads(json.dumps(reply, default=lambda o: list(o) if hasattr(o, "__iter__") else str(o)))
    except sg.Error as e:
        return {"error": {"code": e.code, "message": e.message, "fix": e.fix, "data": e.data}}


def _free_slots(sgcall, holder):
    reply = detailed(sgcall, holder)
    rows = live_rows(reply)
    result = (reply.get("results") or [reply])[0]
    occupied = {str(row.get("index", row.get("slot"))) for row in (result.get("slots") or []) if row.get("occupant")}
    return [row["index"] for row in rows if str(row["index"]) not in occupied]


def _first_free_slot(sgcall, holder):
    free = _free_slots(sgcall, holder)
    if not free:
        raise SystemExit(f"{holder} has no free slot")
    return free[0]


def _first_two_free(sgcall, holder):
    free = _free_slots(sgcall, holder)
    if len(free) < 2:
        raise SystemExit(f"{holder} has fewer than two free slots")
    return free[0], free[1]


def run(sgcall, done, log, say=print):
    """The whole check against the live guest game. Returns (failures, notes); evidence goes to <done>\\items.json."""
    player = _call(sgcall, "find", "thing", runtime_type="Human", limit=1)
    things = player.get("things") if isinstance(player, dict) else None
    if not things:
        raise SystemExit("item check: no player to stand the items beside")
    position = things[0]["position"]
    at = [position["x"] + 1.5, position["y"] + 0.5, position["z"]]
    slots_part(sgcall, log, at)
    say("item check: slots " + json.dumps({k: len(v["template"]) for k, v in log["slots"].items()}))
    remove_part(sgcall, log, at, say)
    failures, notes = verdict(log)
    log["verdict"] = {"failures": failures, "notes": notes}
    with open(os.path.join(done, "items.json"), "w", encoding="utf-8") as f:
        json.dump(log, f, indent=1, default=str)
    return failures, notes
