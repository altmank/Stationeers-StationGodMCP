"""keyguard_check's pure logic, offline: log matching and the verdict.

    py -3.12 -m pytest tools/picture-rig/test_keyguard_check.py
"""
import keyguard_check as k

LOG = "\n".join([
    "[Info   :StationGod] started",
    "[Info   :StationGod] [Ghosts] menu open",
    "[Info   :StationGod] [Ghosts] menu closed",
    "[Info   :StationGod] [Ghosts] menu open",
])


def test_menu_lines_in_order():
    assert [(i, kind) for i, kind, _ in k.menu_lines(LOG)] == [(1, "open"), (2, "closed"), (3, "open")]


def test_lines_after_a_mark():
    assert [kind for _, kind, _ in k.lines_after(LOG, 3)] == ["open"]
    assert k.lines_after(LOG, 4) == []


def test_no_menu_lines():
    assert k.menu_lines("nothing here") == []


def test_control_missing_is_keys_not_reaching():
    failures, _ = k.verdict(False, False, [])
    assert failures and "keys do not reach the game" in failures[0]
    failures, _ = k.verdict(True, False, [])
    assert "no menu closed" in failures[0] and "menu open" not in failures[0].split("saw")[1]


def test_case_open_line_fails():
    failures, _ = k.verdict(True, True, k.lines_after(LOG, 3))
    assert failures and "a ghost key fired" in failures[0]


def test_case_only_closed_line_passes():
    failures, notes = k.verdict(True, True, [(0, "closed", "x")], "Stationeers")
    assert failures == [] and any("Stationeers" in n for n in notes)


def test_clean_case_passes():
    failures, _ = k.verdict(True, True, [])
    assert failures == []
