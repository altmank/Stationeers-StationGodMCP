"""Against the real mod: skipped unless STATIONGOD_LIVE_PIPE names a running pipe (the test server's
StationGodMCP-Test, never the owner's game). Reads only.

    set STATIONGOD_LIVE_PIPE=StationGodMCP-Test
    py -3.12 -m unittest discover -s clients/python/tests -p test_live.py -v

Optional: STATIONGOD_LIVE_CLIENT (a key name; its key in the default variable for the pipe).
"""
import json
import os
import shutil
import tempfile
import unittest

from _support import stationgod

PIPE = os.environ.get("STATIONGOD_LIVE_PIPE")


@unittest.skipUnless(PIPE, "set STATIONGOD_LIVE_PIPE to a running StationGod pipe (the test server's)")
class LiveTests(unittest.TestCase):
    def setUp(self):
        if PIPE.lower().rsplit("\\", 1)[-1] == "stationgodmcp":
            self.skipTest("refusing the default pipe: the live tests are for the test server")
        self.game = stationgod.connect(pipe=PIPE, client=os.environ.get("STATIONGOD_LIVE_CLIENT"))
        self.addCleanup(self.game.close)

    def test_connect_and_welcome(self):
        if self.game.protocol == 2:
            self.assertEqual(PIPE, self.game.welcome["server"]["pipe_name"])
            self.assertTrue(self.game.welcome["server"]["world"]["id"])
        self.assertIn("game_time_s", self.game.call("game_clock"))

    def test_fields_equal_the_projection_of_the_full_reply(self):
        things = self.game.call("find_things", kind="structure", limit=20)["things"]
        ids = [thing["reference_id"] for thing in things][:10]
        if not ids:
            self.skipTest("no structures in this world")
        full = self.game.call("thing_health", reference_ids=ids)
        shaped = self.game.call("thing_health", reference_ids=ids, fields=["reference_id", "damage_ratio"])
        projected = [{key: entry[key] for key in ("reference_id", "damage_ratio") if key in entry}
                     for entry in full["results"]]
        self.assertEqual(projected, shaped["results"])

    def test_iterate_gives_the_ids_of_one_large_call(self):
        # Pause the world first (py -3.12 mcp.py console pause true) or this can differ by things that came or went.
        paged = [thing["reference_id"] for thing in self.game.iterate("find_things", kind="structure", page_size=100)]
        whole = self.game.call("find_things", kind="structure", limit=5000)
        self.assertEqual([thing["reference_id"] for thing in whole["things"]], paged)

    def test_output_file_writes_a_readable_file_and_a_pointer(self):
        folder = tempfile.mkdtemp(prefix="stationgod-live-")
        self.addCleanup(shutil.rmtree, folder, True)
        game = stationgod.connect(pipe=PIPE, output_dir=folder)
        self.addCleanup(game.close)
        pointer = game.call("find_things", kind="structure", limit=50, output_file=True)
        with open(pointer["output_file"], encoding="utf-8") as file:
            reply = json.load(file)
        self.assertEqual(len(reply["things"]), pointer["counts"]["things"])


if __name__ == "__main__":
    unittest.main()
