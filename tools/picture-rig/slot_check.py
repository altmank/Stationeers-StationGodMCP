"""The slot check, in the guest: a slot named by its key, the player's hands, a storage filled from a list, and the shape
of a cut reply, against a real world (the VM's throwaway one).

Pure parts (key analysis, the verdicts) are tested offline in test_slot_check.py; the guest parts call StationGod
through the sg client (sgcall) and gather evidence into out\\slots.json.

Cases: slots-by-key, hand, locker-list, cut-reply-shape.
"""
import json
import os

import item_check
from item_check import code_of, message_of

GENERAL_ITEM = "ItemIronSheets"
HAND_OTHER = "ItemIronIngot"
PACK = item_check.PACK
MADE_UP_KEY = "NoSuchSlotKey"
CUT_TO = 3
CASES = ("slots-by-key", "hand", "locker-list", "cut-reply-shape")
MARKER = "slot-check ran: " + ",".join(CASES)


# ---- pure parts ----

def key_rows(contents):
    """[{index, key, empty, cls, name}] of an inspect contents reply made with include empty and detail; key is None
    when the row has no string_key or an empty one; `has_key_field` is false when any row lacks the field entirely."""
    result = ((contents or {}).get("results") or [contents or {}])[0]
    body = result.get("payload") or result
    rows = []
    for row in body.get("slots") or []:
        rows.append({"index": row.get("index", row.get("slot")), "has_key_field": "string_key" in row,
                     "key": row.get("string_key") or None, "empty": not row.get("occupant"),
                     "cls": item_check.class_name(row.get("slot_class")), "name": row.get("display_name") or ""})
    return rows


def analyse_keys(rows):
    """{count, keyed, unkeyed, unique: {key: index}, duplicates: {key: [index...]}} of key_rows' rows."""
    seen = {}
    for row in rows:
        if row["key"]:
            seen.setdefault(row["key"], []).append(row["index"])
    return {"count": len(rows), "keyed": sum(1 for r in rows if r["key"]), "unkeyed": sum(1 for r in rows if not r["key"]),
            "unique": {k: v[0] for k, v in seen.items() if len(v) == 1},
            "duplicates": {k: v for k, v in seen.items() if len(v) > 1}}


def candidates(rows, analysis):
    """Slots a key build may try, in order: empty, with a key no other slot has."""
    return [(r["key"], r["index"]) for r in rows if r["empty"] and r["key"] in analysis["unique"]]


def landed_index(reply):
    """The slot index the first items row of a build item reply landed in, or None."""
    for row in (reply or {}).get("items") or []:
        slot = row.get("slot")
        if slot:
            return slot.get("slot")
    return None


def truncated_note(reply):
    """The cut note of a reply: its `truncated` entry for list slots, or the data of the client's truncated refusal."""
    notes = (reply or {}).get("truncated")
    if isinstance(notes, dict):
        notes = [notes]
    for note in notes or []:
        if isinstance(note, dict) and note.get("list") == "slots":
            return note
    error = (reply or {}).get("error") or {}
    if error.get("code") == "truncated" and isinstance(error.get("data"), dict):
        return error["data"]
    return None


def slots_returned(reply):
    return len((reply or {}).get("slots") or [])


def key_failures(evidence):
    failures = []
    keys = evidence.get("keys")
    if not keys:
        return ["slots-by-key: not run"]
    if keys.get("worn_suit") != keys.get("suit_id"):
        failures.append("slots-by-key: inspect player worn.suit is not the suit read")
    if not keys.get("rows"):
        failures.append("slots-by-key: the suit has no slot rows")
    for row in keys.get("rows") or []:
        if not row["has_key_field"]:
            failures.append(f"slots-by-key: slot {row['index']} has no string_key field")
            break
    missing = keys.get("missing") or {}
    if code_of(missing) != "slot_not_found":
        failures.append(f"slots-by-key: a made-up key answered {code_of(missing) or 'ok'}, not slot_not_found")
    else:
        present = set(keys["analysis"]["unique"]) | set(keys["analysis"]["duplicates"])
        if present and not any(key in message_of(missing) for key in present):
            failures.append("slots-by-key: slot_not_found does not list the keys present")
    if code_of(keys.get("empty_key")) not in ("invalid_argument",):
        failures.append(f"slots-by-key: an empty key answered {code_of(keys.get('empty_key')) or 'ok'}, "
                        "not invalid_argument")
    for key, indexes in (keys["analysis"]["duplicates"] or {}).items():
        reply = (keys.get("ambiguous") or {}).get(key)
        if code_of(reply) != "ambiguous_slot":
            failures.append(f"slots-by-key: duplicate key {key!r} answered {code_of(reply) or 'ok'}, not ambiguous_slot")
        elif not all(str(i) in message_of(reply) for i in indexes):
            failures.append(f"slots-by-key: ambiguous_slot for {key!r} does not list indexes {indexes}")
    for key, reply in (keys.get("resolved") or {}).items():
        if code_of(reply) in ("slot_not_found", "ambiguous_slot", "invalid_argument"):
            failures.append(f"slots-by-key: the unique key {key!r} answered {code_of(reply)}, not a slot refusal "
                            "or a made item")
    built = keys.get("built")
    if built:
        if code_of(built["reply"]):
            failures.append(f"slots-by-key: build by key {built['key']!r} refused {code_of(built['reply'])}")
        elif landed_index(built["reply"]) != built["index"]:
            failures.append(f"slots-by-key: key {built['key']!r} landed in slot {landed_index(built['reply'])}, "
                            f"not {built['index']}")
    moved = keys.get("moved")
    if moved and not code_of(moved["reply"]):
        to = (moved["reply"] or {}).get("to") or {}
        if to.get("slot_key") != moved["key"] or to.get("slot") != moved["index"]:
            failures.append(f"slots-by-key: move by key {moved['key']!r} answered to {to}")
    return failures


def hand_failures(evidence):
    hand = evidence.get("hand")
    if not hand:
        return ["hand: not run"]
    failures = []
    first = hand.get("first")
    if code_of(first) not in ("", None, "slot_occupied"):
        failures.append(f"hand: the first right_hand build answered {code_of(first)}")
    if not code_of(first) and landed_index(first) is None:
        failures.append("hand: the first right_hand build made nothing")
    if code_of(hand.get("second")) != "slot_occupied":
        failures.append(f"hand: a different item into the occupied right hand answered "
                        f"{code_of(hand.get('second')) or 'ok'}, not slot_occupied (never silent)")
    moved = hand.get("moved")
    if moved is not None and code_of(moved) != "slot_occupied":
        failures.append(f"hand: move item into the occupied right hand answered {code_of(moved) or 'ok'}, "
                        "not slot_occupied")
    if "left" in hand:
        if code_of(hand["left"]) or landed_index(hand["left"]) is None:
            failures.append(f"hand: build into the empty left_hand answered {code_of(hand['left']) or 'nothing made'}")
        to = (hand.get("left_moved") or {}).get("to") or {}
        if code_of(hand.get("left_moved")) or to.get("slot_key") != "left_hand":
            failures.append(f"hand: move into the empty left_hand answered {code_of(hand.get('left_moved'))} to {to}")
    if hand.get("worn_right") is None:
        failures.append("hand: inspect player worn.right_hand is None with the hand occupied")
    return failures


def list_failures(evidence):
    box = evidence.get("list")
    if not box:
        return ["locker-list: not run"]
    failures = []
    fits = box.get("fits")
    if code_of(fits):
        failures.append(f"locker-list: a list that fits was refused {code_of(fits)} {message_of(fits)}")
    else:
        rows = (fits or {}).get("items") or []
        if sorted({row.get("line") for row in rows}) != [0, 1]:
            failures.append(f"locker-list: rows carry lines {[row.get('line') for row in rows]}, not 0 and 1")
        for thing in (fits or {}).get("placed") or []:
            if thing not in box.get("after_fits_ids", []):
                failures.append(f"locker-list: made {thing} is not in the storage afterwards")
    over = box.get("over")
    if code_of(over) != "no_room" or "items[2]" not in message_of(over):
        failures.append(f"locker-list: an over-full list answered {code_of(over) or 'ok'} {message_of(over)!r}, "
                        "not no_room naming items[2]")
    if box.get("before_over_ids") != box.get("after_over_ids"):
        failures.append("locker-list: the storage changed after the refused list")
    both = box.get("both")
    if code_of(both) != "invalid_argument":
        failures.append(f"locker-list: items with prefab answered {code_of(both) or 'ok'}, not invalid_argument")
    return failures


def cut_failures(evidence):
    cut = evidence.get("cut")
    if not cut:
        return ["cut-reply-shape: not run"]
    failures = []
    if "whole" not in cut:
        return ["cut-reply-shape: no device in the world has more than %d slots: %s" % (CUT_TO, cut.get("looked_at"))]
    whole, total = cut.get("whole"), cut.get("total")
    if code_of(whole):
        failures.append(f"cut-reply-shape: inspect slots refused {code_of(whole)}")
    elif truncated_note(whole):
        failures.append(f"cut-reply-shape: inspect slots on a {total}-slot suit carries a cut note {truncated_note(whole)}")
    elif slots_returned(whole) != total:
        failures.append(f"cut-reply-shape: inspect slots answered {slots_returned(whole)} of {total} slots")
    limited = cut.get("limited")
    note = truncated_note(limited)
    if total is not None and total <= CUT_TO:
        failures.append(f"cut-reply-shape: the suit has only {total} slots; nothing to cut")
    elif code_of(limited) and not note:
        failures.append(f"cut-reply-shape: list_limits {{slots: {CUT_TO}}} was refused {code_of(limited)} "
                        f"{message_of(limited)}")
    elif not note:
        failures.append(f"cut-reply-shape: list_limits {{slots: {CUT_TO}}} answered {slots_returned(limited)} slots "
                        "and no truncated note")
    else:
        if note.get("returned") != CUT_TO or note.get("total") != total or not note.get("more"):
            failures.append(f"cut-reply-shape: the note is malformed: {note}")
        if not code_of(limited) and slots_returned(limited) != CUT_TO:
            failures.append(f"cut-reply-shape: {slots_returned(limited)} slots came, not {CUT_TO}")
    return failures


def verdict(evidence):
    """(failures, notes) of a whole run's slots.json content."""
    failures = key_failures(evidence) + hand_failures(evidence) + list_failures(evidence) + cut_failures(evidence)
    if evidence.get("marker") != MARKER:
        failures.append("the slot check did not run: its marker is missing from the evidence")
    notes = [MARKER] if evidence.get("marker") == MARKER else []
    keys = evidence.get("keys") or {}
    analysis = keys.get("analysis")
    if analysis:
        notes.append(f"suit {analysis['count']} slots, {analysis['keyed']} keyed, "
                     f"{len(analysis['duplicates'])} shared keys")
    return failures, notes


# ---- guest-only parts ----

def _call(sgcall, verb, what=None, **options):
    return sgcall.call(verb, what, **options)


def _holder_ids(sgcall, holder):
    return sorted(item_check.reference_ids(item_check.detailed(sgcall, holder)) - {str(holder)})


def keys_part(sgcall, log, at):
    keys = log["keys"] = {}
    player = _call(sgcall, "inspect", "player")
    log["player"] = player
    worn = (player.get("worn") or {})
    suit = (worn.get("suit") or {}).get("reference_id")
    if not suit:
        raise SystemExit("slot check: the player wears no suit (inspect player worn.suit is None)")
    keys["suit_id"], keys["worn_suit"] = suit, suit
    contents = item_check.detailed(sgcall, suit)
    log["suit_contents"] = contents
    rows = keys["rows"] = key_rows(contents)
    analysis = keys["analysis"] = analyse_keys(rows)
    keys["missing"] = _call(sgcall, "build", "item", prefab=GENERAL_ITEM, to=suit, slot=MADE_UP_KEY, confirm=True)
    keys["empty_key"] = _call(sgcall, "build", "item", prefab=GENERAL_ITEM, to=suit, slot="", confirm=True)
    keys["ambiguous"] = {key: _call(sgcall, "build", "item", prefab=GENERAL_ITEM, to=suit, slot=key, confirm=True)
                         for key in analysis["duplicates"]}
    first_unique = sorted(analysis["unique"])[:1]
    keys["resolved"] = {key: _call(sgcall, "build", "item", prefab=GENERAL_ITEM, to=suit, slot=key, confirm=True)
                        for key in first_unique}
    tried = keys["tried"] = []
    for key, index in candidates(rows, analysis):
        reply = _call(sgcall, "build", "item", prefab=GENERAL_ITEM, to=suit, slot=key, confirm=True)
        tried.append({"key": key, "index": index, "code": code_of(reply)})
        if not code_of(reply):
            keys["built"] = {"key": key, "index": index, "reply": reply}
            break
    rest = [c for c in candidates(item_rows(sgcall, suit), analysis) if c[0] != (keys.get("built") or {}).get("key")]
    for key, index in rest:
        loose = item_check.build(sgcall, GENERAL_ITEM, at=at)
        reply = _call(sgcall, "move", "item", id=loose, to=suit, slot=key)
        tried.append({"key": key, "index": index, "code": code_of(reply), "move": True})
        if not code_of(reply):
            keys["moved"] = {"key": key, "index": index, "reply": reply}
            break


def item_rows(sgcall, holder):
    return key_rows(item_check.detailed(sgcall, holder))


def hand_part(sgcall, log, at):
    hand = log["hand"] = {}
    hand["before"] = _call(sgcall, "inspect", "player").get("worn")
    hand["first"] = _call(sgcall, "build", "item", prefab=GENERAL_ITEM, to="player", slot="right_hand", confirm=True)
    hand["second"] = _call(sgcall, "build", "item", prefab=HAND_OTHER, to="player", slot="right_hand", confirm=True)
    loose = item_check.build(sgcall, HAND_OTHER, at=at)
    hand["moved"] = _call(sgcall, "move", "item", id=loose, to="player", slot="right_hand")
    hand["worn_right"] = (_call(sgcall, "inspect", "player").get("worn") or {}).get("right_hand")
    if (hand["before"] or {}).get("left_hand") is None:
        hand["left"] = _call(sgcall, "build", "item", prefab=GENERAL_ITEM, to="player", slot="left_hand", confirm=True)
        loose = item_check.build(sgcall, GENERAL_ITEM, at=at)
        hand["left_moved"] = _call(sgcall, "move", "item", id=loose, to="player", slot="left_hand")


def list_part(sgcall, log, at):
    box = log["list"] = {}
    pack = item_check.build(sgcall, PACK, at=at)
    box["pack"] = pack
    lines = [{"prefab": "ItemIronIngot", "quantity": 1}, {"prefab": "ItemCopperIngot", "quantity": 1}]
    box["fits"] = _call(sgcall, "build", "item", to=pack, items=lines, confirm=True)
    box["after_fits_ids"] = _holder_ids(sgcall, pack)
    box["before_over_ids"] = _holder_ids(sgcall, pack)
    over = lines + [{"prefab": GENERAL_ITEM, "quantity": 2500}]
    box["over"] = _call(sgcall, "build", "item", to=pack, items=over, confirm=True)
    box["after_over_ids"] = _holder_ids(sgcall, pack)
    box["both"] = _call(sgcall, "build", "item", prefab=GENERAL_ITEM, to=pack, items=lines, confirm=True)


def cut_part(sgcall, log):
    """inspect slots reaches devices and logic-capable worn items (a vanilla suit has no logic), so the cut is read on
    the first device of the world with more than CUT_TO slots."""
    found = _call(sgcall, "find", "device", limit=60, fields=["reference_id"])
    cut = log["cut"] = {"looked_at": []}
    for row in (found.get("devices") or []) if isinstance(found, dict) else []:
        thing = str(row["reference_id"])
        whole = _call(sgcall, "inspect", "slots", id=thing)
        count = slots_returned(whole)
        cut["looked_at"].append({"id": thing, "slots": count, "code": code_of(whole)})
        if count > CUT_TO and not truncated_note(whole):
            cut.update(id=thing, total=count, whole=whole)
            cut["limited"] = _call(sgcall, "inspect", "slots", id=thing, list_limits={"slots": CUT_TO})
            return


def run(sgcall, done, log, say=print):
    """The whole check against the live guest game. Returns (failures, notes); evidence goes to <done>\\slots.json."""
    player = _call(sgcall, "find", "thing", runtime_type="Human", limit=1)
    things = player.get("things") if isinstance(player, dict) else None
    if not things:
        raise SystemExit("slot check: no player to stand the items beside")
    position = things[0]["position"]
    at = [position["x"] + 1.5, position["y"] + 0.5, position["z"]]
    for name, part in (("slots-by-key", keys_part), ("hand", hand_part), ("locker-list", list_part)):
        part(sgcall, log, at)
        say(f"slot check: {name} done")
    cut_part(sgcall, log)
    say("slot check: cut-reply-shape done")
    log["marker"] = MARKER
    failures, notes = verdict(log)
    log["verdict"] = {"failures": failures, "notes": notes}
    with open(os.path.join(done, "slots.json"), "w", encoding="utf-8") as f:
        json.dump(log, f, indent=1, default=str)
    return failures, notes
