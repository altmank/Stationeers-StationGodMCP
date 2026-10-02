"""What the client reads from the catalogue: the resend rule's effective class, durations, paging and the
generated name checks."""
import unittest

from _support import stationgod  # noqa: F401
from stationgod import _methods
from stationgod.catalogue import Catalogue, extract

CATALOGUE = Catalogue(_methods.TABLE, _methods.CATALOGUE_HASH)


class EffectiveClassTests(unittest.TestCase):
    def test_a_building_tool_is_read_on_a_dry_run_and_write_on_a_real_one(self):
        self.assertEqual("read", CATALOGUE.effective_class("place_cables", {}))
        self.assertEqual("read", CATALOGUE.effective_class("place_cables", {"dry_run": True}))
        self.assertEqual("write", CATALOGUE.effective_class("place_cables", {"dry_run": False, "confirm": True}))

    def test_rules_follow_the_catalogue_in_order(self):
        self.assertEqual("cheat", CATALOGUE.effective_class("place_structure", {"free": True, "dry_run": False}))
        self.assertEqual("read", CATALOGUE.effective_class("move_gas", {"dry_run": True}))
        self.assertEqual("read", CATALOGUE.effective_class("move_gas", {"transfer_id": "t1"}))
        self.assertEqual("cheat", CATALOGUE.effective_class("move_gas", {"from": "1", "to": "2"}))
        self.assertEqual("read", CATALOGUE.effective_class("plant_genes", {"reference_id": "5"}))
        self.assertEqual("read", CATALOGUE.effective_class("rocket_flight_log", {"action": " LIST "}))
        self.assertEqual("write", CATALOGUE.effective_class("rocket_flight_log", {"action": "start"}))

    def test_null_is_an_omitted_argument(self):
        self.assertEqual("read", CATALOGUE.effective_class("place_cables", {"dry_run": None}))

    def test_an_unknown_method_has_no_class(self):
        self.assertIsNone(CATALOGUE.effective_class("no_such_method", {}))


class ResendTests(unittest.TestCase):
    def test_reads_are_safe_to_resend(self):
        self.assertTrue(CATALOGUE.resend_safe("find_things", {}))
        self.assertTrue(CATALOGUE.resend_safe("place_cables", {}))
        self.assertTrue(CATALOGUE.resend_safe("catalogue", {}))

    def test_effects_writes_unknown_methods_and_subscriptions_are_not(self):
        self.assertFalse(CATALOGUE.resend_safe("highlight", {}))
        self.assertFalse(CATALOGUE.resend_safe("show_preview", {}))
        self.assertFalse(CATALOGUE.resend_safe("write_logic", {}))
        self.assertFalse(CATALOGUE.resend_safe("place_cables", {"dry_run": False}))
        self.assertFalse(CATALOGUE.resend_safe("no_such_method", {}))
        self.assertFalse(CATALOGUE.resend_safe("subscribe", {}))


class DurationAndPagingTests(unittest.TestCase):
    def test_x_duration_adds_the_argument_or_the_maximum(self):
        self.assertEqual(12.0, CATALOGUE.duration_s("sample_logic", {"duration_seconds": 12}))
        self.assertEqual(30.0, CATALOGUE.duration_s("sample_logic", {}))
        self.assertEqual(0.0, CATALOGUE.duration_s("game_clock", {}))

    def test_paging_names_the_list_and_arguments(self):
        paging = CATALOGUE.paging("find_things")
        self.assertEqual(("things", "offset", "limit", "has_more"),
                         (paging["list"], paging["offset"], paging["limit"], paging["has_more"]))
        self.assertIsNone(CATALOGUE.paging("game_clock"))


class CheckTests(unittest.TestCase):
    def test_an_unknown_name_is_refused_with_the_nearest(self):
        problems = CATALOGUE.check("find_things", {"prefab": "x"})
        self.assertEqual("prefab", problems[0]["path"])
        self.assertIn("prefab_contains", problems[0]["problem"])

    def test_a_missing_required_argument_is_refused(self):
        problems = CATALOGUE.check("read_logic", {})
        self.assertTrue(any(problem["problem"].endswith("is required.") for problem in problems))

    def test_types_and_ranges_are_the_mods_to_check(self):
        self.assertEqual([], CATALOGUE.check("find_things", {"limit": "not a number"}))

    def test_extract_keeps_protocol_methods_a_catalogue_does_not_list(self):
        table = extract({"methods": [], "protocol_methods": []})
        self.assertEqual({"catalogue", "subscribe", "unsubscribe"}, set(table))


if __name__ == "__main__":
    unittest.main()
