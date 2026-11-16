"""slot_check's pure logic, offline: key rows, the key analysis, and each case's verdict.

    py -3.12 -m pytest tools/picture-rig/test_slot_check.py
"""
import slot_check as c

CONTENTS = {"slots": [
    {"index": 0, "display_name": "Power", "string_key": "SharedPower1", "slot_class": "Battery", "occupant": None},
    {"index": 1, "display_name": "Power", "string_key": "SharedPower2", "slot_class": "Battery", "occupant": {"reference_id": "9"}},
    {"index": 2, "display_name": "Slot", "string_key": "Dup", "slot_class": "None", "occupant": None},
    {"index": 3, "display_name": "Slot", "string_key": "Dup", "slot_class": "None", "occupant": None},
    {"index": 4, "display_name": "Slot", "string_key": None, "slot_class": "None", "occupant": None}]}


def err(code, message=""):
    return {"error": {"code": code, "message": message}}


def good_keys():
    rows = c.key_rows(CONTENTS)
    analysis = c.analyse_keys(rows)
    return {"suit_id": "5", "worn_suit": "5", "rows": rows, "analysis": analysis,
            "missing": err("slot_not_found", "its slot keys are SharedPower1, SharedPower2, Dup"),
            "empty_key": err("invalid_argument"),
            "ambiguous": {"Dup": err("ambiguous_slot", "slot indexes 2, 3")},
            "built": {"key": "SharedPower1", "index": 0, "reply": {"items": [{"line": 0, "slot": {"id": "5", "slot": 0}}]}},
            "moved": {"key": "SharedPower1", "index": 0, "reply": {"to": {"id": "5", "slot": 0, "slot_key": "SharedPower1"}}}}


def test_rows_and_analysis():
    rows = c.key_rows(CONTENTS)
    analysis = c.analyse_keys(rows)
    assert analysis["count"] == 5 and analysis["keyed"] == 4 and analysis["unkeyed"] == 1
    assert analysis["unique"] == {"SharedPower1": 0, "SharedPower2": 1}
    assert analysis["duplicates"] == {"Dup": [2, 3]}
    assert c.candidates(rows, analysis) == [("SharedPower1", 0)]


def test_a_good_key_case_passes():
    assert c.key_failures({"keys": good_keys()}) == []


def test_a_key_that_lands_in_another_slot_fails():
    keys = good_keys()
    keys["built"]["reply"] = {"items": [{"slot": {"id": "5", "slot": 3}}]}
    assert any("landed in slot 3" in f for f in c.key_failures({"keys": keys}))


def test_a_made_up_key_must_be_slot_not_found_and_list_keys():
    keys = good_keys()
    keys["missing"] = err("slot_not_found", "nothing useful")
    assert any("does not list the keys" in f for f in c.key_failures({"keys": keys}))
    keys["missing"] = {"placed": []}
    assert any("not slot_not_found" in f for f in c.key_failures({"keys": keys}))


def test_a_shared_key_must_be_ambiguous_with_its_indexes():
    keys = good_keys()
    keys["ambiguous"]["Dup"] = err("ambiguous_slot", "slot indexes 2")
    assert any("does not list indexes" in f for f in c.key_failures({"keys": keys}))


def test_a_missing_string_key_field_fails():
    keys = good_keys()
    keys["rows"][2]["has_key_field"] = False
    assert any("no string_key field" in f for f in c.key_failures({"keys": keys}))


def test_hands_must_never_be_silent():
    hand = {"first": {"items": [{"slot": {"id": "1", "slot": 0}}]}, "second": err("slot_occupied"),
            "moved": err("slot_occupied"), "worn_right": {"reference_id": "7"}}
    assert c.hand_failures({"hand": hand}) == []
    hand["second"] = {"items": []}
    assert any("never silent" in f for f in c.hand_failures({"hand": hand}))


def good_list():
    return {"fits": {"placed": ["1", "2"], "items": [{"line": 0}, {"line": 1}]}, "after_fits_ids": ["1", "2"],
            "over": err("no_room", "items[2]: takes 0 of 2500"), "before_over_ids": ["1", "2"],
            "after_over_ids": ["1", "2"], "both": err("invalid_argument")}


def test_a_good_list_case_passes_and_a_changed_storage_fails():
    assert c.list_failures({"list": good_list()}) == []
    box = good_list()
    box["after_over_ids"] = ["1", "2", "3"]
    assert any("changed after the refused" in f for f in c.list_failures({"list": box}))
    box = good_list()
    box["over"] = err("no_room", "items[1]: takes 0")
    assert any("items[2]" in f for f in c.list_failures({"list": box}))


def test_the_cut_shape():
    note = {"list": "slots", "returned": 3, "total": 21, "more": "pass list_limits {slots: 21}"}
    cut = {"total": 21, "whole": {"slots": [{}] * 21}, "limited": {"slots": [{}] * 3, "truncated": [note]}}
    assert c.cut_failures({"cut": cut}) == []
    cut["whole"] = {"slots": [{}] * 5, "truncated": [{"list": "slots", "returned": 5, "total": 21, "more": "x"}]}
    assert any("carries a cut note" in f for f in c.cut_failures({"cut": cut}))


def test_a_cut_note_may_come_as_the_clients_truncated_refusal():
    note = {"list": "slots", "returned": 3, "total": 21, "more": "pass list_limits"}
    cut = {"total": 21, "whole": {"slots": [{}] * 21}, "limited": {"error": {"code": "truncated", "data": note}}}
    assert c.cut_failures({"cut": cut}) == []
    cut["limited"] = {"slots": [{}] * 3}
    assert any("no truncated note" in f for f in c.cut_failures({"cut": cut}))
    cut["limited"] = {"slots": [{}] * 3, "truncated": [{"list": "slots", "returned": 3, "total": 21}]}
    assert any("malformed" in f for f in c.cut_failures({"cut": cut}))


def test_a_case_that_did_not_run_fails():
    failures, _ = c.verdict({})
    assert len(failures) == 5
    assert any("did not run" in f for f in failures)


def test_a_full_run_without_the_marker_fails():
    failures, _ = c.verdict({"keys": good_keys(), "hand": {}, "list": good_list(), "cut": {}})
    assert any("did not run" in f for f in failures)


class FakeGame:
    """A stand-in for sgcall that answers like the game for the calls the check makes (a suit with the CONTENTS slots)."""

    def __init__(self):
        self.next_id = 100
        self.packs = {}
        self.right_hand = None

    def call(self, verb, what=None, **o):
        if (verb, what) == ("find", "thing"):
            return {"things": [{"position": {"x": 0, "y": 0, "z": 0}}]}
        if (verb, what) == ("inspect", "player"):
            return {"worn": {"suit": {"reference_id": "5"}, "right_hand": self.right_hand}}
        if (verb, what) == ("inspect", "contents"):
            if o["id"] == "5":
                return CONTENTS
            return {"slots": [{"index": i, "display_name": "Slot", "string_key": None, "slot_class": "None",
                               "occupant": ({"reference_id": r} if r else None)} for i, r in enumerate(self.packs[o["id"]])]}
        if (verb, what) == ("inspect", "slots"):
            n = o.get("list_limits", {}).get("slots", 5)
            reply = {"slots": [{}] * min(n, 5)}
            if n < 5:
                reply["truncated"] = [{"list": "slots", "returned": n, "total": 5, "more": "pass list_limits"}]
            return reply
        if (verb, what) == ("build", "item"):
            return self.build(o)
        if (verb, what) == ("move", "item"):
            if o.get("slot") == "right_hand":
                return err("slot_occupied")
            return {"to": {"id": "5", "slot": 0, "slot_key": o["slot"]}}
        raise AssertionError((verb, what, o))

    def build(self, o):
        if "items" in o and "prefab" in o:
            return err("invalid_argument")
        if "items" in o:
            pack = self.packs[o["to"]]
            if any(line.get("quantity", 1) > 100 for line in o["items"]):
                return err("no_room", "items[2]: takes 0 of 2500")
            placed = []
            for line, _ in enumerate(o["items"]):
                self.next_id += 1
                pack[pack.index(None)] = str(self.next_id)
                placed.append(str(self.next_id))
            return {"placed": placed, "items": [{"line": i, "slot": {"id": o["to"], "slot": i}} for i in range(len(placed))]}
        self.next_id += 1
        made = str(self.next_id)
        if o.get("to") == "5":
            slot = o["slot"]
            if slot == "" : return err("invalid_argument")
            if slot == "Dup": return err("ambiguous_slot", "slot indexes 2, 3")
            if slot == "SharedPower1": return {"placed": [made], "items": [{"line": 0, "slot": {"id": "5", "slot": 0}}]}
            return err("slot_not_found", "its slot keys are SharedPower1, SharedPower2, Dup")
        if o.get("to") == "player":
            if self.right_hand is None and o["prefab"] == c.GENERAL_ITEM:
                self.right_hand = {"reference_id": made}
                return {"placed": [made], "items": [{"line": 0, "slot": {"id": "p", "slot": 6}}]}
            return err("slot_occupied")
        if o["prefab"] == c.PACK:
            self.packs[made] = [None] * 20
        return {"placed": [made]}


def test_the_whole_check_runs_against_a_fake_game(tmp_path):
    failures, notes = c.run(FakeGame(), str(tmp_path), {}, say=lambda text: None)
    assert failures == [], failures
    assert (tmp_path / "slots.json").exists()


def test_the_slot_checks_flag_runs_the_slot_check_and_not_the_pictures(tmp_path, monkeypatch):
    import shots
    called = []
    monkeypatch.setattr(shots, "finish_slots", lambda done, log, pipe, hub: called.append("slots") or 0)
    monkeypatch.setattr(shots, "run", lambda log: called.append("pictures"))
    assert shots.main(["--pipe", "p", "--hub", "h", "--done", str(tmp_path), "--slot-checks"]) == 0
    assert called == ["slots"]
