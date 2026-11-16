"""needs_check's pure logic, offline, and the whole check against a fake game.

    py -3.12 -m pytest tools/picture-rig/test_needs_check.py
"""
import needs_check as c

DIVISOR = 5.0


def player(nutrition=10.0, hydration=1.0, waste=0.1, sanitation=True, capacity=50.0):
    return {"nutrition": nutrition, "nutrition_capacity": capacity, "hydration": hydration,
            "hydration_capacity": DIVISOR, "waste_ratio": waste, "sanitation": sanitation}


def err(code, message=""):
    return {"error": {"code": code, "message": message}}


def written(before, after, waste=True):
    return {"player": {"reference_id": "77"},
            "nutrition": {"before": before["nutrition"], "after": after["nutrition"], "capacity": 50.0},
            "hydration": {"before": before["hydration"], "after": after["hydration"], "capacity": DIVISOR},
            "waste": {"before": before["waste"], "after": after["waste"]} if waste else None,
            "sanitation": waste, "warnings": [{"code": "drains", "message": "x"}], "job_id": "write-3"}


def test_shares_come_from_the_reply_and_the_given_divisor():
    got = c.shares(player(nutrition=10.0, hydration=1.0, waste=0.1), DIVISOR)
    assert got == {"nutrition": 0.2, "hydration": 0.2, "waste": 0.1, "sanitation": True}


def test_asked_values_stay_away_from_what_the_player_reads():
    asked = c.asked_levels({"nutrition": 0.2, "hydration": 0.52, "waste": 0.5})
    assert asked == {"nutrition": 0.5, "hydration": 0.25, "waste": 0.25}
    assert all(abs(asked[k] - v) > c.APART for k, v in {"nutrition": 0.2, "hydration": 0.52, "waste": 0.5}.items())


def good_set(sanitation=True):
    before = {"nutrition": 0.2, "hydration": 0.2, "waste": 0.1 if sanitation else 0.0, "sanitation": sanitation}
    asked = {"nutrition": 0.5, "hydration": 0.5, "waste": 0.5}
    after = {"nutrition": 0.499, "hydration": 0.5, "waste": 0.5}
    ran = {"before": before, "asked": asked, "reply": written(before, after, sanitation)}
    if not sanitation:
        ran["waste_refused"] = err("sanitation_off")
    return ran


def test_a_good_set_passes_with_sanitation_on_and_off():
    assert c.set_failures({"set": good_set()}) == []
    assert c.set_failures({"set": good_set(False)}) == []


def test_a_set_that_reads_the_wrong_value_fails():
    ran = good_set()
    ran["reply"]["hydration"]["after"] = 0.2
    assert any("hydration reads 0.2 after" in f for f in c.set_failures({"set": ran}))


def test_a_before_that_is_not_what_inspect_read_fails():
    ran = good_set()
    ran["reply"]["nutrition"]["before"] = 0.9
    assert any("nutrition before" in f for f in c.set_failures({"set": ran}))


def test_sanitation_off_must_refuse_waste_and_carry_none():
    ran = good_set(False)
    ran["waste_refused"] = {"nutrition": {}}
    assert any("not sanitation_off" in f for f in c.set_failures({"set": ran}))
    ran = good_set(False)
    ran["reply"]["waste"] = {"before": 0, "after": 0}
    assert any("still carries a waste" in f for f in c.set_failures({"set": ran}))


def test_a_refused_set_fails_with_the_reason():
    ran = good_set()
    ran["reply"] = err("cheats_off", "off")
    assert any("refused cheats_off" in f for f in c.set_failures({"set": ran}))


def test_the_drains_warning_is_required():
    ran = good_set()
    ran["reply"]["warnings"] = []
    assert any("no drains" in f for f in c.set_failures({"set": ran}))


def test_the_read_case_compares_inspect_player_to_what_was_asked():
    asked = {"nutrition": 0.5, "hydration": 0.5, "waste": 0.5}
    after = {"nutrition": 0.5, "hydration": 0.49, "waste": 0.5, "sanitation": True}
    assert c.read_failures({"read": {"after": after, "asked": asked, "divisor": DIVISOR}}) == []
    after["waste"] = 0.1
    assert any("waste_ratio 0.1" in f for f in c.read_failures({"read": {"after": after, "asked": asked, "divisor": DIVISOR}}))
    off = {"nutrition": 0.5, "hydration": 0.5, "waste": 0, "sanitation": False}
    assert c.read_failures({"read": {"after": off, "asked": asked, "divisor": DIVISOR}}) == []
    off["waste"] = None
    assert any("no waste_ratio" in f for f in c.read_failures({"read": {"after": off, "asked": asked, "divisor": DIVISOR}}))


def good_undo():
    before = {"nutrition": 0.2, "hydration": 0.2, "waste": 0.1, "sanitation": True}
    return {"job_id": "write-3", "reply": {"undone": 1}, "before": before,
            "after": {"nutrition": 0.2, "hydration": 0.21, "waste": 0.1, "sanitation": True}}


def test_undo_must_restore_every_value():
    assert c.undo_failures({"undo": good_undo()}) == []
    ran = good_undo()
    ran["after"]["waste"] = 0.5
    assert any("waste reads 0.5 after undo" in f for f in c.undo_failures({"undo": ran}))
    ran = good_undo()
    ran["job_id"] = None
    assert any("no job_id" in f for f in c.undo_failures({"undo": ran}))
    ran = good_undo()
    ran["reply"] = err("cannot_undo")
    assert any("undo refused cannot_undo" in f for f in c.undo_failures({"undo": ran}))


def test_the_verdict_judges_exactly_the_three_cases():
    assert c.CASES == ("needs-set", "needs-undo", "needs-read")
    assert c.MARKER == "needs-check ran: needs-set,needs-undo,needs-read"
    asked = {"nutrition": 0.5, "hydration": 0.5, "waste": 0.5}
    evidence = {"set": good_set(), "undo": good_undo(), "marker": c.MARKER,
                "read": {"after": {"nutrition": 0.5, "hydration": 0.5, "waste": 0.5, "sanitation": True},
                         "asked": asked, "divisor": DIVISOR}}
    failures, notes = c.verdict(evidence)
    assert failures == [] and notes == [c.MARKER]


def test_a_case_that_did_not_run_fails_and_so_does_a_missing_marker():
    failures, _ = c.verdict({})
    assert len(failures) == 4 and any("did not run" in f for f in failures)


class FakeGame:
    """A stand-in for sgcall that answers like the game for the calls the check makes: a human with nutrition 10 of 50,
    hydration 1 (of the divisor 5), waste 0.1, and write needs / undo as the mod and the hub answer them."""

    def __init__(self, sanitation=True):
        self.sanitation = sanitation
        self.nutrition, self.hydration, self.waste = 10.0, 1.0, 0.1 if sanitation else 0.0
        self.backs = {}
        self.calls = []

    def call(self, verb, what=None, **o):
        self.calls.append((verb, what, o))
        if (verb, what) == ("inspect", "player"):
            return {"reference_id": "77", "nutrition": self.nutrition, "nutrition_capacity": 50.0,
                    "hydration": self.hydration, "hydration_capacity": 5.0, "waste_ratio": self.waste,
                    "sanitation": self.sanitation}
        if (verb, what) == ("find", "thing"):
            return {"things": [{"position": {"x": 0, "y": 0, "z": 0}}]}
        if (verb, what) == ("write", "needs"):
            return self.write(o)
        if verb == "undo":
            return self.undo(o)
        raise AssertionError((verb, what, o))

    def write(self, o):
        assert o["confirm"] is True
        if "waste" in o and not self.sanitation:
            return err("sanitation_off")
        before = {"nutrition": self.nutrition / 50.0, "hydration": self.hydration / 5.0, "waste": self.waste}
        for need in ("nutrition", "hydration", "waste"):
            if need in o and (need != "waste" or self.sanitation):
                setattr(self, need, o[need] * {"nutrition": 50.0, "hydration": 5.0, "waste": 1.0}[need])
        after = {"nutrition": self.nutrition / 50.0, "hydration": self.hydration / 5.0, "waste": self.waste}
        self.backs["write-1"] = {k: before[k] for k in o if k in before}
        reply = written(before, after, self.sanitation)
        reply["job_id"] = "write-1"
        return reply

    def undo(self, o):
        back = self.backs[o["id"]]
        for need, share in back.items():
            setattr(self, need, share * {"nutrition": 50.0, "hydration": 5.0, "waste": 1.0}[need])
        return {"job_id": o["id"], "undone": 1, "status": "undone"}


def test_the_whole_check_runs_against_a_fake_game(tmp_path, monkeypatch):
    monkeypatch.setattr(c.time, "sleep", lambda s: None)
    game = FakeGame()
    failures, notes = c.run(game, str(tmp_path), {}, say=lambda text: None)
    assert failures == [], failures
    assert (tmp_path / "needs.json").exists()
    assert notes == [c.MARKER]
    assert ("undo", None, {"id": "write-1", "confirm": True}) in game.calls
    assert game.nutrition == 10.0 and game.waste == 0.1


def test_the_whole_check_with_sanitation_off_asserts_sanitation_off(tmp_path, monkeypatch):
    monkeypatch.setattr(c.time, "sleep", lambda s: None)
    game = FakeGame(sanitation=False)
    failures, notes = c.run(game, str(tmp_path), {}, say=lambda text: None)
    assert failures == [], failures
    assert any("Sanitation is off" in n for n in notes)
    assert game.calls[1][2].get("waste") is not None and game.calls[1][2].get("nutrition") is None


def test_a_game_that_changes_nothing_fails_the_check(tmp_path, monkeypatch):
    monkeypatch.setattr(c.time, "sleep", lambda s: None)
    monkeypatch.setattr(c, "SETTLE_SECONDS", 0)
    game = FakeGame()
    game.write = lambda o: written({"nutrition": 0.2, "hydration": 0.2, "waste": 0.1},
                                   {"nutrition": 0.2, "hydration": 0.2, "waste": 0.1}) | {"job_id": "write-1"}
    game.backs["write-1"] = {}
    failures, _ = c.run(game, str(tmp_path), {}, say=lambda text: None)
    assert any("nutrition reads 0.2 after" in f for f in failures)


def test_the_needs_checks_flag_runs_the_needs_check_and_not_the_pictures(tmp_path, monkeypatch):
    import shots
    called = []
    monkeypatch.setattr(shots, "finish_needs", lambda done, log, pipe, hub: called.append("needs") or 0)
    monkeypatch.setattr(shots, "run", lambda log: called.append("pictures"))
    assert shots.main(["--pipe", "p", "--hub", "h", "--done", str(tmp_path), "--needs-checks"]) == 0
    assert called == ["needs"]


def test_finish_needs_writes_the_done_file_with_the_marker(tmp_path, monkeypatch):
    import shots
    monkeypatch.setattr(c.time, "sleep", lambda s: None)
    monkeypatch.setattr(shots.sgcall, "connect", lambda pipe, hub: None)
    monkeypatch.setattr(shots, "wait_player", lambda: None)
    monkeypatch.setattr(shots.sgcall, "wait_ready", lambda: None)
    monkeypatch.setattr(shots.sgcall, "call", FakeGame().call)
    assert shots.finish_needs(str(tmp_path), {}, "p", "h") == 0
    assert "needs-check ran: " in (tmp_path / "done-ok").read_text()
