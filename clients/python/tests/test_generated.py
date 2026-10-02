"""The generated module against catalogue.json: fails when it is stale, and every stub calls its method."""
import hashlib
import importlib.util
import os
import unittest

from _support import REPOSITORY, stationgod
from stationgod import _methods

GENERATOR = os.path.join(REPOSITORY, "clients", "python", "generate_catalogue.py")


def _generator():
    spec = importlib.util.spec_from_file_location("generate_catalogue", GENERATOR)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class Recorder(stationgod.Client):
    def __init__(self):
        super().__init__(pipe="unused")
        self.seen = []

    def call(self, method, /, **params):
        self.seen.append((method, params))
        return {}


class GeneratedTests(unittest.TestCase):
    def test_the_generated_module_is_current_with_catalogue_json(self):
        generator = _generator()
        with open(os.path.join(REPOSITORY, "catalogue.json"), "rb") as source:
            expected = generator.render(source.read())
        with open(generator.OUTPUT, encoding="utf-8", newline="") as current:
            self.assertEqual(expected, current.read(),
                             "stationgod/_methods.py is stale: run py -3.12 clients/python/generate_catalogue.py")

    def test_the_hash_is_the_sha256_of_the_exact_bytes(self):
        with open(os.path.join(REPOSITORY, "catalogue.json"), "rb") as source:
            self.assertEqual("sha256:" + hashlib.sha256(source.read()).hexdigest(), _methods.CATALOGUE_HASH)

    def test_every_method_has_a_stub_that_calls_it(self):
        recorder = Recorder()
        methods = sorted(name for name, entry in _methods.TABLE.items() if not entry["protocol"])
        self.assertGreater(len(methods), 80)
        for name in methods:
            getattr(recorder, name)()
        self.assertEqual(methods, [method for method, _ in recorder.seen])

    def test_a_stub_passes_arguments_and_options_and_maps_python_keywords(self):
        recorder = Recorder()
        recorder.move_gas(from_="12", to="13", dry_run=True, deadline_ms=5000)
        method, params = recorder.seen[0]
        self.assertEqual("move_gas", method)
        self.assertEqual("12", params["from"])
        self.assertEqual(True, params["dry_run"])
        self.assertEqual(5000, params["deadline_ms"])
        self.assertIsNone(params["transfer_id"])   # None is omitted by call()

    def test_the_read_only_set_leaves_out_drawing_and_argument_dependent_methods(self):
        self.assertIn("find_things", _methods.READ_ONLY)
        for name in ("highlight", "show_preview", "place_cables", "write_logic", "move_gas"):
            self.assertNotIn(name, _methods.READ_ONLY)


if __name__ == "__main__":
    unittest.main()
