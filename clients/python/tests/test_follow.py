"""sg.follow: the find topic through the hub (protocol.md, Subscriptions)."""
import unittest

from _support import HubCase, ok, refused
import sg
from sg._follow import apply_update


def subscribed(result, keyed=None):
    return ok({"subscription": "s1", "topic": "find", "what": "frame", "interval_s": 10.0, "frame": 5,
               "as_of_s": 12.5, "cost_ms": 3.1, "keyed": keyed or {"frames": "reference_id"}, "result": result})


class FollowTests(HubCase):
    """sg.follow on a hub with subscriptions: the first list, changes merged in order, and its end."""

    features = ["pipeline", "subscriptions"]

    def test_it_subscribes_with_the_finds_options_and_holds_the_first_list(self):
        self.handler = lambda method, params: subscribed({"frames": [{"reference_id": "1"}], "total_outer": 1})

        following = sg.follow("find", "frame", interval_s=5, include=["inner"])

        self.assertEqual([{"include": ["inner"], "topic": "find", "what": "frame", "interval_s": 5.0}],
                         self.params("subscribe"))
        self.assertEqual({"frames": [{"reference_id": "1"}], "total_outer": 1}, following.result)
        self.assertEqual(12.5, following.as_of_s)
        self.assertTrue(following.live)
        following.close()
        self.assertEqual([{"subscription": "s1"}], self.params("unsubscribe"))

    def test_next_returns_each_change_and_result_takes_it_in(self):
        update = {"type": "event", "event": "find_update", "subscription": "s1", "seq": 1, "frame": 9,
                  "as_of_s": 22.5, "keyed": {"frames": "reference_id"},
                  "lists": {"frames": {"added": [{"reference_id": "3"}], "changed": [{"reference_id": "1", "c": 2}],
                                       "removed": ["2"]}},
                  "rest": {"total_outer": 2}}

        def handler(method, params):
            if method == "subscribe":
                self.hub.write(update)   # before the reply: kept until the reply names the subscription
                return subscribed({"frames": [{"reference_id": "1", "c": 1}, {"reference_id": "2"}], "total_outer": 2})
            return ok({})

        self.handler = handler
        with sg.follow("find", "frame") as following:
            change = following.next(30)
            self.assertEqual(update["lists"], change["lists"])
            self.assertEqual({"frames": [{"reference_id": "1", "c": 2}, {"reference_id": "3"}], "total_outer": 2},
                             following.result)
            self.assertEqual(22.5, following.as_of_s)

    def test_an_ended_subscription_ends_next_and_says_why(self):
        def handler(method, params):
            if method == "subscribe":
                self.hub.write({"type": "event", "event": "subscription_ended", "subscription": "s1",
                                "reason": "world_changed"})
                return subscribed({"frames": []})
            return ok({})

        self.handler = handler
        following = sg.follow("find", "frame")
        self.assertIsNone(following.next(30))
        self.assertFalse(following.live)
        self.assertEqual("world_changed", following.end_reason)

    def test_inside_a_run_next_waits_with_the_hubs_wait(self):
        update = {"type": "event", "event": "find_update", "subscription": "s1", "seq": 1, "frame": 9,
                  "as_of_s": 2.0, "keyed": {"frames": "reference_id"},
                  "lists": {"frames": {"added": [{"reference_id": "5"}], "changed": [], "removed": []}}}

        def handler(method, params):
            if method == "subscribe":
                return subscribed({"frames": []})
            if method == "wait":
                self.hub.write(update)
            return ok({})

        self.handler = handler
        source = ("f = sg.follow('find', 'frame')\nchange = f.next(30)\nf.close()\n"
                  "[change['lists']['frames']['added'][0]['reference_id'], len(f.result['frames'])]")
        self.assertEqual(["5", 1], self.run_code(source))
        self.assertEqual([("watch", 1.0)], [(p["kind"], p["s"]) for p in self.params("wait")])

    def test_a_refused_subscribe_raises_the_games_error(self):
        self.handler = lambda method, params: refused("subscription_limit", "too much")
        with self.assertRaises(sg.Error) as raised:
            sg.follow("find", "thing")
        self.assertEqual("subscription_limit", raised.exception.code)

    def test_only_a_find_can_be_followed(self):
        with self.assertRaises(sg.Error):
            sg.follow("read", "logic")


class NoSubscriptionsTests(HubCase):
    """sg.follow on a hub without subscriptions: too_old, nothing sent."""

    def test_it_raises_too_old(self):
        self.handler = lambda method, params: ok({})
        with self.assertRaises(sg.Error) as raised:
            sg.follow("find", "frame")
        self.assertEqual("too_old", raised.exception.code)
        self.assertEqual([], self.params("subscribe"))


class ApplyUpdateTests(unittest.TestCase):
    """How an update changes the followed reply."""

    def test_a_whole_update_replaces_everything(self):
        result, keyed = apply_update({"frames": [{"reference_id": "1"}]}, {"frames": "reference_id"},
                                     {"whole": True, "keyed": {"frames": "reference_id"}, "result": {"frames": []}})
        self.assertEqual({"frames": []}, result)

    def test_rest_replaces_the_other_keys_and_keyed_lists_stay(self):
        result, _ = apply_update({"things": [{"reference_id": "1"}], "broken": 0, "gone": 1},
                                 {"things": "reference_id"},
                                 {"keyed": {"things": "reference_id"}, "rest": {"broken": 1}})
        self.assertEqual({"things": [{"reference_id": "1"}], "broken": 1}, result)

    def test_an_empty_list_that_fills_takes_its_key_field_from_the_update(self):
        result, keyed = apply_update({"pads": []}, {"pads": ""},
                                     {"keyed": {"pads": "reference_id"},
                                      "lists": {"pads": {"added": [{"reference_id": "8"}], "changed": [],
                                                         "removed": []}}})
        self.assertEqual({"pads": [{"reference_id": "8"}]}, result)
        self.assertEqual({"pads": "reference_id"}, keyed)


if __name__ == "__main__":
    unittest.main()
