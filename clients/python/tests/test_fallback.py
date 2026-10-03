"""Version 1: the fall back from hello to today's protocol (protocol.md, Old clients), and protocol="v1"."""
import os
import unittest

from _support import FakeModCase, stationgod, wait_for


class OldModOverPipe(FakeModCase):
    transport = "pipe"

    def test_the_old_mods_answer_to_hello_switches_to_version_1_on_the_same_connection(self):
        mod = self.mod(version=1)
        game = self.client(mod)
        self.assertEqual(84211.5, game.call("game_clock")["game_time_s"])
        self.assertEqual(1, game.protocol)
        self.assertIsNone(game.welcome)
        self.assertEqual("hello", mod.received[0]["type"])
        call = mod.received[1]
        self.assertNotIn("type", call)
        self.assertEqual(("game_clock", {}), (call["method"], call["params"]))
        self.assertEqual(1, mod.instance + len(mod.connections))   # one connection, no welcome

    def test_shape_is_sent_and_an_unshaped_reply_comes_back_whole(self):
        mod = self.mod(version=1)
        mod.handlers["find_things"] = lambda params: {"things": [{"a": 1, "b": 2}]}
        result = self.client(mod).call("find_things", fields=["a"])
        self.assertEqual({"fields": ["a"]}, mod.calls("find_things")[0]["shape"])
        self.assertEqual([{"a": 1, "b": 2}], result["things"])

    def test_a_version_1_mod_that_shapes_marks_it(self):
        mod = self.mod(version=1, shapes_v1=True)
        mod.handlers["find_things"] = lambda params: {"things": [{"a": 1, "b": 2}]}
        self.assertEqual([{"a": 1}], self.client(mod).call("find_things", fields=["a"])["things"])

    def test_calls_go_one_at_a_time(self):
        mod = self.mod(version=1)
        mod.handlers["find_things"] = lambda params: {"things": []}
        game = self.client(mod)
        pending = [game.call_async("find_things") for _ in range(3)]
        self.assertEqual([{"things": []}] * 3, [call.result() for call in pending])

    def test_version_1_is_lenient_no_generated_checks(self):
        mod = self.mod(version=1)
        mod.handlers["find_things"] = lambda params: {"things": []}
        self.client(mod).call("find_things", prefab="x")
        self.assertEqual({"prefab": "x"}, mod.calls("find_things")[0]["params"])

    def test_subscribe_is_too_old(self):
        with self.assertRaises(stationgod.TooOld):
            self.client(self.mod(version=1)).subscribe(items=[{"reference_id": "1"}])

    def test_a_written_read_is_resent_and_a_write_is_not(self):
        mod = self.mod(version=1)
        game = self.client(mod)
        game.open()
        mod.handlers["find_things"] = lambda params: {"things": []}
        mod.break_on("find_things")
        with self.assertRaises(stationgod.Unreachable) as raised:
            game.call("find_things")
        self.assertFalse(raised.exception.maybe_ran)
        self.assertEqual(2, len(mod.calls("find_things")))
        mod.handlers["write_logic"] = lambda params: {}
        mod.break_on("write_logic")
        with self.assertRaises(stationgod.Unreachable) as raised:
            game.call("write_logic", reference_id="1", logic_type="On", value=1)
        self.assertTrue(raised.exception.maybe_ran)
        self.assertEqual(1, len(mod.calls("write_logic")))

    def test_protocol_v2_refuses_the_old_mod_and_sends_nothing_after_hello(self):
        mod = self.mod(version=1)
        game = self.client(mod, protocol="v2")
        for _ in range(2):
            with self.assertRaisesRegex(stationgod.TooOld, "only protocol version 1"):
                game.call("game_clock")
        self.assertEqual(["hello", "hello"], [message.get("type") for message in mod.received])
        self.assertIsNone(game.protocol)

    def test_protocol_v2_speaks_version_2(self):
        mod = self.mod(version=2)
        game = self.client(mod, protocol="v2")
        self.assertEqual(84211.5, game.call("game_clock")["game_time_s"])
        self.assertEqual(2, game.protocol)

    def test_protocol_v1_sends_no_hello(self):
        mod = self.mod(version=2)
        game = self.client(mod, protocol="v1")
        game.call("game_clock")
        self.assertEqual(1, game.protocol)
        self.assertNotIn("type", mod.received[0])


class OldModOverTcp(FakeModCase):
    transport = "tcp"

    def test_the_secret_then_hello_falls_back_to_version_1_on_one_connection(self):
        mod = self.mod(version=1, legacy_secret="s3cret")
        os.environ["STATIONGOD_TEST_SECRET"] = "s3cret"
        game = self.client(mod, secret_env="STATIONGOD_TEST_SECRET")
        self.assertEqual(84211.5, game.call("game_clock")["game_time_s"])
        self.assertEqual(1, game.protocol)
        self.assertEqual({"type": "auth", "secret": "s3cret"}, mod.received[0])
        self.assertEqual("hello", mod.received[1]["type"])

    def test_without_a_secret_tcp_is_refused(self):
        mod = self.mod(version=1, legacy_secret="s3cret")
        os.environ.pop("STATIONGOD_NO_SECRET", None)
        with self.assertRaises(stationgod.Unauthorized):
            self.client(mod, secret_env="STATIONGOD_NO_SECRET").open()

    def test_protocol_v1_signs_in_with_the_secret(self):
        mod = self.mod(version=1, legacy_secret="s3cret")
        os.environ["STATIONGOD_TEST_SECRET"] = "s3cret"
        game = self.client(mod, protocol="v1", secret_env="STATIONGOD_TEST_SECRET")
        game.call("game_clock")
        self.assertEqual({"type": "auth", "secret": "s3cret"}, mod.received[0])

    def test_a_wrong_secret_is_unauthorized(self):
        mod = self.mod(version=1, legacy_secret="s3cret")
        os.environ["STATIONGOD_BAD_SECRET"] = "nope"
        with self.assertRaises(stationgod.Unauthorized):
            self.client(mod, protocol="v1", secret_env="STATIONGOD_BAD_SECRET").open()
        wait_for(lambda: not mod.connections, what="the mod to close")


if __name__ == "__main__":
    unittest.main()
