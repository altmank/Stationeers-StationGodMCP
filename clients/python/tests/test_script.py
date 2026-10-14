"""The save-time parse, sg._script: main's signature and the id literals a shared script may not carry.

The hub is C#: it runs this module once per write script and keeps what it answers in the script's header (the
parameters run_script checks a run's arguments against) and refuses a shared script whose ids list is not empty.
"""
import unittest

import fakehub  # noqa: F401 - puts the package on sys.path
from sg._script import parse  # noqa: E402


class ScriptParseTests(unittest.TestCase):
    def test_main_is_required_and_async_main_is_refused(self):
        missing = parse("x = 1\n")
        self.assertFalse(missing["ok"])
        self.assertIn("no def main(...)", missing["error"])
        self.assertEqual("main may not be async", parse("async def main():\n    pass\n")["error"])

    def test_a_syntax_error_names_its_line(self):
        broken = parse("x = 1\ny = (\n")

        self.assertFalse(broken["ok"])
        self.assertTrue(broken["error"].startswith("line 2: "), broken["error"])

    def test_positional_only_parameters_are_refused(self):
        self.assertIn("positional-only", parse("def main(a, /):\n    pass\n")["error"])

    def test_parameters_come_in_signature_order_with_annotations_defaults_and_kinds(self):
        parsed = parse("def main(a: int, b: str = 'x', *rest, c: float = 1.0, **named) -> None:\n    pass\n")

        self.assertEqual([
            {"name": "a", "type": "int", "default": False, "kind": "pos_or_kw"},
            {"name": "b", "type": "str", "default": True, "kind": "pos_or_kw"},
            {"name": "rest", "type": None, "default": False, "kind": "var_positional"},
            {"name": "c", "type": "float", "default": True, "kind": "keyword_only"},
            {"name": "named", "type": None, "default": False, "kind": "var_keyword"}
        ], parsed["parameters"])

    def test_a_parameter_with_no_annotation_has_none_and_strings_are_text(self):
        parsed = parse("def main(a, b: 'int', c: list[int], d: dict[str, int] | None):\n    pass\n")

        self.assertEqual([None, "int", "list[int]", "dict[str, int] | None"],
                         [parameter["type"] for parameter in parsed["parameters"]])

    def test_a_run_of_a_script_with_no_parameters_has_an_empty_parameters_list(self):
        self.assertEqual([], parse("def main():\n    pass\n")["parameters"])

    def test_id_literals_are_the_id_positions_and_id_shaped_constants(self):
        for code in ["def main():\n    sg.read('logic', id=42)\n",
                     "def main():\n    sg.read('logic', id=122643)\n",
                     "def main():\n    sg.read('logic', id='122643')\n",
                     "def main():\n    sg.read('logic', reads=[{'id': 42}])\n",
                     "def main():\n    sg.move('item', reference_id='7')\n",
                     "def main():\n    sg.move('item', to_id=7, from_id=8)\n",
                     "def main():\n    target = '4820585'\n",
                     "def main():\n    target = 100000\n",
                     "def main():\n    sg.write('logic', ids=[7, '8'], logic='On', value=1)\n",
                     "def main():\n    print(f'thing {122643}')\n"]:
            self.assertTrue(parse(code)["ids"], code)

    def test_literals_that_are_not_ids_are_not_found(self):
        for code in ["def main():\n    sg.wait(1000)\n",
                     "def main():\n    count = 42\n    print(count, 'hello')\n",
                     "def main():\n    sg.inspect('thing', id=external)\n",
                     # the documented hole: a composite id outside an id position
                     "def main():\n    target = '5-1234'\n"]:
            self.assertEqual([], parse(code)["ids"], code)

    def test_an_id_built_at_run_time_is_the_documented_hole(self):
        # The rule is partial by necessity: arithmetic, int() and f-string interpolation are not literals.
        self.assertEqual([], parse("def main():\n    sg.read('logic', id=int('12' + '2643'))\n")["ids"])


if __name__ == "__main__":
    unittest.main()
