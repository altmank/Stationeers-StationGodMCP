"""Version 2 against the fake mod, over loopback TCP and a real overlapped named pipe: sign-in, calls in flight
(full duplex), errors, shaping passed through, client-side output files, generated checks, iterate, timeouts."""
import json
import os
import shutil
import tempfile
import threading
import time
import unittest

from _support import FakeError, FakeModCase, stationgod, wait_for


class Version2Cases:
    def test_connect_gives_the_welcome(self):
        mod = self.mod()
        game = self.client(mod).open()
        self.assertEqual("w1", game.welcome["server"]["world"]["id"])
        self.assertIn("subscriptions", game.features)
        hello = [message for message in mod.received if message.get("type") == "hello"][0]
        self.assertEqual("hello", hello["type"])
        self.assertEqual([2], hello["protocol"])
        self.assertEqual("stationgod-py/0.2.0", hello["client"]["library"])

    def test_a_call_returns_its_result(self):
        mod = self.mod()
        game = self.client(mod)
        self.assertEqual(84211.5, game.call("game_clock")["game_time_s"])
        call = mod.calls("game_clock")[0]
        self.assertEqual(("call", {}), (call["type"], call["params"]))

    def test_a_held_call_does_not_block_others_on_the_same_connection(self):
        mod = self.mod()
        mod.handlers["find_things"] = lambda params: {"things": [{"reference_id": "1"}]}
        release = mod.hold("find_things")
        game = self.client(mod)
        slow = game.call_async("find_things")
        wait_for(lambda: mod.calls("find_things"), what="the held call to arrive")
        started = time.perf_counter()
        self.assertEqual(84211.5, game.call("game_clock")["game_time_s"])
        self.assertLess(time.perf_counter() - started, 1.0)
        self.assertFalse(slow.done())
        release.set()
        self.assertEqual("1", slow.result()["things"][0]["reference_id"])
        self.assertEqual(1, len(mod.connections))

    def test_calls_beyond_max_in_flight_wait_in_the_library(self):
        mod = self.mod(max_in_flight=2)
        mod.handlers["find_things"] = lambda params: {"things": []}
        release = mod.hold("find_things")
        game = self.client(mod)
        first = [game.call_async("find_things") for _ in range(2)]
        third = []
        thread = threading.Thread(target=lambda: third.append(game.call_async("find_things")))
        thread.start()
        time.sleep(0.3)
        self.assertEqual(2, len(mod.calls("find_things")))
        release.set()
        thread.join(5)
        for pending in first + third:
            self.assertEqual({"things": []}, pending.result())
        self.assertEqual(3, len(mod.calls("find_things")))
        self.assertNotIn("too_many_in_flight", json.dumps(mod.received))

    def test_errors_raise_with_code_message_and_data(self):
        mod = self.mod()

        def refuse(code):
            def handler(params):
                raise FakeError(code, f"{code} message", {"required": "write"})
            return handler

        for code in ("thing_not_found", "invalid_argument", "game_changed"):
            mod.handlers[code] = refuse(code)
        game = self.client(mod, check_arguments=False)
        expected = {"thing_not_found": stationgod.NotFound, "invalid_argument": stationgod.InvalidArgument,
                    "game_changed": stationgod.GameError}
        for code, kind in expected.items():
            with self.assertRaises(kind) as raised:
                game.call(code)
            self.assertEqual(code, raised.exception.code)
            self.assertEqual(f"{code} message", raised.exception.message)
            self.assertEqual({"required": "write"}, raised.exception.data)

    def test_an_unknown_method_is_too_old_and_a_game_error(self):
        game = self.client(self.mod())
        with self.assertRaises(stationgod.TooOld) as raised:
            game.call("no_such_method")
        self.assertIsInstance(raised.exception, stationgod.GameError)
        self.assertEqual("method_not_found", raised.exception.code)

    def test_omit_is_passed_as_shape_omit(self):
        mod = self.mod()
        mod.handlers["get_ic_status"] = lambda params: {"reference_id": "300", "pins": []}
        game = self.client(mod)
        game.call("get_ic_status", reference_id="300", omit=["source", "runtime.registers"])
        call = mod.calls("get_ic_status")[0]
        self.assertEqual({"omit": ["source", "runtime.registers"]}, call["shape"])
        self.assertEqual({"reference_id": "300"}, call["params"])

    def test_shaping_is_passed_to_the_mod_and_the_reply_is_not_reshaped(self):
        mod = self.mod()
        mod.handlers["thing_health"] = lambda params: {"results": [{"reference_id": "1", "damage_ratio": 0.0,
                                                                     "is_broken": False}]}
        game = self.client(mod)
        result = game.call("thing_health", reference_ids=["1"], fields=["reference_id", "damage_ratio"],
                           limit={"results": 5}, max_bytes=4096, deadline_ms=2000)
        call = mod.calls("thing_health")[0]
        self.assertEqual({"fields": ["reference_id", "damage_ratio"], "limit": {"results": 5}, "max_bytes": 4096},
                         call["shape"])
        self.assertEqual(2000, call["deadline_ms"])
        self.assertEqual({"reference_ids": ["1"]}, call["params"])
        self.assertEqual([{"reference_id": "1", "damage_ratio": 0.0}], result["results"])

    def test_an_integer_limit_is_the_methods_own_argument(self):
        mod = self.mod()
        mod.handlers["find_things"] = lambda params: {"things": []}
        self.client(mod).call("find_things", limit=20)
        call = mod.calls("find_things")[0]
        self.assertEqual({"limit": 20}, call["params"])
        self.assertNotIn("shape", call)

    def test_shape_is_not_sent_to_a_server_without_the_feature(self):
        mod = self.mod(features=["cancel"])
        mod.handlers["find_things"] = lambda params: {"things": [{"a": 1, "b": 2}]}
        result = self.client(mod).call("find_things", fields=["a"])
        self.assertNotIn("shape", mod.calls("find_things")[0])
        self.assertEqual([{"a": 1, "b": 2}], result["things"])   # the library does not shape

    def test_output_file_is_written_here_and_never_sent(self):
        folder = tempfile.mkdtemp(prefix="stationgod-output-")
        self.addCleanup(shutil.rmtree, folder, True)
        mod = self.mod()
        mod.handlers["find_things"] = lambda params: {"count": 1, "things": [{"reference_id": "7", "x": 1}]}
        game = self.client(mod, output_dir=folder)
        pointer = game.call("find_things", fields=["reference_id"], output_file="things")
        call = mod.calls("find_things")[0]
        self.assertNotIn("output_file", call["params"])
        self.assertEqual({"fields": ["reference_id"]}, call["shape"])
        self.assertEqual(os.path.join(os.path.abspath(folder), "things.json"), pointer["output_file"])
        self.assertEqual({"things": 1}, pointer["counts"])
        with open(pointer["output_file"], encoding="utf-8") as file:
            self.assertEqual({"count": 1, "things": [{"reference_id": "7"}]}, json.load(file))

    def test_an_error_reply_is_never_written_to_a_file(self):
        folder = tempfile.mkdtemp(prefix="stationgod-output-")
        self.addCleanup(shutil.rmtree, folder, True)
        mod = self.mod()

        def refuse(params):
            raise FakeError("thing_not_found", "gone")

        mod.handlers["find_things"] = refuse
        with self.assertRaises(stationgod.NotFound):
            self.client(mod, output_dir=folder).call("find_things", output_file=True)
        self.assertEqual([], os.listdir(folder))

    def test_a_bad_output_file_name_is_refused_before_sending(self):
        mod = self.mod()
        with self.assertRaises(stationgod.InvalidArgument):
            self.client(mod).call("find_things", output_file="../escape")
        self.assertEqual([], mod.calls("find_things"))

    def test_generated_checks_refuse_unknown_names_before_sending(self):
        mod = self.mod()
        game = self.client(mod)
        with self.assertRaises(stationgod.InvalidArgument) as raised:
            game.call("find_things", prefab="x")
        self.assertIn("prefab_contains", raised.exception.message)
        self.assertEqual("client", raised.exception.data["checked_by"])
        self.assertEqual([], mod.calls("find_things"))

    def test_the_stubs_call_through(self):
        mod = self.mod()
        mod.handlers["thing_health"] = lambda params: {"results": [{"reference_id": params["reference_ids"][0]}]}
        result = self.client(mod).thing_health(reference_ids=["364"])
        self.assertEqual("364", result["results"][0]["reference_id"])

    def test_iterate_walks_pages_until_has_more_is_false(self):
        mod = self.mod()
        things = [{"reference_id": str(index)} for index in range(7)]

        def page(params):
            offset, limit = params.get("offset", 0), params.get("limit", 100)
            return {"things": things[offset:offset + limit], "total": len(things),
                    "has_more": offset + limit < len(things)}

        mod.handlers["find_things"] = page
        seen = [thing["reference_id"] for thing in self.client(mod).iterate("find_things", page_size=3)]
        self.assertEqual([str(index) for index in range(7)], seen)
        self.assertEqual([0, 3, 6], [call["params"]["offset"] for call in mod.calls("find_things")])

    def test_no_reply_in_time_is_unreachable(self):
        mod = self.mod()
        mod.handlers["find_things"] = lambda params: {"things": []}
        mod.hold("find_things")
        game = self.client(mod)
        started = time.monotonic()
        with self.assertRaises(stationgod.Unreachable) as raised:
            game.call("find_things", deadline_ms=200)
        self.assertLess(time.monotonic() - started, 5)
        self.assertFalse(raised.exception.maybe_ran)   # a read changes nothing
        wait_for(lambda: any(m.get("type") == "cancel" for m in mod.received), what="the cancel")

    def test_on_call_meters_each_call(self):
        mod = self.mod()
        game = self.client(mod)
        seen = []
        game.on_call = lambda *values: seen.append(values)
        game.call("game_clock")
        method, ms, size, elapsed, queued = seen[0]
        self.assertEqual("game_clock", method)
        self.assertGreater(size, 10)
        self.assertEqual((0.2, 1.5), (elapsed, queued))

    def test_on_call_runs_before_the_caller_wakes(self):
        # The dashboard attributes each metered call to the card running now, so the hook must have run by the time
        # call() returns, even when it is slow.
        mod = self.mod()
        game = self.client(mod)
        seen = []

        def slow_hook(*values):
            time.sleep(0.05)
            seen.append(values[0])
        game.on_call = slow_hook
        for _ in range(5):
            game.call("game_clock")
            self.assertEqual(1, len(seen))
            seen.clear()


class Version2OverTcp(Version2Cases, FakeModCase):
    transport = "tcp"

    def test_the_shared_secret_goes_first(self):
        mod = self.mod()
        self.client(mod).open()
        self.assertEqual({"type": "auth", "secret": "s3cret"}, mod.received[0])
        self.assertEqual("hello", mod.received[1]["type"])

    def test_a_wrong_secret_is_unauthorized(self):
        mod = self.mod()
        os.environ["STATIONGOD_WRONG_SECRET"] = "nope"
        with self.assertRaises(stationgod.Unauthorized):
            self.client(mod, secret_env="STATIONGOD_WRONG_SECRET").open()

    def test_without_a_secret_tcp_is_refused(self):
        mod = self.mod()
        os.environ.pop("STATIONGOD_NO_SECRET", None)
        with self.assertRaises(stationgod.Unauthorized):
            self.client(mod, secret_env="STATIONGOD_NO_SECRET").open()


class Version2OverPipe(Version2Cases, FakeModCase):
    transport = "pipe"

    def test_a_client_name_is_sent_in_hello(self):
        mod = self.mod()
        game = self.client(mod, client="dashboard").open()
        self.assertEqual("dashboard", mod.received[0]["client"]["name"])
        self.assertNotIn("auth", mod.received[0])


if __name__ == "__main__":
    unittest.main()
