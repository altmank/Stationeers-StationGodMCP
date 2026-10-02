"""output_file on the caller's machine: every shared fixture in clients/fixtures/output_file, pruning, and a failed
write answered inline."""
import glob
import json
import os
import re
import shutil
import tempfile
import time
import unittest

from _support import FIXTURES, stationgod  # noqa: F401
from stationgod import output
from stationgod.errors import InvalidArgument


class OutputFixtureTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.mkdtemp(prefix="stationgod-output-")
        self.addCleanup(shutil.rmtree, self.folder, True)

    def test_every_shared_fixture(self):
        paths = sorted(glob.glob(os.path.join(FIXTURES, "output_file", "*.json")))
        self.assertGreater(len(paths), 5)
        for path in paths:
            with self.subTest(fixture=os.path.basename(path)):
                with open(path, encoding="utf-8") as file:
                    self._run(json.load(file))

    def _run(self, case):
        if "expected_error" in case:
            with self.assertRaises(InvalidArgument) as raised:
                output.target(case["output_file"])
            self.assertEqual(case["expected_error"], raised.exception.code)
            return
        name = output.target(case["output_file"])
        if "expected_inline" in case:
            self.assertIsNone(name)
            return
        pointer = output.write(case["method"], name, case["result"], self.folder)
        written = pointer["output_file"]
        self.assertEqual(os.path.abspath(self.folder), os.path.dirname(written))
        file_name = os.path.basename(written)
        if "expected_file_name" in case:
            self.assertEqual(case["expected_file_name"], file_name)
        else:
            self.assertRegex(file_name, case["expected_file_name_pattern"])
        self.assertEqual(os.path.getsize(written), pointer["bytes"])
        with open(written, encoding="utf-8") as file:
            self.assertEqual(case["expected_file"], json.load(file))
        without_path = {key: value for key, value in pointer.items() if key not in ("output_file", "bytes")}
        self.assertEqual(case["expected_pointer"], without_path)
        self.assertEqual(list(case["expected_pointer"]), list(without_path), "key order")


class FolderTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.mkdtemp(prefix="stationgod-output-")
        self.addCleanup(shutil.rmtree, self.folder, True)

    def test_pruning_keeps_the_newest_200_and_drops_old_files(self):
        now = time.time()
        for index in range(output.MAXIMUM_FILES + 5):
            path = os.path.join(self.folder, f"old-{index:03d}.json")
            with open(path, "w") as file:
                file.write("{}")
            os.utime(path, (now - 60 * (index + 1), now - 60 * (index + 1)))
        expired = os.path.join(self.folder, "expired.json")
        with open(expired, "w") as file:
            file.write("{}")
        os.utime(expired, (now - output.MAXIMUM_AGE_S - 3600,) * 2)
        other = os.path.join(self.folder, "notes.txt")
        with open(other, "w") as file:
            file.write("not ours")

        output.write("find_things", "fresh.json", {}, self.folder)

        self.assertEqual(output.MAXIMUM_FILES, len(glob.glob(os.path.join(self.folder, "*.json"))))
        self.assertTrue(os.path.exists(os.path.join(self.folder, "fresh.json")))
        self.assertTrue(os.path.exists(os.path.join(self.folder, "old-000.json")))
        self.assertFalse(os.path.exists(os.path.join(self.folder, f"old-{output.MAXIMUM_FILES - 1:03d}.json")))
        self.assertFalse(os.path.exists(expired))
        self.assertTrue(os.path.exists(other))

    def test_a_failed_write_answers_the_reply_inline(self):
        blocked = os.path.join(self.folder, "a-file-not-a-folder")
        with open(blocked, "w") as file:
            file.write("x")
        answer = output.write("find_things", "", {"things": [{"a": 1}]}, blocked)
        self.assertEqual([{"a": 1}], answer["things"])
        self.assertTrue(answer["output_file_error"].startswith("Could not write "))

    def test_a_named_file_is_replaced_whole(self):
        output.write("rooms", "same.json", {"rooms": [1, 2, 3]}, self.folder)
        output.write("rooms", "same.json", {"rooms": [4]}, self.folder)
        with open(os.path.join(self.folder, "same.json"), encoding="utf-8") as file:
            self.assertEqual({"rooms": [4]}, json.load(file))
        self.assertEqual([], glob.glob(os.path.join(self.folder, "*.tmp")))

    def test_the_folder_comes_from_the_option_then_the_environment_then_localappdata(self):
        saved = os.environ.get(output.ENVIRONMENT_VARIABLE)
        try:
            os.environ[output.ENVIRONMENT_VARIABLE] = "C:\\b"
            self.assertEqual("C:\\a", str(output.folder("C:\\a")))
            self.assertEqual("C:\\b", str(output.folder(None)))
            del os.environ[output.ENVIRONMENT_VARIABLE]
            self.assertTrue(re.search(r"StationGodMCP[\\/]output$", str(output.folder(" "))))
        finally:
            if saved is None:
                os.environ.pop(output.ENVIRONMENT_VARIABLE, None)
            else:
                os.environ[output.ENVIRONMENT_VARIABLE] = saved


if __name__ == "__main__":
    unittest.main()
