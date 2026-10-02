"""The key proof against the shared vectors, and the default key variable per target."""
import json
import os
import unittest

from _support import FIXTURES, stationgod


class KeyTests(unittest.TestCase):
    def test_proofs_equal_the_shared_vectors(self):
        with open(os.path.join(FIXTURES, "hmac", "vectors.json"), encoding="utf-8") as file:
            cases = json.load(file)["cases"]
        self.assertGreater(len(cases), 0)
        for case in cases:
            self.assertEqual(case["proof"], stationgod.proof(case["key"], case["nonce"], case["client"],
                                                             case["transport"]))

    def test_the_default_variable_depends_on_the_target(self):
        self.assertEqual("STATIONGOD_KEY_STATIONGODMCP", stationgod.key_variable(pipe_name="StationGodMCP"))
        self.assertEqual("STATIONGOD_KEY_STATIONGODMCP_TEST", stationgod.key_variable(pipe_name="StationGodMCP-Test"))
        self.assertEqual("STATIONGOD_KEY_STATIONGODMCP_TEST",
                         stationgod.key_variable(pipe_name="\\\\.\\pipe\\StationGodMCP-Test"))
        self.assertEqual("STATIONGOD_KEY_10_8_0_2_8765", stationgod.key_variable(host="10.8.0.2", port=8765))
        self.assertEqual("STATIONGOD_KEY_GAME_HOST_LAN_18765",
                         stationgod.key_variable(host="game-host.lan", port=18765))

    def test_a_client_reads_its_default_variable(self):
        self.assertEqual("STATIONGOD_KEY_STATIONGODMCP_TEST", stationgod.Client(pipe="StationGodMCP-Test").key_env)
        self.assertEqual("MY_KEY", stationgod.Client(pipe="StationGodMCP", key_env="MY_KEY").key_env)


if __name__ == "__main__":
    unittest.main()
