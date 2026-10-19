"""sg.ok and sg.failed against the fake hub."""
import unittest

from _support import HubCase, ok
import sg

GOOD = [{"index": 0, "ok": True, "reference_id": "5", "value": 1.0},
        {"index": 1, "ok": True, "reference_id": "6", "value": 2.0}]
BAD = {"index": 1, "ok": False, "reference_id": "6",
       "error": {"code": "not_found", "message": "no such id", "fix": "find it first"}}


class RowsTests(HubCase):
    def reply(self, payload):
        self.handler = lambda method, params: ok(payload)
        return sg.read("logic", reads=[{"id": "5", "logic": "Pressure"}])

    def test_all_ok_returns_the_rows_and_no_failed(self):
        reply = self.reply({"results": GOOD})
        self.assertEqual(GOOD, [dict(r) for r in sg.ok(reply)])
        self.assertEqual([], sg.failed(reply))

    def test_some_failed_raises_rows_failed_and_failed_lists_them(self):
        reply = self.reply({"results": [GOOD[0], BAD]})
        with self.assertRaises(sg.Error) as caught:
            sg.ok(reply)
        self.assertEqual("rows_failed", caught.exception.code)
        self.assertEqual([1], [r["index"] for r in sg.failed(reply)])

    def test_error_message_names_index_reference_code_and_fix(self):
        reply = self.reply({"results": [BAD, dict(BAD, index=2, reference_id="7")]})
        with self.assertRaises(sg.Error) as caught:
            sg.ok(reply)
        message = caught.exception.message
        for part in ("1", "6", "7", "not_found", "find it first"):
            self.assertIn(part, message)
        self.assertIn("2", message)

    def test_a_row_without_a_fix_shows_no_fix_part(self):
        reply = self.reply({"results": [GOOD[0], {"index": 1, "ok": False, "reference_id": "6",
                                                  "error": {"code": "not_found", "message": "no such id"}}]})
        with self.assertRaises(sg.Error) as caught:
            sg.ok(reply)
        self.assertEqual("1 of 2 rows failed: row 1 (6): not_found: no such id", caught.exception.message)
        self.assertNotIn("fix", caught.exception.message)

    def test_one_id_on_both_faces(self):
        # One id: the script face answers the what's one-id shape, the program face {results: [row]} (hub.md Faces).
        one = {"reference_id": "5", "logic_type": {"id": 1, "name": "On"}, "value": 1.0}
        script = self.reply(one)
        self.assertEqual([script], sg.ok(script))
        self.assertEqual([], sg.failed(script))
        program = self.reply({"results": [dict(one, index=0, ok=True)], "count": 1})
        self.assertEqual(["5"], [row["reference_id"] for row in sg.ok(program)])
        failed = self.reply({"results": [BAD]})
        self.assertEqual([1], [row["index"] for row in sg.failed(failed)])
        with self.assertRaises(sg.Error):
            sg.ok(failed)

    def test_rows_behind_a_cursor_are_paged_in(self):
        def handler(method, params):
            if method == "more":
                return ok({"entries": [BAD]})
            return ok({"results": [GOOD[0]], "main": "results", "cursor": "c1"})
        self.handler = handler
        reply = sg.read("logic", reads=[{"id": "5", "logic": "On"}, {"id": "6", "logic": "On"}])
        self.assertEqual([1], [row["index"] for row in sg.failed(reply)])
        with self.assertRaises(sg.Error) as caught:
            sg.ok(reply)
        self.assertEqual("rows_failed", caught.exception.code)

    def test_no_results_key(self):
        reply = self.reply({"count": 3})
        self.assertEqual([reply], sg.ok(reply))
        self.assertEqual([], sg.failed(reply))


if __name__ == "__main__":
    unittest.main()
