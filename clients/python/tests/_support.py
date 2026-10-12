"""Shared test helpers: sg connected in process to a fake hub over pipes, and running code as a run would."""
import unittest

import fakehub  # noqa: F401 - puts the package on sys.path
from fakehub import FakeHub, ok, refused  # noqa: F401

import sg  # noqa: E402
from sg import _session  # noqa: E402
from sg._analysis import RUN_FUNCTION, analyse  # noqa: E402
from sg._transport import Transport  # noqa: E402


class HubCase(unittest.TestCase):
    """Each test gets a fake hub answering through `self.answer(method, params)` (override or set self.handler) and
    sg connected to it."""

    def setUp(self):
        self.handler = lambda method, params: ok({})
        self.hub, reader, writer = FakeHub.over_pipes(lambda method, params: self.handler(method, params))
        self.transport = Transport(reader, writer, client="sg-test")
        self.welcome = self.transport.hello(timeout=5)
        _session.use(self.transport)
        self.addCleanup(self._close)

    def _close(self):
        _session.end_run()
        _session.use(None)
        self.transport.close()
        self.hub.close()

    def params(self, method):
        """The params of every call of method so far."""
        return [params for name, params in self.hub.calls if name == method]

    def run_code(self, source, run_id="script-1"):
        """Runs source as a run's code would run (compiled as <script>, while_lines known); returns its value."""
        analysis = analyse(source)
        self.assertIsNone(analysis.syntax_error)
        namespace = {"sg": sg, "__name__": "__script__"}
        exec(analysis.code, namespace)
        _session.begin_run(_session.Run(run_id, analysis.while_lines))
        try:
            return namespace[RUN_FUNCTION]()
        finally:
            _session.end_run()
