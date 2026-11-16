"""The needs check, in the guest: write needs sets a player's nutrition, hydration and waste and reads them back, inspect
player reads the same values, and undo puts them back, against a real world (the VM's throwaway one).

Pure parts (the shares, the verdicts) are tested offline in test_needs_check.py against a fake game; the guest parts
call StationGod through the sg client (sgcall) and gather evidence into out\\needs.json.

Cases: needs-set, needs-undo, needs-read. The verdict judges exactly these three.

Every expected value comes from a reply or the game's data: the asked values are shares chosen away from what the player
reads now (so a set and an undo cannot pass by changing nothing); the capacities, the before values and Sanitation are
read from inspect player and from the write's own reply. If the world has Sanitation off, the waste part asserts
sanitation_off instead (and the check says so in its notes): the game reads waste as 0 there.
"""
import json
import os
import time

from item_check import code_of, message_of

CASES = ("needs-set", "needs-undo", "needs-read")
MARKER = "needs-check ran: " + ",".join(CASES)

# What a share may differ from the asked one by after the write: the game drains hunger and thirst every tick, and the
# waste read-back accepts what the game's float maths keeps.
TOLERANCE = 0.03
SETTLE_SECONDS = 6
PREFERRED = (0.5, 0.25)
APART = 0.1


# ---- pure parts ----

def body(reply):
    """The reply of a one-id call: its first result row when the hub wrapped it as {results: [...]}, else the reply."""
    rows = (reply or {}).get("results")
    if rows and isinstance(rows[0], dict):
        return rows[0].get("payload") or rows[0]
    return reply or {}


def shares(player, hydration_divisor):
    """{nutrition, hydration, waste, sanitation} of an inspect player reply, as the shares write needs takes: nutrition
    of its storage, hydration of the divisor the write's reply gave, waste as the stomach's ratio (None: no stomach)."""
    p = body(player)
    capacity = p.get("nutrition_capacity")
    return {"nutrition": (p.get("nutrition") / capacity) if capacity else None,
            "hydration": (p.get("hydration") / hydration_divisor) if hydration_divisor else None,
            "waste": p.get("waste_ratio"), "sanitation": p.get("sanitation")}


def asked_value(before):
    """A share to ask for: the first preferred one that is not within APART of what the player reads now."""
    for value in PREFERRED:
        if before is None or abs(before - value) > APART:
            return value
    return PREFERRED[-1] / 2


def asked_levels(before):
    """{nutrition, hydration, waste} to ask for, each away from its reading now (before is a shares() dict)."""
    return {need: asked_value(before.get(need)) for need in ("nutrition", "hydration", "waste")}


def near(value, wanted, tolerance=TOLERANCE):
    return value is not None and wanted is not None and abs(value - wanted) <= tolerance


def reply_change(reply, need):
    return (body(reply).get(need) or {}) if isinstance(body(reply).get(need), dict) else None


def set_failures(evidence):
    ran = evidence.get("set")
    if not ran:
        return ["needs-set: not run"]
    failures = []
    reply, asked, before = ran.get("reply"), ran["asked"], ran["before"]
    if code_of(reply):
        return [f"needs-set: write needs refused {code_of(reply)} {message_of(reply)}"]
    for need in ("nutrition", "hydration"):
        change = reply_change(reply, need)
        if change is None:
            failures.append(f"needs-set: the reply has no {need} change")
            continue
        if not near(change.get("after"), asked[need]):
            failures.append(f"needs-set: {need} reads {change.get('after')} after, asked {asked[need]}")
        if not near(change.get("before"), before[need]):
            failures.append(f"needs-set: {need} before {change.get('before')} is not what inspect player read "
                            f"({before[need]})")
    if before["sanitation"]:
        waste = reply_change(reply, "waste")
        if waste is None:
            failures.append("needs-set: Sanitation is on and the reply has no waste change")
        elif not near(waste.get("after"), asked["waste"]):
            failures.append(f"needs-set: waste reads {waste.get('after')} after, asked {asked['waste']}")
        elif not near(waste.get("before"), before["waste"]):
            failures.append(f"needs-set: waste before {waste.get('before')} is not what inspect player read "
                            f"({before['waste']})")
    else:
        refused = ran.get("waste_refused")
        if code_of(refused) != "sanitation_off":
            failures.append(f"needs-set: Sanitation is off and a waste ask answered {code_of(refused) or 'ok'}, "
                            "not sanitation_off")
        if body(reply).get("waste") is not None:
            failures.append("needs-set: Sanitation is off and the reply still carries a waste change")
    if body(reply).get("sanitation") != before["sanitation"]:
        failures.append("needs-set: the reply's sanitation is not inspect player's")
    codes = [w.get("code") for w in body(reply).get("warnings") or []]
    if "drains" not in codes:
        failures.append(f"needs-set: nutrition and hydration were set and the warnings are {codes}, with no drains")
    return failures


def read_failures(evidence):
    ran = evidence.get("read")
    if not ran:
        return ["needs-read: not run"]
    failures = []
    after, asked, divisor = ran["after"], ran["asked"], ran["divisor"]
    if "sanitation" not in after or after["sanitation"] is None:
        failures.append("needs-read: inspect player has no sanitation")
    for need in ("nutrition", "hydration"):
        if not near(after[need], asked[need]):
            failures.append(f"needs-read: inspect player reads {need} {after[need]}, asked {asked[need]}")
    if after.get("sanitation"):
        if not near(after["waste"], asked["waste"]):
            failures.append(f"needs-read: inspect player reads waste_ratio {after['waste']}, asked {asked['waste']}")
    elif after.get("waste") is None:
        failures.append("needs-read: inspect player has no waste_ratio for a body with a stomach")
    elif after["waste"] != 0:
        failures.append(f"needs-read: Sanitation is off and waste_ratio reads {after['waste']}, not 0")
    if not divisor:
        failures.append("needs-read: no hydration divisor from the write reply")
    return failures


def undo_failures(evidence):
    ran = evidence.get("undo")
    if not ran:
        return ["needs-undo: not run"]
    failures = []
    if not ran.get("job_id"):
        return ["needs-undo: the write reply has no job_id to undo"]
    reply = ran.get("reply")
    if code_of(reply):
        return [f"needs-undo: undo refused {code_of(reply)} {message_of(reply)}"]
    if not (body(reply).get("undone") or 0) >= 1:
        failures.append(f"needs-undo: undo reports {body(reply).get('undone')} undone")
    before, after = ran["before"], ran["after"]
    for need in ("nutrition", "hydration"):
        if not near(after[need], before[need]):
            failures.append(f"needs-undo: {need} reads {after[need]} after undo, was {before[need]} before the write")
    if before["sanitation"] and not near(after["waste"], before["waste"]):
        failures.append(f"needs-undo: waste reads {after['waste']} after undo, was {before['waste']} before the write")
    return failures


def verdict(evidence):
    """(failures, notes) of a whole run's needs.json content."""
    failures = set_failures(evidence) + undo_failures(evidence) + read_failures(evidence)
    if evidence.get("marker") != MARKER:
        failures.append("the needs check did not run: its marker is missing from the evidence")
    notes = [MARKER] if evidence.get("marker") == MARKER else []
    ran = evidence.get("set") or {}
    if ran.get("before") and not ran["before"]["sanitation"]:
        notes.append("Sanitation is off in this world: the waste part asserted sanitation_off")
    return failures, notes


# ---- guest-only parts ----

def _call(sgcall, verb, what=None, **options):
    return sgcall.call(verb, what, **options)


def read_player(sgcall, divisor):
    return shares(_call(sgcall, "inspect", "player"), divisor)


def settle(sgcall, divisor, wanted, waste_matters, seconds=SETTLE_SECONDS):
    """inspect player's shares once nutrition and hydration (and waste, when it matters) read near wanted, or after the
    seconds are up: the waste's event is applied at the game's next atmospherics tick."""
    end = time.monotonic() + seconds
    while True:
        now = read_player(sgcall, divisor)
        done = near(now["nutrition"], wanted["nutrition"]) and near(now["hydration"], wanted["hydration"])
        if waste_matters:
            done = done and near(now["waste"], wanted["waste"])
        if done or time.monotonic() >= end:
            return now
        time.sleep(0.5)


def run(sgcall, done, log, say=print):
    """The whole check against the live guest game. Returns (failures, notes); evidence goes to <done>\\needs.json."""
    first = _call(sgcall, "inspect", "player")
    log["player"] = first
    # The hydration divisor is the write reply's own capacity; until it is known, hydration is read against inspect
    # player's hydration capacity, which differs only by the food quality multiplier (1 for a fresh player).
    divisor = body(first).get("hydration_capacity")
    before = shares(first, divisor)
    asked = asked_levels(before)
    ran = log["set"] = {"before": before, "asked": asked}
    if before["sanitation"]:
        request = dict(asked)
    else:
        request = {k: asked[k] for k in ("nutrition", "hydration")}
        ran["waste_refused"] = _call(sgcall, "write", "needs", waste=asked["waste"], confirm=True)
    reply = ran["reply"] = _call(sgcall, "write", "needs", confirm=True, **request)
    say("needs check: needs-set sent")
    job_id = body(reply).get("job_id") or (reply or {}).get("job_id")
    divisor = (reply_change(reply, "hydration") or {}).get("capacity") or divisor
    before = ran["before"] = shares(first, divisor)
    after = settle(sgcall, divisor, asked, before["sanitation"])
    log["read"] = {"after": after, "asked": asked, "divisor": divisor}
    say("needs check: needs-read done")
    undo = log["undo"] = {"job_id": job_id, "before": before}
    undo["reply"] = _call(sgcall, "undo", id=job_id, confirm=True) if job_id else None
    undo["after"] = settle(sgcall, divisor, before, before["sanitation"])
    say("needs check: needs-undo done")
    log["marker"] = MARKER
    failures, notes = verdict(log)
    log["verdict"] = {"failures": failures, "notes": notes}
    with open(os.path.join(done, "needs.json"), "w", encoding="utf-8") as f:
        json.dump(log, f, indent=1, default=str)
    return failures, notes
