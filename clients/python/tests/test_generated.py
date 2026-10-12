"""The generated verb layer against catalogue/verbs: current, complete, and each function calls its own verb."""
import ast
import importlib.util
import os
import unittest

from _support import HubCase
from fakehub import PACKAGE_ROOT
import sg
from sg import _catalogue

GENERATOR = os.path.join(PACKAGE_ROOT, "generate_catalogue.py")


def generator():
    spec = importlib.util.spec_from_file_location("generate_catalogue", GENERATOR)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class GeneratedTests(unittest.TestCase):
    def test_the_generated_files_are_current(self):
        module = generator()
        for name, text in module.render_all().items():
            self.assertEqual(text, module.current(name),
                             f"sg/{name} is stale: run py -3 clients/python/generate_catalogue.py")

    def test_the_stubs_parse_and_overload_every_what(self):
        with open(os.path.join(PACKAGE_ROOT, "sg", "_verbs.pyi"), encoding="utf-8") as file:
            tree = ast.parse(file.read())
        names = [node.name for node in tree.body if isinstance(node, ast.FunctionDef)]
        self.assertEqual(len(_catalogue.WHATS["find"]), names.count("find"))
        self.assertEqual(1, names.count("undo"))

    def test_every_verb_but_help_and_run_script_has_a_function(self):
        for verb in _catalogue.VERBS:
            if verb not in ("help", "run_script"):
                self.assertTrue(callable(getattr(sg, verb)), verb)


class GeneratedCallTests(HubCase):
    def test_each_function_sends_its_own_verb(self):
        for verb, whats in _catalogue.WHATS.items():
            if verb in ("help", "run_script"):
                continue
            function = getattr(sg, verb)
            if whats == ("-",):
                function()
            else:
                function(whats[0])
        sent = [(params["verb"], params.get("what")) for params in self.params("verb")]
        expected = [(verb, None if whats == ("-",) else whats[0]) for verb, whats in _catalogue.WHATS.items()
                    if verb not in ("help", "run_script")]
        self.assertEqual(expected, sent)


if __name__ == "__main__":
    unittest.main()
