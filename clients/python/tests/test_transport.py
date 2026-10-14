"""The transport: hello and welcome, calls matched to replies by id, concurrency, events, end of stream, the child
hub's command line."""
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock

from _support import FakeHub, ok
from fakehub import PACKAGE_ROOT, welcome
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

    def test_a_welcome_after_hello_replaces_the_session_state(self):
        # A hub whose game was unreachable at hello sends the mod's welcome when the link first comes up; the features
        # watch/changes read must be the newest ones, not the hello answer's.
        hub, transport = self.connect(None)
        transport.hello(timeout=5)
        self.assertEqual(["pipeline"], transport.welcome["features"])

        hub.write(welcome(features=["pipeline", "subscriptions"]))
        deadline = time.time() + 5
        while time.time() < deadline and "subscriptions" not in (transport.welcome.get("features") or []):
            time.sleep(0.01)

        self.assertEqual(["pipeline", "subscriptions"], transport.welcome["features"])
        self.assertEqual("w1", transport.welcome["server"]["world"]["id"])

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


class TransportMisuseTests(unittest.TestCase):
    def connect(self):
        hub, reader, writer = FakeHub.over_pipes(None)
        transport = Transport(reader, writer, client="sg-test")
        transport.hello(timeout=5)
        self.addCleanup(hub.close)
        return hub, transport, writer

    def test_params_json_cannot_carry_are_invalid_argument_and_leave_nothing_pending(self):
        _, transport, _ = self.connect()
        for bad in ({1, 2}, float("nan"), float("inf")):
            with self.assertRaises(sg.Error) as raised:
                transport.call("verb", {"bad": bad})
            self.assertEqual("invalid_argument", raised.exception.code)
        self.assertEqual({}, transport._pending)
        self.assertTrue(transport.call("verb", {})["ok"])
        transport.close()

    def test_a_closed_writer_raises_sg_error(self):
        _, transport, writer = self.connect()
        writer.close()
        with self.assertRaises(sg.Error):
            transport.call("verb", {})
        with self.assertRaises(sg.Error):
            transport.send({"type": "run_end"})
        self.assertEqual({}, transport._pending)


SILENT_HUB = r"""
import sys, time
sys.stdin.buffer.readline()
time.sleep(60)
"""


def hub_program(folder, source, pause=False):
    """A command that runs source with this Python (a .cmd on Windows, a shell script elsewhere). pause: the .cmd
    waits about a second before it starts Python, so the job assignment of cmd.exe (right after it starts) is done
    before cmd.exe has a child; a real hub is the assigned process itself."""
    script = os.path.join(folder, "child_hub.py")
    with open(script, "w", encoding="utf-8") as file:
        file.write(source)
    if sys.platform == "win32":
        program = os.path.join(folder, "hub.cmd")
        with open(program, "w", encoding="utf-8") as file:
            if pause:
                file.write("@ping -n 2 127.0.0.1 >nul\n")
            file.write(f'@"{sys.executable}" "{script}" %*\n')
    else:
        program = os.path.join(folder, "hub")
        with open(program, "w", encoding="utf-8") as file:
            file.write(f'#!/bin/sh\nexec "{sys.executable}" "{script}" "$@"\n')
        os.chmod(program, 0o755)
    return program


class ConnectLifecycleTests(unittest.TestCase):
    def test_a_hub_that_never_welcomes_times_out_and_is_killed(self):
        program = hub_program(tempfile.mkdtemp(), SILENT_HUB)
        started = []
        real_popen = _transport.subprocess.Popen

        def popen(*args, **kwargs):
            started.append(real_popen(*args, **kwargs))
            return started[-1]

        with mock.patch.object(_transport, "HELLO_TIMEOUT_S", 0.5), \
                mock.patch.object(_transport.subprocess, "Popen", popen):
            with self.assertRaises(sg.Error) as raised:
                sg.connect(program)
        self.assertEqual("hub_timeout", raised.exception.code)
        self.assertIsNotNone(started[0].wait(10))

    def test_a_second_connect_closes_the_first(self):
        program = hub_program(tempfile.mkdtemp(), CHILD_HUB)
        first = sg.connect(program, "--pipe", "Test")
        try:
            second = sg.connect(program, "--pipe", "Test")
            self.assertTrue(first._writer.closed)
            self.assertIsNot(first, second)
            self.assertEqual("7", sg.find("thing")["things"][0]["reference_id"])
        finally:
            sg.close()


PARENT = r"""
import sys, time
sys.path.insert(0, sys.argv[1])
import sg
sg.connect(sys.argv[2])
print("connected", flush=True)
time.sleep(60)
"""

STUBBORN_HUB = r"""
import json, os, sys, time
sys.stdin.buffer.readline()
with open(os.environ["SG_TEST_PID_FILE"], "w") as file:
    file.write(str(os.getpid()))
sys.stdout.buffer.write((json.dumps({"type": "welcome", "protocol": 2, "features": ["pipeline"]}) + "\n").encode())
sys.stdout.buffer.flush()
time.sleep(120)   # never reads stdin again: only the Job Object can end it
"""


def windows_process_alive(pid):
    import ctypes
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = ctypes.c_void_p
    handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return False
    try:
        code = ctypes.c_ulong()
        kernel32.GetExitCodeProcess(ctypes.c_void_p(handle), ctypes.byref(code))
        return code.value == 259  # STILL_ACTIVE
    finally:
        kernel32.CloseHandle(ctypes.c_void_p(handle))


@unittest.skipUnless(sys.platform == "win32", "the Job Object is Windows only")
class ChildHubDiesWithParentTests(unittest.TestCase):
    def test_killing_the_program_kills_its_child_hub(self):
        folder = tempfile.mkdtemp()
        program = hub_program(folder, STUBBORN_HUB, pause=True)
        parent_script = os.path.join(folder, "parent.py")
        with open(parent_script, "w", encoding="utf-8") as file:
            file.write(PARENT)
        pid_file = os.path.join(folder, "hub.pid")
        environment = dict(os.environ, SG_TEST_PID_FILE=pid_file)
        parent = subprocess.Popen([sys.executable, parent_script, PACKAGE_ROOT, program], stdout=subprocess.PIPE,
                                  env=environment)
        hub_pid = None
        try:
            self.assertEqual(b"connected", parent.stdout.readline().strip())
            with open(pid_file, encoding="utf-8") as file:
                hub_pid = int(file.read())
            self.assertTrue(windows_process_alive(hub_pid))
            parent.kill()
            parent.wait(10)
            deadline = time.monotonic() + 10
            while windows_process_alive(hub_pid) and time.monotonic() < deadline:
                time.sleep(0.1)
            self.assertFalse(windows_process_alive(hub_pid), "the child hub outlived its program")
        finally:
            if parent.poll() is None:
                parent.kill()
            parent.stdout.close()
            if hub_pid is not None and windows_process_alive(hub_pid):
                os.kill(hub_pid, 9)  # do not leave it holding the test runner's pipes


class EncodeTests(unittest.TestCase):
    def test_a_lone_surrogate_is_escaped_so_the_line_is_valid_utf_8(self):
        line = _transport.encode({"text": "a" + chr(0xD800) + "\u00e9"})
        line.decode("utf-8")  # strict: raises if not valid UTF-8
        self.assertIn(b"\\ud800", line)

    def test_nan_is_refused(self):
        with self.assertRaises(ValueError):
            _transport.encode({"value": float("nan")})


class NonClosableTests(unittest.TestCase):
    def test_a_worker_transport_ignores_close_and_with(self):
        hub, reader, writer = FakeHub.over_pipes(None)
        self.addCleanup(hub.close)
        transport = Transport(reader, writer, client="sg-test", closable=False)
        transport.hello(timeout=5)
        transport.close()
        with transport:
            pass
        self.assertTrue(transport.call("verb", {})["ok"])
        writer.close()


class JobHandleTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform == "win32", "the Job Object is Windows only")
    def test_the_job_is_freed_when_the_hub_cannot_start(self):
        closed = []
        real_create = _transport._KillJob.create

        def create():
            job = real_create()
            real_close = job.close
            job.close = lambda: (closed.append(True), real_close())
            return job

        with mock.patch.object(_transport._KillJob, "create", staticmethod(create)):
            with self.assertRaises(OSError):
                sg.connect(os.path.join(tempfile.mkdtemp(), "no-such-hub.exe"))
        self.assertEqual([True], closed)


if __name__ == "__main__":
    unittest.main()
