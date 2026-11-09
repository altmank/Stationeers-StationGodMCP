"""judge_shots offline: a replay of the run that passed with no player in shot must fail, and a good log must pass.

    py -3.12 -m pytest tools/picture-rig/test_judge_shots.py
"""
import copy
import json
import math
import os

import judge_shots

HERE = os.path.dirname(os.path.abspath(__file__))


def replay():
    with open(os.path.join(HERE, "fixtures", "seated-run-replies.json"), encoding="utf-8") as f:
        return json.load(f)


def shot(yaw, front, width=1920):
    radians = math.radians(yaw)
    right = (front[1], -front[0])
    out = (front[0] * math.cos(radians) + right[0] * math.sin(radians),
           front[1] * math.cos(radians) + right[1] * math.sin(radians))
    return {"path": "x.png", "bytes": 1, "width": width, "occluded_by": None, "render_ms": 3.0,
            "target": {"id": "201", "front": {"x": front[0], "z": front[1]}},
            "camera": {"yaw": float(yaw), "position": {"x": 10 + out[0] * 2.7, "y": 1.0, "z": 20 + out[1] * 2.7},
                       "look_at": {"x": 10.0, "y": 1.0, "z": 20.0}}}


def good(front=(0.0, -1.0)):
    yaws = {"scene_front_before": 0, "neutral_front": 0, "neutral_side": 90, "neutral_back": 180, "scene_front": 0,
            "scene_side": 90, "scene_back": 180, "neutral_4096": 0}
    log = {key: shot(yaw, front) for key, yaw in yaws.items()}
    log["player"] = {"reference_id": "201", "location": "world", "held_in": None}
    log["worn"] = {"results": [{"slots": [{"slot": 3, "prefab_name": "ItemEvaSuit"},
                                          {"slot": 4, "prefab_name": "ItemJetpackBasic"},
                                          {"slot": 6, "prefab_name": "UniformOrangeJumpSuit"}]}]}
    log["view"] = {"forward": {"x": front[0], "y": 0.1, "z": front[1]}, "seated": False, "third_person": False}
    log["refusals"] = {"escape": {"error": {"code": "bad_path"}}, "device": {"error": {"code": "bad_path"}},
                       "exists": {"error": {"code": "file_exists"}}}
    return log


def test_the_run_with_no_player_in_shot_fails_as_occluded_and_seated():
    failures, _ = judge_shots.judge(replay())

    assert any("in a seat" in f for f in failures)
    assert any("occluded by" in f for f in failures)
    # Its neutral_front said occluded_by None through the window: the seat still fails that picture.
    assert any(f.startswith("neutral_front:") and "in a seat" in f for f in failures)


def test_a_good_log_passes_from_the_independent_front():
    failures, notes = judge_shots.judge(good())

    assert failures == []
    assert "view.forward" in notes[0]


def test_the_north_front_bug_fails():
    # The body faces south, the reply stands the camera north: the defect Aegis's run showed.
    log = good((0.0, -1.0))
    for key in judge_shots.PICTURES:
        reply = shot(log[key]["camera"]["yaw"], (0.0, 1.0))
        log[key] = reply

    failures, _ = judge_shots.judge(log)

    assert any("degrees off" in f for f in failures)
    assert any("target.front" in f for f in failures)


def test_a_body_facing_near_north_passes_with_a_note():
    bearing = math.radians(15)
    front = (math.sin(bearing), math.cos(bearing))

    failures, notes = judge_shots.judge(good(front))

    assert failures == []
    assert any("yaw fix not distinguishable" in n for n in notes)


def test_twenty_five_degrees_from_north_is_enough():
    bearing = math.radians(25)

    failures, notes = judge_shots.judge(good((math.sin(bearing), math.cos(bearing))))

    assert failures == []
    assert not any("north" in n for n in notes)


def test_no_suit_or_no_jetpack_fails():
    log = good()
    log["worn"]["results"][0]["slots"] = [{"slot": 6, "prefab_name": "UniformOrangeJumpSuit"}]

    failures, _ = judge_shots.judge(log)

    assert any("does not wear" in f for f in failures)


def test_a_mirrored_side_fails():
    log = good()
    log["neutral_side"] = shot(270, (0.0, -1.0))
    log["neutral_side"]["camera"]["yaw"] = 90.0

    failures, _ = judge_shots.judge(log)

    assert any(f.startswith("neutral_side:") for f in failures)


def test_a_picture_of_something_else_fails():
    log = good()
    log["scene_back"]["target"]["id"] = "39"

    failures, _ = judge_shots.judge(log)

    assert any("not the player" in f for f in failures)


def test_a_missing_picture_or_refusal_fails():
    log = good()
    del log["neutral_4096"]
    log["refusals"]["device"] = {"path": "con.png"}

    failures, _ = judge_shots.judge(log)

    assert any(f.startswith("neutral_4096:") for f in failures)
    assert any("refusal device" in f for f in failures)


def test_without_an_independent_read_target_front_stands_in_and_says_so():
    log = copy.deepcopy(good())
    del log["view"]

    failures, notes = judge_shots.judge(log)

    assert failures == []
    assert "by eye" in notes[0]


def test_a_seated_view_is_not_a_read_of_the_front():
    log = good()
    log["view"]["seated"] = True

    assert judge_shots.body_front(log) == (None, None)
