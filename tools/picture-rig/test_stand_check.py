"""stand_check's pure logic and a whole run against a fake game, offline.

    py -3.12 -m pytest tools/picture-rig/test_stand_check.py
"""
import json
import os
import re

import stand_check as c

CATALOGUE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "catalogue", "verbs")


def declared_keys(text):
    """The top-level key names of a catalogue shape such as list[{a: int, b: {c: x}}] (brace depth 1 only)."""
    inner = text[text.index("{") + 1: text.rindex("}")]
    depth, start, parts = 0, 0, []
    for i, ch in enumerate(inner):
        if ch in "{[(":
            depth += 1
        elif ch in "}])":
            depth -= 1
        elif ch == "," and depth == 0:
            parts.append(inner[start:i])
            start = i + 1
    parts.append(inner[start:])
    return [part.split(":", 1)[0].strip() for part in parts if ":" in part]


def plan_spot_shape():
    spot = json.load(open(os.path.join(CATALOGUE, "plan.json"), encoding="utf-8"))["whats"]["spot"]
    keys = spot["reply"]["variants"][0]["keys"]
    return keys, declared_keys(keys["spots"])


def err(code, message=""):
    return {"error": {"code": code, "message": message}}


ROWS = [{"index": 0, "name": "Power", "action": "OnOff", "state": 0, "states": 2},
        {"index": 1, "name": "Mode", "action": "Mode", "state": 1, "states": None}]


def good_clear():
    reply = {"stand": {"position": {"x": 10, "y": 0, "z": 0}, "distance_m": 3, "eye_to_target_m": 1.6, "floor": "terrain",
                       "tried": 40, "rejected": [{"reason": "overlaps 7", "count": 3}]},
             "after": {"position": {"x": 10, "y": 0, "z": 0}}}
    return {"reach_m": 2.5, "reply": reply, "bodies": {"7": ({"x": 8, "y": 0, "z": 0}, 0.5), "8": ({"x": 9, "y": 0, "z": 1.0}, 0.5)}}


def good_interact():
    return {"rows": ROWS, "far_m": 10.0,
            "pressed": {"thing": {}, "interactable": {"name": "Power", "index": 0}, "before": 0, "after": 1, "player": "p"},
            "rows_after": [dict(ROWS[0], state=1), ROWS[1]],
            "unknown": err("interactable_not_found", "has: 0: Power, 1: Mode"), "far": err("out_of_range")}


def good_none():
    return {"reply": err("no_clear_stand", "9 points tried; overlaps 5: 9"), "before": {"x": 0, "y": 0, "z": 0},
            "after": {"x": 0, "y": 0, "z": 0}}


def test_a_good_run_passes():
    evidence = {"clear": good_clear(), "none": good_none(), "interact": good_interact(), "marker": c.MARKER}
    failures, notes = c.verdict(evidence)
    assert failures == []
    assert c.MARKER in notes
    assert any("read 0 before the press" in n for n in notes)


def test_a_stand_beyond_the_reach_asked_fails():
    clear = good_clear()
    clear["reply"]["stand"]["eye_to_target_m"] = 2.9
    assert any("over the reach" in f for f in c.clear_failures({"clear": clear}))


def test_feet_inside_a_body_fail():
    clear = good_clear()
    clear["reply"]["stand"]["position"] = {"x": 8.2, "y": 0, "z": 0}
    clear["reply"]["after"]["position"] = {"x": 8.2, "y": 0, "z": 0}
    assert any("inside body 7" in f for f in c.clear_failures({"clear": clear}))


def test_a_refused_stand_or_a_missing_record_fails():
    assert any("refused no_clear_stand" in f for f in c.clear_failures({"clear": {"reply": err("no_clear_stand")}}))
    assert any("no stand record" in f for f in c.clear_failures({"clear": {"reply": {}, "reach_m": 2.5}}))


def test_a_refusal_must_be_no_clear_stand_with_counts_and_move_nothing():
    none = good_none()
    none["after"] = {"x": 3, "y": 0, "z": 0}
    assert any("moved" in f for f in c.none_failures({"none": none}))
    none = good_none()
    none["reply"] = {"stand": {}}
    assert any("not no_clear_stand" in f for f in c.none_failures({"none": none}))
    none = good_none()
    none["reply"] = err("no_clear_stand", "nothing")
    assert any("no counts" in f for f in c.none_failures({"none": none}))


def test_a_press_that_changes_nothing_or_reads_back_wrong_fails():
    press = good_interact()
    press["pressed"]["after"] = 0
    assert any("did not change" in f for f in c.interact_failures({"interact": press}))
    press = good_interact()
    press["rows_after"] = ROWS
    assert any("read back" in f for f in c.interact_failures({"interact": press}))


def test_the_unknown_and_far_cases_must_be_refused_as_named():
    press = good_interact()
    press["unknown"] = err("interactable_not_found", "no names")
    assert any("does not list the names" in f for f in c.interact_failures({"interact": press}))
    press = good_interact()
    press["far"] = {"after": 1}
    assert any("not out_of_range" in f for f in c.interact_failures({"interact": press}))


def test_the_verdict_judges_exactly_the_three_cases():
    failures, _ = c.verdict({"marker": c.MARKER})
    assert sorted(f.split(":")[0] for f in failures) == ["interact", "stand-clear", "stand-none"]
    assert all("not run" in f for f in failures)


def test_a_full_run_without_the_marker_fails():
    evidence = {"clear": good_clear(), "none": good_none(), "interact": good_interact()}
    failures, notes = c.verdict(evidence)
    assert any("marker" in f for f in failures) and notes == [] or any("marker" in f for f in failures)


def test_the_pressable_row_is_read_from_the_rows_not_named():
    assert c.pressable(ROWS)["name"] == "Power"
    assert c.pressable([ROWS[1]]) is None


class FakeGame:
    """A tiny world: structures with positions, a player, a battery's Power interactable, and the build rules the check
    relies on: placements are reported first, a battery needs a frame in the cell below it, builds answer a queued job
    and then a final BuildJob (result.placed[].reference_id), and a ring of frames in a battery's layer boxes it in."""

    def __init__(self):
        self.player = {"x": 0.0, "y": 0.0, "z": 0.0}
        self.things = {}
        self.next_id = 100
        self.power = 0
        self.jobs = {}
        self.reports = []

    def call(self, verb, what=None, **o):
        if (verb, what) == ("find", "thing"):
            if o.get("runtime_type") == "Human":
                return {"things": [{"reference_id": "1", "position": dict(self.player)}]}
            return {"things": [{"reference_id": i, "position": p} for i, (_, p) in self.things.items()]}
        if (verb, what) == ("build", "structure"):
            return self.build_structure(o)
        if (verb, what) == ("inspect", "job"):
            return self.jobs[o["id"]]
        if (verb, what) == ("inspect", "prefab"):
            return {"render_box": {"size": {"x": 1.0, "y": 1.0, "z": 1.0}}}
        if (verb, what) == ("inspect", "device"):
            assert o["include"] == ["interactables"], o
            return {"results": [{"payload": {"interactables": [dict(ROWS[0], state=self.power), ROWS[1]]}}]}
        if (verb, what) == ("move", "player"):
            return self.move(o)
        if (verb, what) == ("write", "interact"):
            return self.press(o)
        raise AssertionError((verb, what, o))

    def problems(self, rows):
        found = []
        for index, row in enumerate(rows):
            at = row["at"]
            if row["prefab"] == c.BATTERY and not any(
                    prefab == c.FRAME and abs(p["x"] - at[0]) < 0.01 and abs(p["y"] - (at[1] - 2.0)) < 0.01
                    and abs(p["z"] - at[2]) < 0.01 for prefab, p in self.things.values()):
                found.append({"index": index, "code": "cannot_place",
                              "message": "Placement requires a Frame below for support"})
        return found

    def build_structure(self, o):
        assert "max_wait" not in o and o["free"] is True and "prefab" not in o, o
        rows = o["placements"]
        assert len(rows) <= 64, len(rows)
        if not o.get("confirm"):
            found = self.problems(rows)
            self.reports.append(found)
            return {"ready": not found, "problems": found, "placements": rows}
        assert not self.problems(rows), rows
        placed = []
        for row in rows:
            self.next_id += 1
            made = str(self.next_id)
            at = row["at"]
            self.things[made] = (row["prefab"], {"x": at[0], "y": at[1], "z": at[2]})
            placed.append({"index": len(placed), "reference_id": made, "prefab_name": row["prefab"]})
        job = "J" + str(self.next_id)
        self.jobs[job] = {"job_id": job, "status": "applied", "result": {"placed": placed}}
        return {"status": "queued", "job_id": job}

    def move(self, o):
        if "at" in o:
            self.player = {"x": o["at"][0], "y": o["at"][1], "z": o["at"][2]}
            return {"after": {"position": dict(self.player)}}
        target = self.things[o["to"]][1]
        boxed = any(prefab == c.FRAME and abs(p["y"] - target["y"]) < 0.5 and abs(p["x"] - target["x"]) < 2.5
                    and abs(p["z"] - target["z"]) < 2.5 for prefab, p in self.things.values())
        if boxed:
            return err("no_clear_stand", "30 points tried; overlaps 9: 30")
        feet = {"x": target["x"] + 1.2, "y": target["y"], "z": target["z"] - 1.0}
        self.player = dict(feet)
        return {"stand": {"position": feet, "distance_m": 1, "eye_to_target_m": 1.6, "floor": "terrain", "tried": 8,
                          "rejected": []}, "after": {"position": dict(feet)}}

    def press(self, o):
        rows = [ROWS[0]["name"], ROWS[1]["name"]]
        if o["interactable"] not in rows:
            return err("interactable_not_found", "has: " + ", ".join(f"{i}: {n}" for i, n in enumerate(rows)))
        target = self.things[o["id"]][1]
        if abs(self.player["x"] - target["x"]) > 3 or abs(self.player["z"] - target["z"]) > 3:
            return err("out_of_range", "far")
        before = self.power
        self.power = 1 - self.power
        return {"thing": {}, "interactable": {"name": o["interactable"], "index": 0}, "before": before,
                "after": self.power, "player": "p"}


def test_the_whole_check_runs_against_a_fake_game(tmp_path):
    game = FakeGame()
    failures, notes = c.run(game, str(tmp_path), {}, say=lambda text: None)
    assert failures == [], failures
    assert (tmp_path / "stand.json").exists()
    assert c.MARKER in notes
    assert all(report == [] for report in game.reports), game.reports
    batteries = [p for prefab, p in game.things.values() if prefab == c.BATTERY]
    assert len(batteries) == 4


def test_the_floor_is_laid_on_odd_cell_centres_a_battery_stands_one_cell_up():
    floor = c.Floor(1194.0, 73.5, 512.0)
    assert (floor.x0, floor.floor_y, floor.z0) == (1195, 73, 513)
    assert floor.cell(1, 1) == [1197, 75, 515]
    assert len(floor.frames()) == c.WIDTH * c.DEPTH


def test_a_refused_floor_goes_higher_and_every_refusal_is_recorded_verbatim():
    class LowRefused(FakeGame):
        def build_structure(self, o):
            rows = o["placements"]
            if not o.get("confirm") and rows[0]["at"][1] < 80:
                return {"ready": False, "problems": [{"index": 0, "code": "terrain", "message": "in the way"}]}
            return super().build_structure(o)

    game, log = LowRefused(), {}
    floor = c.lay_floor(game, [0.0, 70.0, 0.0], log)
    assert floor.floor_y > 80
    refused = [entry for entry in log["floor"] if entry["ready"] is False]
    assert refused and refused[0]["problems"][0]["message"] == "in the way"


def test_a_frame_ring_that_is_refused_stops_the_check_with_the_replies():
    class Refuses(FakeGame):
        def build_structure(self, o):
            if len(o["placements"]) == 8:
                return {"ready": False, "problems": [{"index": 0, "code": "overlaps_placement", "message": "taken"}]}
            return super().build_structure(o)

    game, log = Refuses(), {}
    floor = c.lay_floor(game, [0.0, 70.0, 0.0], log)
    try:
        c.none_part(game, log, floor)
    except SystemExit as stop:
        assert "frame ring" in str(stop) and "taken" in str(stop)
    else:
        raise AssertionError("expected the check to stop")


def test_a_battery_without_the_floor_below_is_reported_not_ready_and_never_built():
    game, log = FakeGame(), {}
    assert c.place_one(game, c.BATTERY, [1.0, 1.0, 1.0], log, "b", "x") is None
    assert log["b"][0]["ready"] is False and "Frame below" in str(log["b"][0]["problems"])
    assert not game.things


def test_the_stand_checks_flag_runs_the_stand_check_and_not_the_pictures(tmp_path, monkeypatch):
    import shots
    called = []
    monkeypatch.setattr(shots, "finish_stand", lambda done, log, pipe, hub: called.append("stand") or 0)
    monkeypatch.setattr(shots, "run", lambda log: called.append("pictures"))
    assert shots.main(["--pipe", "p", "--hub", "h", "--done", str(tmp_path), "--stand-checks"]) == 0
    assert called == ["stand"]


def test_finish_stand_writes_done_fail_with_the_failures(tmp_path, monkeypatch):
    import shots
    monkeypatch.setattr(shots.sgcall, "connect", lambda pipe, hub: None)
    monkeypatch.setattr(shots, "wait_player", lambda: None)
    monkeypatch.setattr(shots.sgcall, "wait_ready", lambda: None)
    monkeypatch.setattr(shots.stand_check, "run", lambda sg, done, log, say: (["stand-none: not run"], []))
    assert shots.finish_stand(str(tmp_path), {}, "p", "h") == 1
    assert "stand-none: not run" in (tmp_path / "done-fail").read_text()
