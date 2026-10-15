"""Verb calls (params and line), reply dicts, the main list's paging, and the error mapping."""
import copy
import pickle
import unittest

from _support import HubCase, ok, refused
import sg
from sg._errors import _PassEnd
from sg._replies import MainList, plain


class VerbCallTests(HubCase):
    def test_a_verb_call_sends_verb_what_args_and_line_none_outside_a_run(self):
        sg.find("thing", prefab="*Battery*", near="player", radius=30, limit=None, extra_option=True)
        self.assertEqual([{"verb": "find", "what": "thing", "line": None,
                           "args": {"prefab": "*Battery*", "near": "player", "radius": 30, "extra_option": True}}],
                         self.params("verb"))

    def test_undo_takes_an_id_and_sends_no_what(self):
        sg.undo(id="script-4")
        self.assertEqual([{"verb": "undo", "args": {"id": "script-4"}, "line": None}], self.params("verb"))

    def test_inside_a_run_the_line_is_the_code_line_that_called(self):
        self.run_code("x = 1\n\nsg.read('logic', id='5', logic='On')\ndef f():\n    sg.find('thing')\nf()")
        self.assertEqual([3, 5], [params["line"] for params in self.params("verb")])


class ReplyTests(HubCase):
    def test_iterating_a_reply_names_its_main_list(self):
        self.handler = lambda method, params: ok({"things": [{"reference_id": "1"}], "count": 1})
        reply = sg.find("thing")
        with self.assertRaises(TypeError) as raised:
            for _ in reply:
                pass
        self.assertIn("iterate r['things'] (the main list)", str(raised.exception))

    def test_a_per_id_results_reply_names_results(self):
        self.handler = lambda method, params: ok({"results": [{"index": 0, "ok": True}], "count": 1})
        with self.assertRaises(TypeError) as raised:
            list(sg.inspect("device", id=["1", "2"]))
        self.assertIn("iterate r['results']", str(raised.exception))

    def test_iterating_a_reply_without_a_list_lists_its_keys(self):
        self.handler = lambda method, params: ok({"game_time_s": 5.0, "paused": False})
        with self.assertRaises(TypeError) as raised:
            list(sg.read("clock"))
        self.assertIn("its keys: game_time_s, paused", str(raised.exception))

    def test_a_reply_is_still_a_dict(self):
        self.handler = lambda method, params: ok({"things": [], "count": 0})
        reply = sg.find("thing")
        self.assertIsInstance(reply, dict)
        self.assertEqual({"things": [], "count": 0}, dict(reply))
        self.assertEqual(["things", "count"], list(reply.keys()))

    def test_a_missing_key_carries_the_keys_the_reply_has(self):
        self.handler = lambda method, params: ok({"things": [{"reference_id": "1"}], "count": 1})
        reply = sg.find("thing")
        with self.assertRaises(KeyError) as raised:
            reply["thngs"]
        self.assertEqual(["things", "count"], raised.exception.reply_keys)
        with self.assertRaises(KeyError) as raised:
            reply["things"][0]["id"]
        self.assertEqual(["reference_id"], raised.exception.reply_keys)


class MainListTests(HubCase):
    PAGES = {None: ([1, 2], "c1"), "c1": ([3, 4], "c2"), "c2": ([5], None)}

    def setUp(self):
        super().setUp()

        def handler(method, params):
            if method == "verb":
                entries, cursor = self.PAGES[None]
                return ok({"things": entries, "cursor": cursor, "main": "things", "count": 2})
            entries, cursor = self.PAGES[params["cursor"]]
            return ok({"entries": entries, **({"cursor": cursor} if cursor else {})})

        self.handler = handler

    def test_walk_goes_page_by_page_and_only_when_asked(self):
        things = sg.find("thing")["things"]
        self.assertIsInstance(things, MainList)
        self.assertTrue(things.more)
        seen = []
        for thing in things.walk():
            seen.append((thing, len(self.hub.calls)))
        self.assertEqual([(1, 1), (2, 1), (3, 2), (4, 2), (5, 3)], seen)
        self.assertEqual([{"cursor": "c1", "line": None}, {"cursor": "c2", "line": None}], self.params("more"))
        self.assertFalse(things.more)

    def test_reading_a_reply_fetches_no_page(self):
        things = sg.find("thing")["things"]
        self.assertEqual(2, len(things))
        self.assertEqual([1, 2], list(things))
        self.assertEqual(2, things[-1])
        self.assertEqual([2, 1], sorted(things, reverse=True))
        self.assertNotIn(4, things)
        self.assertEqual([1, 2], plain(things))
        self.assertEqual(["verb"], self.hub.methods())

    def test_all_pages_in_the_rest_once(self):
        things = sg.find("thing")["things"]
        for thing in things.walk():
            if thing == 1:
                break
        self.assertEqual([1, 2, 3, 4, 5], things.all())
        self.assertEqual([1, 2, 3, 4, 5], list(things.walk()))
        self.assertEqual(5, len(things))
        self.assertEqual(["verb", "more", "more"], self.hub.methods())

    def test_without_a_cursor_it_is_the_full_list(self):
        self.handler = lambda method, params: ok({"things": [1, 2], "count": 2})
        things = sg.find("thing")["things"]
        self.assertFalse(things.more)
        self.assertEqual([1, 2], list(things.walk()))
        self.assertEqual(["verb"], self.hub.methods())

    def test_the_catalogue_names_the_main_list_when_the_hub_does_not(self):
        self.handler = lambda method, params: ok({"pieces": [1], "count": 1, "cursor": "c2"})
        pieces = sg.swap("cable", network="9")["pieces"]
        self.assertIsInstance(pieces, MainList)
        self.handler = lambda method, params: ok({"entries": [2]})
        self.assertEqual([1, 2], list(pieces.walk()))


class ErrorTests(HubCase):
    def test_an_error_raises_sg_error_with_code_message_fix_maybe_ran_and_the_call(self):
        self.handler = lambda method, params: refused("thing_not_found", "No thing 99.", fix="find it first",
                                                      maybe_ran=False)
        with self.assertRaises(sg.Error) as raised:
            sg.inspect("device", id="99")
        error = raised.exception
        self.assertEqual(("thing_not_found", "No thing 99.", "find it first", False),
                         (error.code, error.message, error.fix, error.maybe_ran))
        self.assertEqual({"verb": "inspect", "what": "device", "args": {"id": "99"}}, error.call)

    def test_stopped_raises_sg_stopped_which_except_exception_does_not_catch(self):
        rows = [["122643", "Pump", "On 0 -> 1"]]
        self.handler = lambda method, params: refused("unseen_target", "not seen", stopped=True,
                                                      fix="nothing acted: send it again with confirm: true",
                                                      data={"rows": rows})
        with self.assertRaises(sg.Stopped) as raised:
            try:
                self.run_code("x = 1\nsg.write('logic', id='122643', logic='On', value=1)")
            except Exception:  # noqa: BLE001 - the point: this must not catch it
                self.fail("sg.Stopped was caught by except Exception")
        stop = raised.exception
        self.assertEqual(("unseen_target", "not seen", rows, 2), (stop.code, stop.message, stop.rows, stop.line))
        self.assertIn("confirm", stop.fix)

    def test_pass_end_raises_the_private_pass_end(self):
        self.handler = lambda method, params: refused("pass_end", "the pass ends", pass_end=True)
        with self.assertRaises(_PassEnd):
            sg.wait(1)
        self.assertFalse(issubclass(_PassEnd, Exception))


class MainListUsesTests(HubCase):
    def setUp(self):
        super().setUp()
        self.handler = lambda method, params: ok({"things": [1, 2], "cursor": "c", "main": "things"}) \
            if method == "verb" else ok({"entries": [3]})

    def test_adding_copy_deepcopy_and_pickle_see_the_fetched_entries_and_all_sees_every_page(self):
        self.assertEqual([0, 1, 2], [0] + sg.find("thing")["things"])
        self.assertEqual([1, 2], copy.copy(sg.find("thing")["things"]))
        self.assertEqual([1, 2], copy.deepcopy(sg.find("thing")["things"]))
        self.assertEqual([1, 2], pickle.loads(pickle.dumps(sg.find("thing")["things"])))
        self.assertEqual([0, 1, 2, 3], [0] + sg.find("thing")["things"].all())


class NestedRowTests(HubCase):
    def test_a_missing_key_at_any_depth_carries_that_dicts_keys(self):
        self.handler = lambda method, params: ok({"device": {"logic": {"On": 1, "Power": 4}},
                                                  "results": [{"slots": [{"index": 0, "logic": {}}]}]})
        reply = sg.inspect("device", id="1")
        with self.assertRaises(KeyError) as raised:
            reply["device"]["logic"]["Onn"]
        self.assertEqual(["On", "Power"], raised.exception.reply_keys)
        with self.assertRaises(KeyError) as raised:
            reply["results"][0]["slots"][0]["occupant"]
        self.assertEqual(["index", "logic"], raised.exception.reply_keys)


if __name__ == "__main__":
    unittest.main()
