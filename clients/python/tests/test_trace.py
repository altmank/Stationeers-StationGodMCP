"""sg.trace(): the last 20 calls, and the last 5 appended to an sg.Error's message, against the fake hub."""
import unittest

from _support import HubCase, ok, refused
import sg
from sg import _trace


class TraceTests(HubCase):
    def setUp(self):
        super().setUp()
        _trace.clear()

    def test_a_successful_call_is_recorded_with_ok_true(self):
        sg.find("thing", prefab="*Battery*")
        entry = sg.trace()[-1]
        self.assertEqual(("find", "thing", True, None), (entry["verb"], entry["what"], entry["ok"], entry["code"]))
        self.assertIn("Battery", entry["args"])
        self.assertLessEqual(len(entry["args"]), 120)
        self.assertIsInstance(entry["ms"], (int, float))

    def test_the_ring_keeps_the_last_20_newest_last(self):
        for n in range(25):
            sg.find("thing", prefab=f"p{n}")
        rows = sg.trace()
        self.assertEqual(20, len(rows))
        self.assertIn("p5", rows[0]["args"])
        self.assertIn("p24", rows[-1]["args"])

    def test_long_args_are_cut_to_120_chars(self):
        sg.find("thing", prefab="x" * 500)
        self.assertLessEqual(len(sg.trace()[-1]["args"]), 120)

    def test_an_error_message_includes_the_last_5_calls(self):
        for n in range(7):
            sg.find("thing", prefab=f"p{n}")
        self.handler = lambda method, params: refused("not_found", "no such thing")
        with self.assertRaises(sg.Error) as caught:
            sg.find("thing", prefab="bad")
        text = str(caught.exception)
        self.assertTrue(text.startswith("not_found: no such thing"))
        for n in (3, 4, 5, 6):
            self.assertIn(f"p{n}", text)
        self.assertNotIn("p2", text)
        self.assertIn("bad", text)  # the failing call is the newest of the last 5
        last = sg.trace()[-1]
        self.assertEqual((False, "not_found"), (last["ok"], last["code"]))

    def test_a_call_whose_transport_raises_is_recorded(self):
        with self.assertRaises(sg.Error) as caught:
            sg.find("thing", radius=float("nan"))
        self.assertEqual("invalid_argument", caught.exception.code)
        last = sg.trace()[-1]
        self.assertEqual(("find", "thing", False, "invalid_argument"),
                         (last["verb"], last["what"], last["ok"], last["code"]))
        self.assertIn("last calls:", str(caught.exception))

    def test_inside_a_run_the_message_carries_the_last_calls_too(self):
        sg.find("thing", prefab="before")
        self.handler = lambda method, params: refused("not_found", "no such thing")
        caught = self.run_code("try:\n    sg.inspect('device', id='5')\nexcept sg.Error as error:\n    caught = error\n"
                               "caught\n")
        self.assertEqual("not_found", caught.code)
        self.assertEqual("no such thing", caught.message)
        text = str(caught)
        self.assertIn("last calls:", text)
        self.assertIn("before", text)
        self.assertTrue(caught.last_calls[-1].startswith('  inspect.device {"id":"5"}'))
        self.assertTrue(caught.last_calls[-1].endswith("error not_found"))

    def test_a_helpers_own_error_carries_the_last_calls(self):
        self.handler = lambda method, params: ok({"results": [{"index": 0, "ok": False, "reference_id": "5",
                                                               "error": {"code": "not_found", "message": "x"}}]})
        reply = sg.read("logic", reads=[{"id": "5", "logic": "On"}])
        with self.assertRaises(sg.Error) as caught:
            sg.ok(reply)
        self.assertIn("read.logic", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
