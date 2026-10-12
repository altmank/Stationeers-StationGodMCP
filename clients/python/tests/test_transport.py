"""The transport: hello and welcome, calls matched to replies by id, concurrency, events, end of stream, the child
hub's command line."""
import os
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock

from _support import FakeHub, ok
import sg
from sg import _transport
from sg._transport import Transport


class TransportTests(unittest.TestCase):
    def connect(self, handler):
        hub, reader, writer = FakeHub.over_pipes(handler)
        transport = Transport(reader, writer, client="sg-test")
        self.addCleanup(hub.close)
        self.addCleanup(transport.close)
        return hub, transport

    def test_hello_names_protocol_2_the_client_and_the_pipeline_feature(self):
        hub, transport = self.connect(None)
        welcome = transport.hello(timeout=5)
        hello = hub.of_type("hello")[0]
        self.assertEqual([2], hello["protocol"])
        self.assertEqual("sg-test", hello["client"]["name"])
        self.assertEqual(["pipeline"], hello["features"])
        self.assertEqual("welcome", welcome["type"])
        self.assertIs(welcome, transport.welcome)

    def test_replies_that_come_back_out_of_order_reach_their_own_callers(self):
        def handler(method, params):
            time.sleep(params["delay"])
            return ok({"echo": params["n"]})

        _, transport = self.connect(handler)
        transport.hello(timeout=5)
        results = {}

        def caller(n, delay):
            results[n] = transport.call("verb", {"n": n, "delay": delay})["result"]["echo"]

        threads = [threading.Thread(target=caller, args=(n, 0.3 - n * 0.015)) for n in range(20)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(10)
        self.assertEqual({n: n for n in range(20)}, results)

    def test_each_call_gets_a_new_id(self):
        hub, transport = self.connect(None)
        transport.hello(timeout=5)
        transport.call("ref", {"id": "r1"})
        transport.call("ref", {"id": "r2"})
        self.assertEqual(2, len(hub.calls))

    def test_events_go_to_the_handlers(self):
        hub, transport = self.connect(None)
        transport.hello(timeout=5)
        seen = threading.Event()
        events = []
        transport.on_event(lambda event: (events.append(event), seen.set()))
        hub.send({"type": "event", "event": "ping"})
        self.assertTrue(seen.wait(5))
        self.assertEqual("ping", events[0]["event"])

    def test_end_of_stream_fails_a_waiting_call_as_maybe_ran(self):
        started = threading.Event()

        def handler(method, params):
            started.set()
            time.sleep(10)
            return ok({})

        hub, transport = self.connect(handler)
        transport.hello(timeout=5)
        failure = []

        def caller():
            try:
                transport.call("verb", {})
            except sg.Error as error:
                failure.append(error)

        thread = threading.Thread(target=caller)
        thread.start()
        self.assertTrue(started.wait(5))
        hub.close()
        thread.join(5)
        self.assertEqual("unreachable", failure[0].code)
        self.assertTrue(failure[0].maybe_ran)

    def test_hello_answers_none_when_the_hub_closes_instead(self):
        hub, transport = self.connect(None)
        hub.runs = []  # no runs to give: the fake hub closes on hello, as for a worker no longer needed
        self.assertIsNone(transport.hello(timeout=5))


class FindHubTests(unittest.TestCase):
    def test_the_argument_comes_first_then_the_environment_then_path(self):
        with mock.patch.dict(os.environ, {"STATIONGODMCP_HUB": "from-env"}):
            self.assertEqual("given", _transport.find_hub("given"))
            self.assertEqual("from-env", _transport.find_hub())
        with mock.patch.dict(os.environ, {"STATIONGODMCP_HUB": ""}), \
                mock.patch.object(_transport.shutil, "which", return_value="on-path"):
            self.assertEqual("on-path", _transport.find_hub())

    def test_no_hub_anywhere_is_an_error_with_a_fix(self):
        with mock.patch.dict(os.environ, {"STATIONGODMCP_HUB": ""}), \
                mock.patch.object(_transport.shutil, "which", return_value=None):
            with self.assertRaises(sg.Error) as raised:
                _transport.find_hub()
        self.assertEqual("hub_not_found", raised.exception.code)
        self.assertIn("connect", raised.exception.fix)


CHILD_HUB = r'''
import json, sys
assert sys.argv[1:] == ["--hub", "--pipe", "Test"], sys.argv
for line in sys.stdin.buffer:
    message = json.loads(line)
    if message["type"] == "hello":
        answer = {"type": "welcome", "protocol": 2, "features": ["pipeline"], "client_id": "p1"}
    else:
        answer = {"type": "reply", "id": message["id"], "ok": True,
                  "result": {"things": [{"reference_id": "7"}], "params": message["params"]}}
    sys.stdout.buffer.write((json.dumps(answer) + "\n").encode())
    sys.stdout.buffer.flush()
'''


class ConnectTests(unittest.TestCase):
    def test_connect_starts_a_child_hub_with_hub_and_the_given_arguments(self):
        folder = tempfile.mkdtemp()
        script = os.path.join(folder, "child_hub.py")
        with open(script, "w", encoding="utf-8") as file:
            file.write(CHILD_HUB)
        if sys.platform == "win32":
            program = os.path.join(folder, "hub.cmd")
            with open(program, "w", encoding="utf-8") as file:
                file.write(f'@"{sys.executable}" "{script}" %*\n')
        else:
            program = os.path.join(folder, "hub")
            with open(program, "w", encoding="utf-8") as file:
                file.write(f'#!/bin/sh\nexec "{sys.executable}" "{script}" "$@"\n')
            os.chmod(program, 0o755)
        transport = sg.connect(program, "--pipe", "Test", client="sg-connect-test")
        try:
            self.assertEqual("p1", transport.welcome["client_id"])
            reply = sg.find("thing", limit=3)
            self.assertEqual({"verb": "find", "what": "thing", "args": {"limit": 3}, "line": None},
                             reply["params"])
            self.assertEqual("7", reply["things"][0]["reference_id"])
            if sys.platform == "win32":
                self.assertIsNotNone(transport._job)
        finally:
            sg.close()


if __name__ == "__main__":
    unittest.main()
