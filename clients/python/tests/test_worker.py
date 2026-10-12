"""The script worker as the hub runs it: a child process on stdin and stdout, against the fake hub."""
import time
import unittest

from _support import FakeHub, ok, refused
from fakehub import worker_environment
from sg._analysis import analyse
from sg.worker import BOOT


def run(code, run_id="script-1"):
    return {"id": run_id, "code": code, "confirm": True, "face": "script", "limits": {"cut_bytes": 4096}}


class WorkerCase(unittest.TestCase):
    def serve_one(self, code, handler=None, exit_code=0):
        """Runs code in a fresh worker (launched as the hub does); returns (hub, run_end) once the worker has exited.
        """
        hub, process = FakeHub.with_worker(run(code), handler)
        try:
            code_seen = process.wait(30)
            hub.wait_for(lambda hub: hub.of_type("_eof"))
        finally:
            if process.poll() is None:
                process.kill()
            self.stderr = process.stderr.read().decode("utf-8", "replace")
            process.stderr.close()
            process.stdin.close()
        self.assertEqual(exit_code, code_seen, self.stderr)
        ends = hub.of_type("run_end")
        self.assertEqual(1, len(ends), self.stderr)
        return hub, ends[0]

    def serve(self, codes, handler=None):
        """Each code in its own worker; returns the run_ends."""
        return [self.serve_one(code, handler)[1] for code in codes]


class AnalysisTests(unittest.TestCase):
    """The AST pass, in process (the worker sends exactly this; WorkerTests checks that it is sent first)."""

    def test_literals_are_numbers_and_strs_with_f_string_pieces_and_no_bools(self):
        analysis = analyse("pump = '122643'\nn = 15\nflag = True\nx = 2.5\nsg.write('logic', id=pump, logic='Open',"
                           " value=1)\nlabel = f'tank {n} of {pump}'\nm = -4\n")
        self.assertEqual(["122643", 15, 2.5, "logic", "Open", 1, "tank ", " of ", -4, 4], analysis.literals)

    def test_float_literals_count_as_numbers_with_negatives_and_without_bools_nan_or_inf(self):
        analysis = analyse("box = [[1.5, 2.0, 3.5], [-4.25, 5, 6]]\nnear = [700.5, 202, 660]\nok = True\n"
                           "big = 1e400\nlabel = f'at {1.5}'\n")
        self.assertEqual([1.5, 2.0, 3.5, -4.25, 4.25, 5, 6, 700.5, 202, 660, "at "], analysis.literals)
        self.assertIsInstance(analysis.literals[1], float)  # 2.0 stays a JSON number; the hub compares by value
        self.assertNotIn(True, [value for value in analysis.literals if isinstance(value, bool)])

    def test_while_lines_are_every_line_inside_a_while_body(self):
        analysis = analyse("n = 0\nwhile n < 3:\n    n += 1\n    if n:\n        sg.wait(\n            1)\nprint(n)\n")
        self.assertEqual([2, 3, 4, 5, 6], analysis.while_lines)  # the test line too

    def test_read_only_code_neither_writes_nor_holds(self):
        analysis = analyse("r = sg.find('thing')\nsg.read('logic', id='1', logic='On')\nsg.server('info')\n"
                           "sg.trade('contacts')\nsg.wait(1)")
        self.assertEqual((False, False), (analysis.writes, analysis.holds))

    def test_write_verbs_and_server_write_whats_write(self):
        for code in ("sg.write('logic', id='1', logic='On', value=1)", "sg.build('structure')", "sg.undo(id='x')",
                     "sg.server('stop', id='script-2')", "sg.server('profiling')", "sg.move(what)",
                     "import sg as s\ns.remove('cable', id='4')", "from sg import swap\nswap('wall', id='3')",
                     "f = sg.write"):
            self.assertTrue(analyse(code).writes, code)

    def test_hold_is_found(self):
        analysis = analyse("with sg.hold(['1'], 'On', value=1):\n    sg.wait(5)")
        self.assertEqual((False, True), (analysis.writes, analysis.holds))

    def test_a_syntax_error_is_reported_with_its_line(self):
        self.assertEqual("line 2: SyntaxError: '(' was never closed", analyse("x = 1\ny = (\n").syntax_error)


def find_handler(method, params):
    if method == "verb" and params["verb"] == "find":
        return ok({"things": [{"reference_id": "7", "prefab_name": "Battery"}], "count": 1, "total": 1})
    return ok({})


class WorkerTests(WorkerCase):
    def test_run_analysis_comes_before_any_call_and_carries_the_fields(self):
        hub, end = self.serve_one("sg.find('thing')\nwhile False:\n    sg.wait(1)\n", find_handler)
        self.assertEqual("done", end["state"])
        kinds = [message["type"] for message in hub.messages]
        self.assertEqual(["hello", "run_analysis", "run_end", "_eof"], kinds)  # one run, then the worker exits
        analysis = hub.of_type("run_analysis")[0]
        self.assertEqual({"type": "run_analysis", "literals": ["thing", 1], "while_lines": [2, 3], "writes": False,
                          "holds": False, "syntax_error": None}, analysis)
        self.assertEqual("sg-worker", hub.of_type("hello")[0]["client"]["name"])

    def test_the_final_bare_expression_is_returned(self):
        _, end = self.serve_one("r = sg.find('thing')\n{'n': len(r['things']), 'id': r['things'][0]['reference_id']}",
                                find_handler)
        self.assertEqual(("done", {"n": 1, "id": "7"}, None), (end["state"], end["returned"], end["error"]))

    def test_return_works_and_a_value_json_cannot_hold_is_its_repr(self):
        ends = self.serve(["return 5\n", "return {1, 2}"])
        self.assertEqual([5, "{1, 2}"], [end["returned"] for end in ends])

    def test_printed_output_is_captured_and_a_row_list_prints_as_a_table(self):
        _, end = self.serve_one("print('hello')\nprint([{'id': '7', 'name': 'Battery'}, {'id': '12'}])")
        self.assertEqual("hello\nid  name\n--  -------\n7   Battery\n12\n", end["printed"])

    def test_the_lines_sent_are_the_code_lines(self):
        hub, _ = self.serve_one("\n\nsg.find('thing')\nfor i in range(2):\n    sg.wait(0)\n", find_handler)
        self.assertEqual([("verb", 3), ("wait", 5), ("wait", 5)],
                         [(method, params["line"]) for method, params in hub.calls])

    def test_time_sleep_is_sg_wait_inside_a_run(self):
        hub, _ = self.serve_one("import time\nwhile True:\n    time.sleep(2)\n    break\n")
        self.assertEqual([("wait", {"s": 2.0, "kind": "wait", "in_loop": True, "line": 3})], hub.calls)

    def test_a_stop_that_reaches_the_top_ends_the_run_stopped(self):
        handler = lambda method, params: refused("stopped", "stopped by the user", stopped=True)  # noqa: E731
        _, end = self.serve_one("x = 1\nsg.wait(1)\n", handler)
        self.assertEqual("stopped", end["state"])
        self.assertIn("sg.Stopped: stopped: stopped by the user", end["error"])

    def test_a_stop_swallowed_by_the_code_still_ends_the_run_stopped(self):
        handler = lambda method, params: refused("stopped", "stopped", stopped=True)  # noqa: E731
        _, end = self.serve_one("try:\n    sg.wait(1)\nexcept BaseException:\n    pass\n'kept'", handler)
        self.assertEqual(("stopped", "kept"), (end["state"], end["returned"]))
        _, end = self.serve_one("try:\n    sg.wait(1)\nfinally:\n    return 'ok'\n", handler)
        self.assertEqual(("stopped", "ok"), (end["state"], end["returned"]))
        _, end = self.serve_one("try:\n    sg.wait(1)\nexcept BaseException:\n    pass\nraise ValueError('x')", handler)
        self.assertEqual("stopped", end["state"])
        self.assertIn("ValueError: x", end["error"])

    def test_a_pass_end_ends_the_run_pass_end_and_finally_runs(self):
        handler = lambda method, params: refused("pass_end", "pass ends", pass_end=True)  # noqa: E731
        _, end = self.serve_one("try:\n    sg.wait(1)\nfinally:\n    print('cleanup')\n", handler)
        self.assertEqual(("pass_end", "cleanup\n"), (end["state"], end["printed"]))

    def test_a_key_error_on_a_reply_gives_the_keys_nearest_first_and_the_values_on_the_line(self):
        code = "limit = 3\nr = sg.find('thing', limit=limit)\ncount = r['thngs'][limit]\n"
        _, end = self.serve_one(code, find_handler)
        self.assertEqual("failed", end["state"])
        lines = end["error"].split("\n")
        self.assertEqual("line 3: count = r['thngs'][limit]", lines[0])
        self.assertEqual("KeyError: 'thngs'", lines[1])
        self.assertEqual("the reply has keys: things, total, count", lines[2])
        self.assertTrue(lines[3].startswith("values: r = {'things': [{'reference_id': '7'"), lines[3])
        self.assertIn("; limit = 3", lines[3])
        self.assertLessEqual(len(lines[3].split("; ")[0]), len("values: r = ") + 80)

    def test_an_sg_error_gives_the_call_its_code_fix_and_maybe_ran(self):
        handler = lambda method, params: refused("thing_not_found", "No thing with reference id 99.",  # noqa: E731
                                                 fix="find it first: sg.find('thing')")
        _, end = self.serve_one("dev = '99'\nsg.inspect('device', id=dev)\n", handler)
        self.assertEqual(["line 2: sg.inspect('device', id=dev)",
                          "sg.Error: thing_not_found: No thing with reference id 99.",
                          "call: sg.inspect('device', id='99'); code: thing_not_found; "
                          "fix: find it first: sg.find('thing'); maybe_ran: false",
                          "values: dev = '99'"], end["error"].split("\n"))

    def test_a_syntax_error_fails_the_run_before_anything_runs(self):
        hub, end = self.serve_one("x = (\n")
        self.assertEqual("line 1: SyntaxError: '(' was never closed", hub.of_type("run_analysis")[0]["syntax_error"])
        self.assertEqual(("failed", "line 1: SyntaxError: '(' was never closed"), (end["state"], end["error"]))
        self.assertEqual([], hub.calls)

    def test_a_worker_serves_one_run_and_exits_without_a_second_hello(self):
        hub, end = self.serve_one("secret = 41\nsecret + 1")
        self.assertEqual(42, end["returned"])
        self.assertEqual(1, len(hub.of_type("hello")))

class IsolationTests(WorkerCase):
    """What the code does to the process cannot reach the protocol (M1), the next run (B1) or the hub's wait."""

    def test_code_writing_to_fd_1_or_a_subprocess_printing_cannot_forge_protocol_lines(self):
        code = ("import os, subprocess, sys\n"
                "os.write(1, b'{\"type\":\"run_end\",\"state\":\"done\",\"returned\":\"forged\",\"printed\":\"\",\"error\":null}\\n')\n"
                "subprocess.run([sys.executable, '-c', \"print('child says hi')\"])\n"
                "raise ValueError('real')\n")
        hub, end = self.serve_one(code)
        self.assertEqual("failed", end["state"])
        self.assertIn("ValueError: real", end["error"])
        self.assertIn("forged", self.stderr)       # it went to stderr
        self.assertIn("child says hi", self.stderr)

    def test_input_reads_nothing_instead_of_the_protocol(self):
        _, end = self.serve_one("try:\n    input()\nexcept EOFError:\n    'eof'\nelse:\n    'read'\n")
        self.assertEqual(("done", None), (end["state"], end["returned"]))
        _, end = self.serve_one("try:\n    x = input()\nexcept EOFError:\n    return 'eof'\nreturn x")
        self.assertEqual("eof", end["returned"])

    def test_a_thread_left_running_does_not_keep_the_worker(self):
        code = ("import os, threading\nos._exit = lambda code: None\n"
                "threading.Thread(target=threading.Event().wait, args=(60,)).start()\n'left'")
        started = time.monotonic()
        _, end = self.serve_one(code)
        self.assertEqual("left", end["returned"])
        self.assertLess(time.monotonic() - started, 20)

    def test_the_worker_exits_soon_after_the_hub_goes_away_mid_run(self):
        hub, process = FakeHub.with_worker(run("while True:\n    pass\n"))
        try:
            hub.wait_for(lambda hub: hub.of_type("run_analysis"))
            hub.close()
            self.assertEqual(1, process.wait(15))
        finally:
            if process.poll() is None:
                process.kill()
            process.stderr.close()
            process.stdin.close()

    def test_the_boot_line_is_the_one_the_hub_uses(self):
        self.assertEqual("import sys; sys.path.insert(0, sys.argv[1]); import sg.worker; sg.worker.main()", BOOT)


class RunEndAlwaysTests(WorkerCase):
    """Every run ends with a run_end, whatever breaks (M3)."""

    def test_an_exception_whose_str_fails_still_fails_the_run(self):
        _, end = self.serve_one("class E(Exception):\n    def __str__(self):\n        raise RuntimeError('x')\nraise E()\n")
        self.assertEqual("failed", end["state"])
        self.assertIn("E", end["error"])

    def test_a_returned_value_whose_repr_fails_is_still_reported(self):
        _, end = self.serve_one("class R:\n    def __repr__(self):\n        raise RuntimeError('r')\nR()\n")
        self.assertEqual("done", end["state"])
        self.assertIn("repr failed", end["returned"])

    def test_code_nested_too_deep_to_parse_is_a_syntax_error(self):
        hub, end = self.serve_one("x = " + "(" * 300 + ")" * 300)
        self.assertEqual("failed", end["state"])
        self.assertTrue(hub.of_type("run_analysis")[0]["syntax_error"].startswith("line 1:"))


class ModuleLikeTests(WorkerCase):
    def test_global_in_a_function_star_imports_and_annotations_work_and_yield_is_refused(self):
        _, end = self.serve_one("count = 0\ndef f():\n    global count\n    count += 1\nf()\ncount\n")
        self.assertEqual(1, end["returned"])
        _, end = self.serve_one("from math import *\nx: int = 2\nfloor(pi) + x\n")
        self.assertEqual(5, end["returned"])
        _, end = self.serve_one("x = 1\nyield x\n")
        self.assertEqual(("failed", "line 2: SyntaxError: 'yield' outside function: a run's code is a function body,"
                          " not a generator"), (end["state"], end["error"]))


class VerbsModuleAnalysisTests(unittest.TestCase):
    def test_writes_through_sg_verbs_are_found(self):
        for code in ("from sg._verbs import write\nwrite('logic', id='1', logic='On', value=1)",
                     "import sg._verbs as v\nv.build('structure')", "sg._verbs.remove('cable', id='2')"):
            self.assertTrue(analyse(code).writes, code)


class HashSeedTests(WorkerCase):
    """B-1: the hub's launch line keeps PYTHONHASHSEED=0, so hashes and set order are the same in every worker."""

    CODE = "return [hash('abc'), list({'a', 'b', 'c', 'd', 'e', 'f'}), list({'x': 1, 'y': 2})]"

    def test_hashes_and_set_order_are_equal_across_workers(self):
        first = self.serve_one(self.CODE)[1]["returned"]
        second = self.serve_one(self.CODE)[1]["returned"]
        self.assertEqual(first, second)

    def test_a_worker_with_hash_randomisation_refuses_to_run(self):
        environment = worker_environment()
        del environment["PYTHONHASHSEED"]
        hub, process = FakeHub.with_worker(run("1"), environment=environment)
        try:
            self.assertEqual(0, process.wait(30))
            hub.wait_for(lambda hub: hub.of_type("run_end"))
        finally:
            process.stderr.close()
            process.stdin.close()
        end = hub.of_type("run_end")[0]
        self.assertEqual("failed", end["state"])
        self.assertIn("hash randomisation", end["error"])
        self.assertEqual([], hub.of_type("run_analysis"))


class RunConnectionTests(WorkerCase):
    """MA-1: the run's connection belongs to the hub."""

    def test_sg_close_and_connect_inside_a_run_raise_and_the_run_still_ends(self):
        _, end = self.serve_one("sg.close()\n'x'")
        self.assertEqual("failed", end["state"])
        self.assertIn("a run's connection belongs to the hub", end["error"])
        _, end = self.serve_one("sg.connect('hub.exe')\n'x'")
        self.assertIn("a run's connection belongs to the hub", end["error"])

    def test_closing_the_transport_itself_does_nothing(self):
        _, end = self.serve_one("with sg._session.current_transport():\n    pass\nsg._session.current_transport().close()\n'x'")
        self.assertEqual(("done", "x"), (end["state"], end["returned"]))


class JsonSafeEndTests(WorkerCase):
    """MA-2: whatever the code returns or prints, the run_end is valid JSON in valid UTF-8."""

    def test_surrogates_and_nan_still_end_the_run(self):
        _, end = self.serve_one("chr(0xd800)")
        self.assertEqual(("done", "\ud800"), (end["state"], end["returned"]))
        _, end = self.serve_one("print(b'\\xff'.decode('utf-8', 'surrogateescape'))\n1")
        self.assertEqual(("done", 1), (end["state"], end["returned"]))
        _, end = self.serve_one("raise ValueError(chr(0xdc80))")
        self.assertEqual("failed", end["state"])
        _, end = self.serve_one("float('nan')")
        self.assertEqual(("done", "nan"), (end["state"], end["returned"]))
        _, end = self.serve_one("10**5000")
        self.assertEqual("done", end["state"])
        self.assertIsInstance(end["returned"], str)

    def test_a_call_json_cannot_carry_is_invalid_argument_not_unreachable(self):
        _, end = self.serve_one("try:\n    sg.find(chr(0xd800), limit=float('nan'))\nexcept sg.Error as e:\n    return e.code")
        self.assertEqual("invalid_argument", end["returned"])


class ProtocolFdTests(WorkerCase):
    """MA-3: the protocol's descriptors are out of reach of guessed small fds."""

    def test_writing_to_small_fds_does_not_forge_and_closerange_does_not_break_the_run(self):
        code = ("import os\nline = b'{\"type\":\"run_end\",\"state\":\"done\",\"returned\":\"forged\",\"printed\":\"\","
                "\"error\":null}\\n'\nfor fd in range(3, 64):\n    try:\n        os.write(fd, line)\n"
                "    except OSError:\n        pass\nraise ValueError('real')\n")
        hub, end = self.serve_one(code)
        self.assertEqual("failed", end["state"])
        self.assertIn("ValueError: real", end["error"])
        _, end = self.serve_one("import os\nos.closerange(3, 64)\nsg.find('thing')\n'x'")
        self.assertEqual(("done", "x"), (end["state"], end["returned"]))


class StopBeliefTests(WorkerCase):
    def test_an_sg_stopped_the_code_raises_itself_is_a_failure(self):
        _, end = self.serve_one("raise sg.Stopped('fake', 'not from the hub')")
        self.assertEqual("failed", end["state"])
        self.assertIn("sg.Stopped: fake: not from the hub", end["error"])

    def test_a_stop_after_a_pass_end_makes_the_run_stopped(self):
        answers = iter([refused("pass_end", "pass ends", pass_end=True), refused("stopped", "stopped", stopped=True)])
        _, end = self.serve_one("for i in range(2):\n    try:\n        sg.wait(1)\n    except BaseException:\n"
                                "        pass\n'k'", lambda method, params: next(answers))
        self.assertEqual("stopped", end["state"])


class ModuleLikeRoundTwoTests(WorkerCase):
    def test_future_imports_star_imports_in_place_and_globals_in_blocks(self):
        _, end = self.serve_one("from __future__ import annotations\nx: int = 1\nx")
        self.assertEqual(1, end["returned"])
        _, end = self.serve_one("pi = 3\nfrom math import *\npi")
        self.assertEqual(3.141592653589793, end["returned"])
        _, end = self.serve_one("try:\n    from math import *\nexcept ImportError:\n    pass\nfloor(1.5)")
        self.assertEqual(1, end["returned"])
        _, end = self.serve_one("if True:\n    global x\nx = 1\nx")
        self.assertEqual(1, end["returned"])

    def test_a_while_test_line_is_a_loop_line(self):
        hub, _ = self.serve_one("n = 0\nwhile sg.wait(1) is None and n < 1:\n    n += 1\n")
        self.assertEqual([(2, True), (2, True)], [(p["line"], p["in_loop"]) for _, p in hub.calls])


if __name__ == "__main__":
    unittest.main()
