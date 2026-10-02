"""Subscriptions against the fake mod (protocol.md, Subscriptions; clients.md, Subscriptions in the library)."""
import threading
import unittest

from _support import FakeModCase, stationgod, wait_for

ITEMS = [{"reference_id": "811234", "logic": ["Temperature", "Pressure"]}]


def reading(temperature):
    return {"gateway_id": "world", "results": [{"reference_id": "811234", "logic": {"Temperature": temperature}}]}


class SubscriptionCases:
    def setUp(self):
        super().setUp()
        self.fake = self.mod()
        self.fake.handlers["read_devices"] = lambda params: reading(290.0)
        self.game = self.client(self.fake)

    def device_subscriptions(self):
        return {key: value for key, value in self.fake.subscriptions().items() if value.get("topic") != "world"}

    def test_subscribe_gives_the_first_reading(self):
        sub = self.game.subscribe(items=ITEMS, include=["clock"], interval_s=1)
        self.assertEqual(reading(290.0), sub.state)
        self.assertEqual(0, sub.seq)
        request = self.fake.calls("subscribe")[-1]["params"]
        self.assertEqual({"items": ITEMS, "include": ["clock"], "interval_s": 1}, request)

    def test_the_library_subscribes_to_the_world_topic(self):
        self.game.open()
        wait_for(lambda: any(value.get("topic") == "world" for value in self.fake.subscriptions().values()),
                 what="the world subscription")

    def test_updates_replace_the_state_and_wake_waiters_and_callbacks(self):
        sub = self.game.subscribe(items=ITEMS)
        seen = []
        sub.on_update(seen.append)
        woke = []
        waiter = threading.Thread(target=lambda: woke.append(sub.wait(5)))
        waiter.start()
        wait_for(lambda: waiter.is_alive(), what="the waiter")
        self.fake.push_update(sub.id, reading(300.0), seq=1, frame=1200)
        waiter.join(5)
        self.assertEqual([True], woke)
        self.assertEqual(reading(300.0), sub.state)
        self.assertEqual((1, 1200), (sub.seq, sub.frame))
        self.assertEqual([reading(300.0)], seen)

    def test_a_callback_that_raises_does_not_stop_the_reader(self):
        sub = self.game.subscribe(items=ITEMS)
        sub.on_update(lambda state: 1 / 0)
        with self.assertLogs("stationgod", "ERROR"):
            self.fake.push_update(sub.id, reading(301.0))
            wait_for(lambda: sub.seq == 1, what="the update")
        self.assertEqual(84211.5, self.game.call("game_clock")["game_time_s"])

    def test_wait_times_out_without_an_update(self):
        sub = self.game.subscribe(items=ITEMS)
        self.assertFalse(sub.wait(0.1))

    def test_a_refused_subscription_raises_subscription_refused(self):
        self.fake.subscription_limit = 1
        with self.assertRaises(stationgod.SubscriptionRefused) as raised:
            self.game.subscribe(items=ITEMS)    # the world topic already holds the one allowed
        self.assertEqual("subscription_limit", raised.exception.code)

    def test_a_server_without_the_feature_is_too_old(self):
        fake = self.mod(features=["shape", "cancel"])
        with self.assertRaises(stationgod.TooOld):
            self.client(fake).subscribe(items=ITEMS)

    def test_close_unsubscribes(self):
        sub = self.game.subscribe(items=ITEMS)
        server_id = sub.id
        sub.close()
        self.assertTrue(sub.closed)
        self.assertEqual("closed", sub.end_reason)
        self.assertEqual({"subscription": server_id}, self.fake.calls("unsubscribe")[-1]["params"])
        self.assertEqual({}, self.device_subscriptions())

    def test_the_mod_ending_it_with_world_changed_closes_it(self):
        sub = self.game.subscribe(items=ITEMS)
        self.fake.push({"event": "subscription_ended", "subscription": sub.id, "reason": "world_changed"})
        wait_for(lambda: sub.closed, what="the subscription to close")
        self.assertEqual("world_changed", sub.end_reason)
        self.assertFalse(sub.wait(0.05))

    def test_a_world_changed_event_closes_subscriptions_and_tells_the_caller(self):
        worlds = []
        self.game.on_world_changed(worlds.append)
        sub = self.game.subscribe(items=ITEMS)
        self.fake.push({"event": "world_changed", "world": {"id": "w2", "save": "other", "epoch": 2}})
        wait_for(lambda: worlds, what="the world-changed callback")
        self.assertEqual("w2", worlds[0]["id"])
        self.assertTrue(sub.closed)
        self.fake.push({"event": "world_changed", "world": {"id": "w2", "save": "other", "epoch": 2}})
        self.game.call("game_clock")
        self.assertEqual(1, len(worlds))   # once per world id

    def test_a_reconnect_into_the_same_world_subscribes_again(self):
        sub = self.game.subscribe(items=ITEMS, interval_s=2)
        first_id = sub.id
        self.fake.handlers["read_devices"] = lambda params: reading(310.0)
        self.fake.drop_all()
        wait_for(lambda: sub.id is not None and sub.id != first_id, what="the resubscribe")
        self.assertFalse(sub.closed)
        self.assertEqual(reading(310.0), sub.state)
        device_requests = [call["params"] for call in self.fake.calls("subscribe") if call["params"].get("items")]
        self.assertEqual([{"items": ITEMS, "interval_s": 2}] * 2, device_requests)
        self.fake.push_update(sub.id, reading(320.0))
        wait_for(lambda: sub.state == reading(320.0), what="an update on the new subscription")

    def test_a_reconnect_into_another_world_closes_it(self):
        worlds = []
        self.game.on_world_changed(worlds.append)
        sub = self.game.subscribe(items=ITEMS)
        self.fake.set_world("w2")
        self.fake.drop_all()
        wait_for(lambda: sub.closed, what="the subscription to close")
        self.assertEqual("world_changed", sub.end_reason)
        self.assertEqual("w2", worlds[0]["id"])
        self.assertEqual(1, len([c for c in self.fake.calls("subscribe") if c["params"].get("items")]))


class SubscriptionsOverTcp(SubscriptionCases, FakeModCase):
    transport = "tcp"


class SubscriptionsOverPipe(SubscriptionCases, FakeModCase):
    transport = "pipe"


if __name__ == "__main__":
    unittest.main()
