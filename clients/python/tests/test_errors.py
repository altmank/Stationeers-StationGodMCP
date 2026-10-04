"""Error replies become exceptions that keep the error's see: the tool_info node that explains the code."""
import unittest

from _support import stationgod  # noqa: F401  (puts the package on the path)
from stationgod.errors import GameError, NotFound, game_error


class ErrorTests(unittest.TestCase):
    def test_an_error_keeps_its_see(self):
        error = game_error({"code": "thing_not_found", "message": "No thing with reference id 4.",
                            "see": {"topic": "errors", "subtopic": "not_found"}})

        self.assertIsInstance(error, NotFound)
        self.assertEqual({"topic": "errors", "subtopic": "not_found"}, error.see)

    def test_an_error_without_see_has_an_empty_one(self):
        self.assertEqual({}, game_error({"code": "x", "message": "y"}).see)
        self.assertEqual({}, GameError("x", "y").see)


if __name__ == "__main__":
    unittest.main()
