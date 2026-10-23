"""How a failed call is written back: short calls whole, long values and long calls cut."""
import unittest

from sg._calls import CALL_LIMIT, call_text


class CallTextTests(unittest.TestCase):
    def test_a_short_call_reads_as_the_python_that_sent_it(self):
        call = {"verb": "write", "what": "logic", "args": {"id": "122643", "logic": "On", "value": 1}}

        self.assertEqual("sg.write('logic', id='122643', logic='On', value=1)", call_text(call))

    def test_a_large_list_shows_its_start_and_size(self):
        placements = [{"prefab": "StructureFrame", "at": [i, 96, 898]} for i in range(168)]
        call = {"verb": "show", "what": "preview", "args": {"placements": placements, "style": "ghost"}}

        text = call_text(call)

        self.assertLessEqual(len(text), CALL_LIMIT + 30)
        self.assertIn("168 items", text)
        self.assertTrue(text.startswith("sg.show('preview', placements=[{'prefab': 'StructureFrame'"))

    def test_many_values_are_cut_as_a_whole(self):
        call = {"verb": "find", "what": "thing", "args": {f"option_{i}": "x" * 100 for i in range(20)}}

        text = call_text(call)

        self.assertLessEqual(len(text), CALL_LIMIT + 30)
        self.assertTrue(text.endswith(" chars)"))


if __name__ == "__main__":
    unittest.main()
