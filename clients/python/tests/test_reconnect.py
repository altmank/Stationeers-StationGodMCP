"""Reconnecting and the resend rule (clients.md, Reconnecting; stage 7 acceptance, Resending)."""
import threading
import time
import unittest

from _support import FakeError, FakeModCase, stationgod, wait_for
from stationgod import connection


def held_then_dropped(test, mod, method, handler, **params):
    """Sends method, waits until the mod has read it (so the call counts as written), then breaks the connection;
    calls after the break are answered at once. Returns the PendingCall."""
    mod.handlers[method] = handler
    release = mod.hold(method)
    test.addCleanup(release.set)
    game = test.game
    pending = game.call_async(method, **params)
    if pending.done():
        pending.result()   # refused before sending: show why
    wait_for(lambda: mod.calls(method), what=f"{method} to arrive")
    del mod.held[method]
    mod.drop_all()
    return pending


class ReconnectCases:
    def setUp(self):
        super().setUp()
        self.fake = self.mod()
        self.game = self.client(self.fake)
        self.game.open()

    def test_the_next_call_reconnects_after_a_break(self):
        self.fake.drop_all()
        wait_for(lambda: not self.game.connected, what="the break to be seen")
        self.assertEqual(84211.5, self.game.call("game_clock")["game_time_s"])

    def test_a_written_read_is_sent_again_once(self):
        pending = held_then_dropped(self, self.fake, "find_things", lambda params: {"things": [{"a": 1}]})
        self.assertEqual({"things": [{"a": 1}]}, pending.result())
        self.assertEqual(2, len(self.fake.calls("find_things")))

    def test_a_read_broken_twice_fails_without_maybe_ran(self):
        self.fake.handlers["find_things"] = lambda params: {"things": []}
        self.fake.break_on("find_things")
        with self.assertRaises(stationgod.Unreachable) as raised:
            self.game.call("find_things")
        self.assertFalse(raised.exception.maybe_ran)
        self.assertEqual(2, len(self.fake.calls("find_things")))

    def test_a_written_highlight_is_not_sent_again(self):
        pending = held_then_dropped(self, self.fake, "highlight", lambda params: {"highlighted": 1},
                                    targets=["1"])
        with self.assertRaises(stationgod.Unreachable) as raised:
            pending.result()
        self.assertTrue(raised.exception.maybe_ran)
        self.assertEqual(1, len(self.fake.calls("highlight")))

    def test_a_written_write_raises_maybe_ran(self):
        pending = held_then_dropped(self, self.fake, "write_logic", lambda params: {"ok": True},
                                    reference_id="1", logic_type="On", value=1)
        with self.assertRaises(stationgod.Unreachable) as raised:
            pending.result()
        self.assertTrue(raised.exception.maybe_ran)
        self.assertIn("may have reached the game", raised.exception.message)
        self.assertEqual(1, len(self.fake.calls("write_logic")))

    def test_a_place_cables_dry_run_counts_as_a_read(self):
        pending = held_then_dropped(self, self.fake, "place_cables", lambda params: {"plan": []},
                                    waypoints=[[0, 0, 0], [2, 0, 0]])
        self.assertEqual({"plan": []}, pending.result())
        self.assertEqual(2, len(self.fake.calls("place_cables")))

    def test_a_confirmed_place_cables_is_not_sent_again(self):
        pending = held_then_dropped(self, self.fake, "place_cables", lambda params: {"job_id": "j1"},
                                    waypoints=[[0, 0, 0], [2, 0, 0]], dry_run=False, confirm=True)
        with self.assertRaises(stationgod.Unreachable) as raised:
            pending.result()
        self.assertTrue(raised.exception.maybe_ran)
        self.assertEqual(1, len(self.fake.calls("place_cables")))

    def test_nothing_is_sent_again_into_another_world(self):
        worlds = []
        self.game.on_world_changed(worlds.append)
        self.fake.set_world("w2")
        pending = held_then_dropped(self, self.fake, "find_things", lambda params: {"things": []})
        with self.assertRaises(stationgod.WorldChanged) as raised:
            pending.result()
        self.assertFalse(raised.exception.maybe_ran)
        self.assertEqual(1, len(self.fake.calls("find_things")))
        self.assertEqual("w2", worlds[0]["id"])
        self.assertEqual("w2", self.game.world["id"])

    def test_an_error_reply_is_never_sent_again(self):
        def busy(params):
            raise FakeError("game_timeout", "not started before its deadline")

        self.fake.handlers["find_things"] = busy
        with self.assertRaises(stationgod.GameError) as raised:
            self.game.call("find_things")
        self.assertEqual("game_timeout", raised.exception.code)
        time.sleep(0.2)
        self.assertEqual(1, len(self.fake.calls("find_things")))

    def test_a_call_whose_line_was_not_written_is_sent_on_the_new_connection(self):
        self.fake.handlers["write_logic"] = lambda params: {"ok": True}
        conn = self.game._conn
        original = conn.send
        failed = []

        def fail_once(message):
            if message.get("method") == "write_logic" and not failed:
                failed.append(message)
                conn.close()
                raise OSError("the pipe broke before the line was written")
            return original(message)

        conn.send = fail_once
        self.assertEqual({"ok": True}, self.game.call("write_logic", reference_id="1", logic_type="On", value=1))
        self.assertEqual(1, len(failed))
        self.assertEqual(1, len(self.fake.calls("write_logic")))   # only the line that was written reached it

    def test_a_call_waiting_for_a_slot_is_sent_after_a_break(self):
        self.fake.max_in_flight = 1
        self.game.close()
        self.game = self.client(self.fake).open()
        self.fake.handlers["write_logic"] = lambda params: {"ok": True}
        waiting = []
        self.fake.handlers["find_things"] = lambda params: {"things": []}
        release = self.fake.hold("find_things")
        self.addCleanup(release.set)
        first = self.game.call_async("find_things")
        wait_for(lambda: self.fake.calls("find_things"), what="the first call")
        thread = threading.Thread(target=lambda: waiting.append(
            self.game.call_async("write_logic", reference_id="1", logic_type="On", value=1)))
        thread.start()
        time.sleep(0.2)
        self.assertEqual([], self.fake.calls("write_logic"))     # waiting for the one slot
        del self.fake.held["find_things"]
        self.fake.drop_all()
        thread.join(5)
        self.assertEqual({"things": []}, first.result())
        self.assertEqual({"ok": True}, waiting[0].result())
        self.assertEqual(1, len(self.fake.calls("write_logic")))

    def test_reconnecting_backs_off_while_the_game_is_away(self):
        sub = self.game.subscribe(items=[{"reference_id": "1", "logic": ["On"]}])
        self.fake.stop()
        time.sleep(0.6)
        attempts = []
        original = connection.open_connection

        def counting(*args, **kwargs):
            attempts.append(time.monotonic())
            return original(*args, **kwargs)

        self.addCleanup(setattr, stationgod.client, "open_connection", original)
        stationgod.client.open_connection = counting
        time.sleep(1.6)
        self.assertGreaterEqual(len(attempts), 1)
        self.assertLessEqual(len(attempts), 6)   # 0.1, 0.2, 0.4, 0.8 ... not a busy loop
        self.assertFalse(sub.closed)


class ReconnectOverTcp(ReconnectCases, FakeModCase):
    transport = "tcp"


class ReconnectOverPipe(ReconnectCases, FakeModCase):
    transport = "pipe"


if __name__ == "__main__":
    unittest.main()
