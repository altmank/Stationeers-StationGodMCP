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

    def test_a_wait_in_a_finally_after_a_stop_is_sent_returns_none_and_the_stop_goes_on(self):
        answers = iter([refused("stopped", "stopped", stopped=True), ok({}), ok({})])
        self.handler = lambda method, params: next(answers)
        source = "log = []\ntry:\n    sg.wait(1)\nfinally:\n    log.append(sg.wait(2))\n    sg.find('thing')\n"
        with self.assertRaises(sg.Stopped):
            self.run_code(source)
        self.assertEqual([1.0, 2.0], [params["s"] for params in self.params("wait")])
        self.assertEqual(1, len(self.params("verb")))  # sg goes on calling after a stop; it does not refuse itself

    def test_a_wait_in_a_function_a_while_loop_calls_is_in_loop(self):
        self.run_code("def step():\n    sg.wait(1)\nn = 0\nwhile n < 1:\n    n += 1\n    step()\nstep()")
        self.assertEqual([(2, True), (2, False)], [(p["line"], p["in_loop"]) for p in self.params("wait")])


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

    def test_a_hub_without_the_subscriptions_feature_polls(self):
        self.handler = lambda method, params: ok({"instant": True}) if method == "wait" \
            else ok({"results": [{"value": 1.0}]})
        rows, met = sg.watch(self.READS, lambda rows: False, timeout=600)
        self.assertEqual(([{"value": 1.0}], True), (rows, met))
        self.assertEqual([], self.params("subscribe"))
        self.assertEqual(1, len(self.params("verb")))

    def test_after_a_stop_watch_reads_once_and_returns(self):
        source = ("try:\n    sg.wait(1)\nexcept BaseException:\n    pass\n"
                  "rows, met = sg.watch([{'id': '5', 'logic': 'On'}], lambda rows: False, 600)\nmet")
        answers = iter([refused("stopped", "stopped", stopped=True)])
        self.handler = lambda method, params: next(answers) if method == "wait" else ok({"results": []})
        self.assertFalse(self.run_code(source))
        self.assertEqual(1, len(self.params("wait")))
        self.assertEqual(1, len(self.params("verb")))


class SubscribedWatchTests(HubCase):
    """sg.watch on a hub that has the devices topic: the devices subscription, and what falls back to polling."""

    features = ["pipeline", "subscriptions"]
    READS = [{"id": "5", "logic": "Pressure"}]

    def ended(self, event, **fields):
        """A handler whose one wait pushes the event that ends the subscription, then polls: the reply's reading is
        1.0 and the poll's is 2.0."""
        def handler(method, params):
            if method == "subscribe":
                return ok({"subscription": "s1", "interval_s": 1.0, "result": {"results": [{"value": 1.0}]}})
            if method == "wait":
                self.hub.write({"type": "event", "event": event, **fields})
                return ok({})
            return ok({"results": [{"value": 2.0}]})
        return handler

    def test_the_replys_reading_is_the_first_rows_and_an_update_replaces_them_without_a_read(self):
        waits = []

        def handler(method, params):
            if method == "subscribe":
                return ok({"subscription": "s1", "interval_s": 1.0, "values": 1, "frame": 7,
                           "result": {"results": [{"value": 1.0}]}})
            if method == "wait":
                waits.append(params["s"])
                if len(waits) == 2:
                    self.hub.write({"type": "event", "event": "update", "subscription": "s1", "seq": 1,
                                    "frame": 9, "game_time_s": 3.0, "late_ms": 1.5,
                                    "result": {"results": [{"value": 5.0}]}})
                return ok({})
            return ok({})

        self.handler = handler
        rows, met = sg.watch(self.READS, lambda rows: rows and rows[0]["value"] >= 5, timeout=60)
        self.assertTrue(met)
        self.assertEqual(5.0, rows[0]["value"])
        self.assertEqual([{"reads": self.READS, "interval_s": 1.0}], self.params("subscribe"))
        self.assertEqual([], self.params("verb"))  # a subscribed watch makes no read logic call
        self.assertEqual([("watch", 1.0), ("watch", 1.0)], [(p["kind"], p["s"]) for p in self.params("wait")])
        self.assertEqual([{"subscription": "s1"}], self.params("unsubscribe"))

    def test_an_atmosphere_entry_subscribes_at_the_heavy_cadence(self):
        reads = [{"id": "5", "atmosphere": {"of": "self"}}]

        def handler(method, params):
            if method == "subscribe":
                return ok({"subscription": "s1", "interval_s": params["interval_s"], "result": {"results": []}})
            return ok({"instant": True}) if method == "wait" else ok({})

        self.handler = handler
        sg.watch(reads, lambda rows: False, timeout=600)
        self.assertEqual([{"reads": reads, "interval_s": 5.0}], self.params("subscribe"))

    def test_a_read_that_is_not_the_devices_form_polls(self):
        def handler(method, params):
            return ok({"instant": True}) if method == "wait" else ok({"results": []})

        self.handler = handler
        sg.watch([{"id": "5", "logic": "On", "include": ["clock"]}], lambda rows: False, timeout=600)
        self.assertEqual([], self.params("subscribe"))
        self.assertEqual(1, len(self.params("verb")))

    def test_the_timeout_returns_unmet_and_unsubscribes(self):
        def handler(method, params):
            if method == "subscribe":
                return ok({"subscription": "s1", "interval_s": 1.0, "result": {"results": [{"value": 1.0}]}})
            if method == "wait":
                time.sleep(params["s"])
                return ok({})
            return ok({})

        self.handler = handler
        started = time.monotonic()
        rows, met = sg.watch(self.READS, lambda rows: False, timeout=0.3)
        self.assertEqual(([{"value": 1.0}], False), (rows, met))
        self.assertLess(time.monotonic() - started, 5)
        self.assertEqual([{"subscription": "s1"}], self.params("unsubscribe"))
        self.assertEqual([], self.params("verb"))

    def test_a_refused_subscribe_polls_for_the_rest_of_the_watch(self):
        def handler(method, params):
            if method == "subscribe":
                return refused("subscription_limit", "1,024 values is the limit for one subscription; poll instead")
            if method == "wait":
                return ok({"instant": True})
            return ok({"results": [{"value": 2.0}]})

        self.handler = handler
        rows, met = sg.watch(self.READS, lambda rows: False, timeout=600)
        self.assertEqual(([{"value": 2.0}], True), (rows, met))
        self.assertEqual(1, len(self.params("verb")))
        self.assertEqual([], self.params("unsubscribe"))  # nothing was opened

    def test_a_subscription_ended_event_falls_back_to_polling_without_raising(self):
        self.handler = self.ended("subscription_ended", subscription="s1", reason="read_failed")
        rows, met = sg.watch(self.READS, lambda rows: rows and rows[0]["value"] >= 2, timeout=60)
        self.assertEqual((True, 2.0), (met, rows[0]["value"]))
        self.assertEqual(1, len(self.params("verb")))
        self.assertEqual([], self.params("unsubscribe"))  # the hub already forgot it

    def test_a_world_changed_event_falls_back_to_polling_without_raising(self):
        self.handler = self.ended("world_changed", world={"id": "w2"})
        rows, met = sg.watch(self.READS, lambda rows: rows and rows[0]["value"] >= 2, timeout=60)
        self.assertEqual((True, 2.0), (met, rows[0]["value"]))
        self.assertEqual(1, len(self.params("verb")))

    def test_after_a_stop_the_watch_reads_once_without_subscribing(self):
        source = ("try:\n    sg.wait(1)\nexcept BaseException:\n    pass\n"
                  "rows, met = sg.watch([{'id': '5', 'logic': 'On'}], lambda rows: False, 600)\nmet")
        answers = iter([refused("stopped", "stopped", stopped=True)])
        self.handler = lambda method, params: next(answers) if method == "wait" else ok({"results": []})
        self.assertFalse(self.run_code(source))
        self.assertEqual([], self.params("subscribe"))
        self.assertEqual(1, len(self.params("verb")))


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

    def test_a_refused_re_sent_exit_still_raises_the_stop(self):
        answers = iter([ok({"after": [0]}), refused("stopped", "stopped", stopped=True),
                        refused("world_changed", "no cleanup after a world change")])
        self.handler = lambda method, params: next(answers)
        with self.assertRaises(sg.Stopped):
            with sg.hold("1", "On", value=1):
                pass

    def test_a_dict_or_bytes_is_not_ids(self):
        with self.assertRaises(TypeError):
            sg.hold({"reference_id": "5"}, "On")
        with self.assertRaises(TypeError):
            sg.hold(b"5", "On")
        self.assertEqual([], self.holds())

    def test_a_set_of_ids_is_a_list(self):
        with sg.hold({"5"}, "On"):
            pass
        self.assertEqual(["5"], self.holds()[0][1])

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
