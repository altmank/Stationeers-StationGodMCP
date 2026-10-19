"""sg.values and sg.value against the fake hub."""
import unittest

from _support import HubCase, ok
import sg
from sg import MainList, Row


def answer_reads(fail=()):
    """A read logic handler: pair entries answer pair rows, logic-list entries answer per-item rows.
    fail: set of (id, logic) that fail (code "not_readable"), or ids alone that fail whole."""
    def handler(method, params):
        if method != "verb":
            return ok({})
        results = []
        for index, entry in enumerate(params["args"]["reads"]):
            rid = str(entry["id"])
            if rid in fail:
                results.append({"index": index, "ok": False, "reference_id": rid,
                                "error": {"code": "not_found", "message": "x"}})
            elif isinstance(entry["logic"], list):
                logic, errors = {}, {}
                for name in entry["logic"]:
                    if (rid, name) in fail:
                        errors[name] = {"code": "not_readable", "message": "x"}
                    else:
                        logic[name] = float(rid)
                results.append({"index": index, "ok": True, "reference_id": rid, "logic": logic,
                                "logic_errors": errors or None})
            elif (rid, entry["logic"]) in fail:
                results.append({"index": index, "ok": False, "reference_id": rid,
                                "error": {"code": "not_readable", "message": "x"}})
            else:
                results.append({"index": index, "ok": True, "reference_id": rid,
                                "logic_type": {"id": 1, "name": entry["logic"]}, "value": float(rid)})
        return ok({"results": results})
    return handler


class ValuesTests(HubCase):
    def setUp(self):
        super().setUp()
        self.handler = answer_reads()

    def sizes(self):
        return [len(p["args"]["reads"]) for p in self.params("verb")]

    def test_256_pairs_one_call(self):
        result = sg.values(list(range(1, 257)), "On")
        self.assertEqual([256], self.sizes())
        self.assertEqual({"On": 7.0}, result[7])
        self.assertEqual("logic", self.params("verb")[0]["what"])

    def test_257_pairs_two_calls(self):
        result = sg.values(list(range(1, 258)), "On")
        self.assertEqual([256, 1], self.sizes())
        self.assertEqual(257, len(result))

    def test_128_items_one_call_129_two(self):
        sg.values(list(range(1, 129)), "On", "Power")
        self.assertEqual([128], self.sizes())
        self.hub.calls.clear()
        result = sg.values(list(range(1, 130)), "On", "Power")
        self.assertEqual([128, 1], self.sizes())
        self.assertEqual({"On": 129.0, "Power": 129.0}, result[129])

    def test_1024_values_one_call_1025_two(self):
        logic = ["L%d" % n for n in range(8)]
        sg.values(list(range(1, 129)), *logic)  # 128 x 8 = 1024
        self.assertEqual([128], self.sizes())
        self.hub.calls.clear()
        logic = ["L%d" % n for n in range(41)]
        sg.values(list(range(1, 26)), *logic)  # 25 x 41 = 1025
        self.assertEqual([24, 1], self.sizes())

    def test_mixed_failures_raise_once_naming_all(self):
        self.handler = answer_reads(fail={"2", ("3", "Power"), ("4", "On")})
        with self.assertRaises(sg.Error) as caught:
            sg.values(list(range(1, 200)), "On", "Power")
        self.assertEqual("read_failed", caught.exception.code)
        message = caught.exception.message
        for part in ("(2, 'On', not_found)", "(2, 'Power', not_found)", "(3, 'Power', not_readable)",
                     "(4, 'On', not_readable)"):
            self.assertIn(part, message)
        self.assertEqual([128, 71], self.sizes())  # every chunk read before raising

    def test_pair_failures_raise_once(self):
        self.handler = answer_reads(fail={("5", "On"), ("300", "On")})
        with self.assertRaises(sg.Error) as caught:
            sg.values(list(range(1, 301)), "On")
        self.assertIn("(5, 'On', not_readable)", caught.exception.message)
        self.assertIn("(300, 'On', not_readable)", caught.exception.message)
        self.assertEqual([256, 44], self.sizes())

    def test_mainlist_rows_and_strings(self):
        rows = MainList([Row(reference_id="11"), Row(id="12")])
        self.assertEqual({"11": {"On": 11.0}, "12": {"On": 12.0}}, sg.values(rows, "On"))
        self.assertEqual({"13": {"On": 13.0}}, sg.values(["13"], "On"))
        self.assertEqual({"14": {"On": 14.0}}, sg.values(Row(reference_id="14"), "On"))

    def test_ids_are_the_replies_strs_and_an_int_finds_the_same_entry(self):
        result = sg.values([15, "16"], "On")
        self.assertEqual(["15", "16"], list(result))
        self.assertEqual(result["15"], result[15])
        self.assertIn(16, result)
        with self.assertRaises(KeyError):
            result[17]

    def test_value(self):
        self.assertEqual(9.0, sg.value(9, "Temperature"))
        self.assertEqual([{"id": "9", "logic": "Temperature"}], self.params("verb")[0]["args"]["reads"])

    def test_one_pair_on_both_faces(self):
        # A reads list answers rows on every face (read.json:30), one pair too; a one-pair shape is read as row 0.
        answers = {
            "rows": {"results": [{"index": 0, "ok": True, "reference_id": "9",
                                  "logic_type": {"id": 6, "name": "Temperature"}, "value": 293.0}],
                     "count": 1, "success_count": 1, "error_count": 0},
            "one": {"reference_id": "9", "logic_type": {"id": 6, "name": "Temperature"}, "value": 293.0},
        }
        for face, answer in answers.items():
            with self.subTest(face=face):
                self.handler = lambda method, params, answer=answer: ok(answer)
                self.assertEqual(293.0, sg.value("9", "Temperature"))
                self.assertEqual({"9": {"Temperature": 293.0}}, sg.values("9", "Temperature"))

    def test_one_pair_that_failed_raises_read_failed(self):
        self.handler = answer_reads(fail={("9", "Temperature")})
        with self.assertRaises(sg.Error) as caught:
            sg.value(9, "Temperature")
        self.assertEqual("read_failed", caught.exception.code)
        self.assertEqual([{"id": "9", "logic": "Temperature", "code": "not_readable"}],
                         caught.exception.data["failed"])

    def test_no_logic_type_is_invalid_argument(self):
        with self.assertRaises(sg.Error) as caught:
            sg.values([1, 2])
        self.assertEqual("invalid_argument", caught.exception.code)
        self.assertEqual([], self.params("verb"))

if __name__ == "__main__":
    unittest.main()
