"""The script worker as the hub runs it: a child process on stdin and stdout, against the fake hub."""
import unittest

from _support import FakeHub, ok, refused
from sg._analysis import analyse


def run(code, run_id="script-1"):
    return {"id": run_id, "code": code, "confirm": True, "face": "script", "limits": {"cut_bytes": 4096}}


class WorkerCase(unittest.TestCase):
    def serve(self, codes, handler=None):
        """Runs each code in one worker, in order; returns (hub, [run_end, ...]) once the worker has exited."""
        hub, process = FakeHub.with_worker([run(code, f"script-{n}") for n, code in enumerate(codes, 1)], handler)
        try:
            exit_code = process.wait(30)
            hub.wait_for(lambda hub: hub.of_type("_eof"))
        finally:
            if process.poll() is None:
                process.kill()
            errors = process.stderr.read().decode("utf-8", "replace")
            process.stderr.close()
            process.stdin.close()
        self.assertEqual(0, exit_code, errors)
        ends = hub.of_type("run_end")
        self.assertEqual(len(codes), len(ends), errors)
        return hub, ends

    def serve_one(self, code, handler=None):
        hub, ends = self.serve([code], handler)
        return hub, ends[0]


class AnalysisTests(unittest.TestCase):
    """The AST pass, in process (the worker sends exactly this; WorkerTests checks that it is sent first)."""

    def test_literals_are_ints_and_strs_with_f_string_pieces_and_no_bools(self):
        analysis = analyse("pump = '122643'\nn = 15\nflag = True\nx = 2.5\nsg.write('logic', id=pump, logic='Open',"
                           " value=1)\nlabel = f'tank {n} of {pump}'\nm = -4\n")
        self.assertEqual(["122643", 15, "logic", "Open", 1, "tank ", " of ", -4, 4], analysis.literals)

    def test_while_lines_are_every_line_inside_a_while_body(self):
        analysis = analyse("n = 0\nwhile n < 3:\n    n += 1\n    if n:\n        sg.wait(\n            1)\nprint(n)\n")
        self.assertEqual([3, 4, 5, 6], analysis.while_lines)

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
        self.assertEqual(["hello", "run_analysis", "run_end", "hello", "_eof"], kinds)
        analysis = hub.of_type("run_analysis")[0]
        self.assertEqual({"type": "run_analysis", "literals": ["thing", 1], "while_lines": [3], "writes": False,
                          "holds": False, "syntax_error": None}, analysis)
        self.assertEqual("sg-worker", hub.of_type("hello")[0]["client"]["name"])

    def test_the_final_bare_expression_is_returned(self):
        _, end = self.serve_one("r = sg.find('thing')\n{'n': len(r['things']), 'id': r['things'][0]['reference_id']}",
                                find_handler)
        self.assertEqual(("done", {"n": 1, "id": "7"}, None), (end["state"], end["returned"], end["error"]))

    def test_return_works_and_a_value_json_cannot_hold_is_its_repr(self):
        _, ends = self.serve(["return 5\n", "return {1, 2}"])
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

    def test_a_stop_swallowed_by_the_code_ends_the_run_done(self):
        handler = lambda method, params: refused("stopped", "stopped", stopped=True)  # noqa: E731
        _, end = self.serve_one("try:\n    sg.wait(1)\nexcept BaseException:\n    pass\n'kept'", handler)
        self.assertEqual(("done", "kept"), (end["state"], end["returned"]))

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

    def test_two_runs_in_sequence_in_one_worker_each_in_a_fresh_namespace(self):
        hub, ends = self.serve(["secret = 41\nsecret + 1", "secret"])
        self.assertEqual(["done", "failed"], [end["state"] for end in ends])
        self.assertEqual(42, ends[0]["returned"])
        self.assertIn("NameError", ends[1]["error"])
        self.assertEqual(3, len(hub.of_type("hello")))  # one per run, and one answered by end of stream


if __name__ == "__main__":
    unittest.main()
