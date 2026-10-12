"""sg.wait, sg.watch, sg.hold, sg.ref and sg.help against the fake hub."""
import time
import unittest

from _support import HubCase, ok, refused
import sg


class WaitTests(HubCase):
    def test_wait_sends_seconds_kind_wait_and_returns_none(self):
        self.assertIsNone(sg.wait(2))
        self.assertEqual([{"s": 2.0, "kind": "wait", "in_loop": False, "line": None}], self.params("wait"))

    def test_a_wait_inside_a_while_body_is_in_loop(self):
        self.run_code("sg.wait(1)\nn = 0\nwhile n < 2:\n    n += 1\n    sg.wait(\n        3)\nsg.wait(4)")
        waits = [(params["s"], params["in_loop"], params["line"]) for params in self.params("wait")]
        self.assertEqual([(1.0, False, 1), (3.0, True, 5), (3.0, True, 5), (4.0, False, 7)], waits)

    def test_a_wait_after_a_stop_returns_without_raising(self):
        answers = iter([refused("stopped", "stopped", stopped=True), ok({}), ok({})])
        self.handler = lambda method, params: next(answers)
        with self.assertRaises(sg.Stopped):
            sg.wait(1)
        self.assertIsNone(sg.wait(1))  # the hub answers {} at once after the stop


class WatchTests(HubCase):
    READS = [{"id": "5", "logic": "Pressure"}]

    def test_watch_returns_met_when_until_holds(self):
        values = iter([1.0, 2.0, 3.0])

        def handler(method, params):
            if method == "verb":
                return ok({"results": [{"index": 0, "ok": True, "value": next(values)}]})
            return ok({})

        self.handler = handler
        rows, met = sg.watch(self.READS, lambda rows: rows[0]["value"] >= 3, timeout=60)
        self.assertTrue(met)
        self.assertEqual(3.0, rows[0]["value"])
        self.assertEqual([{"verb": "read", "what": "logic", "args": {"reads": self.READS}, "line": None}] * 3,
                         self.params("verb"))
        self.assertEqual([("watch", 1.0)] * 2, [(p["kind"], p["s"]) for p in self.params("wait")])

    def test_watch_returns_its_rows_unmet_at_timeout_without_raising(self):
        def handler(method, params):
            if method == "wait":
                time.sleep(params["s"])
                return ok({})
            return ok({"results": [{"index": 0, "ok": True, "value": 0.0}]})

        self.handler = handler
        started = time.monotonic()
        rows, met = sg.watch(self.READS, lambda rows: False, timeout=0.3)
        self.assertFalse(met)
        self.assertEqual(0.0, rows[0]["value"])
        self.assertLess(time.monotonic() - started, 5)

    def test_an_instant_wait_ends_the_watch_as_met(self):
        self.handler = lambda method, params: ok({"instant": True}) if method == "wait" else \
            ok({"results": [{"value": 0.0}]})
        rows, met = sg.watch(self.READS, lambda rows: False, timeout=600)
        self.assertEqual(([{"value": 0.0}], True), (rows, met))

    def test_an_atmosphere_read_polls_every_5_s(self):
        self.handler = lambda method, params: ok({"instant": True}) if method == "wait" else ok({"results": []})
        sg.watch([{"id": "5", "atmosphere": {"of": "self"}}], lambda rows: False, timeout=600)
        self.assertEqual(5.0, self.params("wait")[0]["s"])

    def test_after_a_stop_watch_reads_once_and_returns(self):
        self.handler = lambda method, params: ok({"results": [{"value": 1.0}]})
        source = ("try:\n    sg.wait(1)\nexcept BaseException:\n    pass\n"
                  "rows, met = sg.watch([{'id': '5', 'logic': 'On'}], lambda rows: False, 600)\nmet")
        answers = iter([refused("stopped", "stopped", stopped=True)])
        self.handler = lambda method, params: next(answers) if method == "wait" else ok({"results": []})
        self.assertFalse(self.run_code(source))
        self.assertEqual(1, len(self.params("wait")))


class HoldTests(HubCase):
    def setUp(self):
        super().setUp()
        self.handler = self.answer
        self.stop_on = set()

    def answer(self, method, params):
        if method == "hold" and params["phase"] in self.stop_on:
            self.stop_on.discard(params["phase"])
            return refused("stopped", "stopped", stopped=True)
        if method == "hold":
            return ok({"after": [0]} if params["phase"] == "enter" else {"rows": [["122643", "On", "1 -> 0"]]})
        if method == "wait" and "wait" in self.stop_on:
            self.stop_on.discard("wait")
            return refused("stopped", "stopped", stopped=True)
        return ok({})

    def holds(self):
        return [(p["phase"], p["ids"], p["logic"], p.get("value"), p.get("after")) for p in self.params("hold")]

    def test_enter_then_exit_with_the_after_values_the_hub_read(self):
        with sg.hold("122643", "On", value=1) as held:
            self.assertEqual([0], held.after)
        self.assertEqual([("enter", ["122643"], "On", 1, None), ("exit", ["122643"], "On", 1, [0])], self.holds())
        self.assertEqual([["122643", "On", "1 -> 0"]], held.rows)

    def test_a_list_of_ids_and_a_given_after_are_sent_as_they_are(self):
        with sg.hold(["1", 2], "Open", after=[0, 0]):
            pass
        self.assertEqual(("enter", ["1", 2], "Open", None, [0, 0]), self.holds()[0])

    def test_exit_runs_after_an_exception_and_the_exception_goes_on(self):
        with self.assertRaises(ValueError):
            with sg.hold("122643", "On", value=1):
                raise ValueError("boom")
        self.assertEqual(["enter", "exit"], [hold[0] for hold in self.holds()])

    def test_exit_runs_after_a_stop_and_the_stop_goes_on(self):
        self.stop_on = {"wait"}
        with self.assertRaises(sg.Stopped):
            with sg.hold("122643", "On", value=1):
                sg.wait(5)
        self.assertEqual(["enter", "exit"], [hold[0] for hold in self.holds()])

    def test_a_stop_delivered_on_the_exit_sends_the_exit_again(self):
        self.stop_on = {"exit"}
        with self.assertRaises(sg.Stopped):
            with sg.hold("122643", "On", value=1):
                pass
        self.assertEqual(["enter", "exit", "exit"], [hold[0] for hold in self.holds()])


class RefAndHelpTests(HubCase):
    def test_ref_sends_the_id(self):
        self.handler = lambda method, params: ok({"ref": "r7", "ids": ["1"], "reply": {}})
        self.assertEqual(["1"], sg.ref("r7")["ids"])
        self.assertEqual([{"id": "r7", "line": None}], self.params("ref"))

    def test_help_asks_for_the_python_text(self):
        self.handler = lambda method, params: ok({"text": "def find(...)"})
        self.assertEqual("def find(...)", sg.help("find", "thing"))
        sg.help()
        sg.help("scripts")
        sg.help("types", "BuildJob")
        self.assertEqual([{"verb": "find", "what": "thing", "lang": "python"}, {"lang": "python"},
                          {"topic": "scripts", "lang": "python"},
                          {"topic": "types", "subtopic": "BuildJob", "lang": "python"}],
                         [params["args"] for params in self.params("verb")])
        self.assertEqual({"help"}, {params["verb"] for params in self.params("verb")})


if __name__ == "__main__":
    unittest.main()
