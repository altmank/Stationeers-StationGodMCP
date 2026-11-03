"""py -3.12 -m unittest discover -s tools/silo-rig -p "test_*.py"   (no game: the calls are faked)"""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import silo_checks as sc  # noqa: E402

FIXTURES = {
    "silo_ore": {"id": "11", "prefab": "StructureSDBSilo"},
    "silo_room": {"id": "12", "prefab": "StructureSDBSilo"},
    "silo_full": {"id": "13", "prefab": "StructureSDBSilo"},
    "locker": {"id": "20", "prefab": "StructureStorageLocker"},
    "tray": {"id": "30", "prefab": "StructureHydroponicsTray"},
    "suit": {"id": 40, "prefab": "ItemHardSuit"},
    "backpack_full": {"id": "50", "prefab": "ItemHardMiningBackPack"},
    "backpacks_spare": {"id": ["51", "52"], "prefab": "ItemHardMiningBackPack"},
    "food_fresh": {"id": ["60", "61"], "prefab": "ItemCookedPumpkin"},
    "ground": {"id": None},
}


def fake_call(verb, what, **options):
    if (verb, what) == ("find", "plant"):
        return {"plants": [{"reference_id": "99", "planted": False, "tray": None},
                           {"reference_id": "31", "planted": True, "tray": {"reference_id": "30"}}]}
    if (verb, what) == ("inspect", "contents"):
        return {"results": [{"silo": {"count": 600 if options["id"] == "13" else 3}}]}
    raise AssertionError(f"unexpected call {verb} {what} {options}")


class FixtureTests(unittest.TestCase):
    def setUp(self):
        self.saved = sc.call
        sc.call = fake_call

    def tearDown(self):
        sc.call = self.saved

    def test_roles_to_things(self):
        t = sc.from_fixtures(FIXTURES)
        self.assertEqual((t["ore_silo"], t["roomy_silo"], t["full_silo"], t["full_count"]), ("11", "12", "13", 600))
        self.assertEqual((t["locker"], t["suit"], t["full_pack"]), ("20", "40", "50"))
        self.assertEqual(t["spare_packs"], ["51", "52"])
        self.assertEqual(t["plant"], "31")          # the plant growing in the tray fixture, not a loose seed bag
        self.assertEqual(t["food"], ["60", "61"])

    def test_a_missing_role_is_none(self):
        less = {k: v for k, v in FIXTURES.items() if k not in ("silo_full", "tray", "food_fresh")}
        t = sc.from_fixtures(less)
        self.assertIsNone(t["full_silo"])
        self.assertIsNone(t["full_count"])
        self.assertIsNone(t["plant"])
        self.assertEqual(t["food"], [])

    def test_ids_of(self):
        self.assertEqual(sc.ids_of({"id": 5}), ["5"])
        self.assertEqual(sc.ids_of({"id": ["1", None, 2]}), ["1", "2"])
        self.assertEqual(sc.ids_of({"id": None}), [])
        self.assertEqual(sc.ids_of(None), [])


def row(rid, prefab=sc.FOOD, decay=None, condition="intact", quantity=4.0):
    return {"reference_id": rid, "prefab_name": prefab, "quantity": quantity, "condition": condition,
            "decay_time_left_s": decay}


class FoodTests(unittest.TestCase):
    def test_fresh_food_by_prefab_even_when_not_decaying(self):
        # In a vacuum the decay rate is 0, so find food gives no time left; the food still rots out of a silo.
        found = {"60": row("60"), "61": row("61"), "70": row("70", prefab=sc.SPOILED), "62": row("62", decay=900)}
        self.assertEqual([r["reference_id"] for r in sc.pick_fresh(found)], ["60", "61"])

    def test_fresh_food_by_fixture_ids(self):
        found = {"60": row("60"), "61": row("61"), "62": row("62")}
        self.assertEqual([r["reference_id"] for r in sc.pick_fresh(found, ["62", "61", "404"])], ["62", "61"])
        self.assertEqual(sc.pick_fresh(found, ["404"]), [])

    def test_same_as_export(self):
        whole = {"taken": [{"whole": True}]}
        spoiled = sc.outcome(row("1", prefab=sc.SPOILED))
        broken = sc.outcome(row("1", condition="broken"))
        fresh = sc.outcome(row("1"))
        self.assertTrue(sc.same_as_export(whole, spoiled, dict(spoiled)))
        self.assertTrue(sc.same_as_export(whole, broken, dict(broken)))
        self.assertFalse(sc.same_as_export(whole, fresh, dict(fresh)), "fresh food out is not spoiled")
        self.assertFalse(sc.same_as_export(whole, spoiled, broken), "differs from the silo's export")
        self.assertFalse(sc.same_as_export(whole, spoiled, sc.outcome(row("2", prefab=sc.SPOILED, quantity=3.0))))
        self.assertFalse(sc.same_as_export({"taken": [{"whole": False}]}, spoiled, dict(spoiled)), "pooled")
        self.assertFalse(sc.same_as_export({}, spoiled, dict(spoiled)), "nothing taken")


class DoneTests(unittest.TestCase):
    def setUp(self):
        sc.RESULTS.clear()
        sc.LINES.clear()

    def test_done_file_names_the_end_and_holds_each_line(self):
        sc.record("a", True)
        sc.record("b", None, "why")
        with tempfile.TemporaryDirectory() as d:
            sc.write_done(d, 0)
            self.assertEqual(os.listdir(d), ["done-ok"])
            with open(os.path.join(d, "done-ok"), encoding="utf-8") as f:
                text = f.read()
        self.assertIn("PASS  a", text)
        self.assertIn("SKIP  b  -- why", text)
        self.assertIn("2 checks, 0 failed", text)
        sc.record("c", False)
        with tempfile.TemporaryDirectory() as d:
            sc.write_done(d, 1)
            self.assertEqual(os.listdir(d), ["done-fail"])

    def test_no_folder_writes_nothing(self):
        sc.write_done(None, 0)


class LineTests(unittest.TestCase):
    """The lines vm-run-silo-checks.ps1's phases match (Get-SiloPhases); test-silo-vm-lib.ps1 matches them too."""

    def setUp(self):
        sc.RESULTS.clear()
        sc.LINES.clear()

    def test_summary_line(self):
        sc.record("a", True)
        sc.record("b", None)
        sc.record("c", False)
        self.assertEqual(sc.summary_line(), "3 checks: 2 passed or skipped, 1 failed")

    def test_progress_is_not_a_result_line(self):
        import io
        from contextlib import redirect_stdout
        out = io.StringIO()
        with redirect_stdout(out):
            sc.progress("world running: {}")
        self.assertEqual(out.getvalue(), "...   world running: {}\n")
        self.assertEqual(sc.LINES, [], "progress lines stay out of the done file")


class ExportPickTests(unittest.TestCase):
    """The export compared with the withdrawal is never the withdrawn item, whatever ids spoiling hands out."""
    LOCKER = "465"

    def item(self, rid, prefab=sc.SPOILED, holder=None):
        return {"reference_id": rid, "prefab_name": prefab, "quantity": 4.0, "condition": None,
                "held_in": [{"reference_id": holder}] if holder else []}

    def test_spoiled_swap_in_the_locker_is_not_the_export(self):
        # The withdrawal's item 100 spoiled into 101 in the locker after `before` was taken: not the export.
        items = {"101": self.item("101", holder=self.LOCKER), "200": self.item("200")}
        self.assertEqual(sc.pick_exported(items, {"100"}, self.LOCKER)["reference_id"], "200")
        self.assertIsNone(sc.pick_exported({"101": self.item("101", holder=self.LOCKER)}, set(), self.LOCKER))

    def test_known_items_are_not_the_export(self):
        items = {"50": self.item("50"), "200": self.item("200", holder="ground-ish")}
        self.assertEqual(sc.pick_exported(items, {"50"}, self.LOCKER)["reference_id"], "200")
        self.assertIsNone(sc.pick_exported({"50": self.item("50")}, {"50"}, self.LOCKER))

    def test_withdrawn_by_id_or_in_the_locker(self):
        items = {"100": self.item("100", prefab=sc.FOOD, holder=self.LOCKER), "200": self.item("200")}
        self.assertEqual(sc.pick_withdrawn(items, "100", self.LOCKER)["reference_id"], "100")
        swapped = {"101": self.item("101", holder=self.LOCKER), "200": self.item("200")}
        self.assertEqual(sc.pick_withdrawn(swapped, "100", self.LOCKER)["reference_id"], "101")


class DoneFileTests(unittest.TestCase):
    def setUp(self):
        sc.RESULTS.clear()
        sc.LINES.clear()

    def test_written_whole_then_renamed(self):
        import tempfile
        sc.record("a", True)
        with tempfile.TemporaryDirectory() as d:
            sc.write_done(d, 0)
            self.assertEqual(sorted(os.listdir(d)), ["done-ok"], "no part file left behind")
            with open(os.path.join(d, "done-ok"), encoding="utf-8") as f:
                self.assertEqual(f.read().splitlines()[-1], sc.done_trailer(1, 0))
        self.assertEqual(sc.done_trailer(14, 0), "14 checks, 0 failed")


class WorldTests(unittest.TestCase):
    def setUp(self):
        sc.RESULTS.clear()
        sc.LINES.clear()
        self.saved = sc.sgcall.wait_ready

    def tearDown(self):
        sc.sgcall.wait_ready = self.saved

    def test_world_never_loaded_fails_at_once(self):
        def never(timeout_s, on_wait):
            on_wait({"error": {"code": "game_unavailable"}})
            raise SystemExit(f"not ready after {timeout_s} s")
        sc.sgcall.wait_ready = never
        self.assertIsNone(sc.wait_for_world(7))
        self.assertEqual(sc.RESULTS, [(sc.WORLD_CHECK, "FAIL")])
        self.assertIn("world never loaded within 7 s", sc.LINES[0])
        self.assertIn("game_unavailable", sc.LINES[0])

    def test_world_running(self):
        sc.sgcall.wait_ready = lambda timeout_s, on_wait: {"paused": False}
        self.assertEqual(sc.wait_for_world(7), {"paused": False})
        self.assertEqual(sc.RESULTS, [])


class WaitReadyTests(unittest.TestCase):
    def test_on_wait_each_retry(self):
        import sgcall
        replies = iter([{"error": {"code": "not_ready"}}, {"ok": 1}, {"paused": False, "game_time_s": 1.0}])
        saved_call = sgcall.call
        sgcall.call = lambda verb, what=None, **o: next(replies)
        import time
        real_sleep = time.sleep
        time.sleep = lambda s: None
        seen = []
        try:
            clock = sgcall.wait_ready(timeout_s=5, on_wait=seen.append)
        finally:
            sgcall.call = saved_call
            time.sleep = real_sleep
        self.assertEqual(clock, {"paused": False, "game_time_s": 1.0})
        self.assertEqual(seen, [{"error": {"code": "not_ready"}}])


if __name__ == "__main__":
    unittest.main()
