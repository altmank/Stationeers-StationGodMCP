"""Shared test helpers: the package on sys.path, a fake mod per test, and polling for a condition."""
import os
import sys
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
REPOSITORY = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
FIXTURES = os.path.join(REPOSITORY, "clients", "fixtures")

import stationgod  # noqa: E402
from stationgod import client as client_module  # noqa: E402
from fakemod import FakeError, FakeMod  # noqa: E402,F401


def wait_for(predicate, timeout=5.0, what="condition"):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.01)
    raise AssertionError(f"timed out waiting for {what}")


class FakeModCase(unittest.TestCase):
    transport = "tcp"

    def setUp(self):
        grace = client_module.REPLY_GRACE_S
        client_module.REPLY_GRACE_S = 0.5     # keep "no reply in time" tests short
        self.addCleanup(setattr, client_module, "REPLY_GRACE_S", grace)

    def mod(self, **options):
        mod = FakeMod(transport=self.transport, **options).start()
        self.addCleanup(mod.stop)
        return mod

    def client(self, mod, **options):
        """A client of the fake mod; over TCP it sends the fake mod's shared secret first."""
        if self.transport == "tcp" and "secret_env" not in options:
            os.environ["STATIONGOD_TEST_SECRET"] = mod.legacy_secret or ""
            options["secret_env"] = "STATIONGOD_TEST_SECRET"
        game = stationgod.Client(**mod.client_options(), **options)
        self.addCleanup(game.close)
        return game
