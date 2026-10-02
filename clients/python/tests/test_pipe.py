"""pipe.py on a real Windows named pipe: both ends overlapped, a pending read never blocks a write (stage 3's duplex
check for the Python side)."""
import threading
import time
import unittest

from _support import stationgod  # noqa: F401  (puts the package on sys.path)
from stationgod import pipe


class PipePair:
    def __init__(self):
        self.name = f"StationGodPipeTest-{time.time_ns()}"
        self.listener = pipe.PipeListener(self.name)
        accepted = {}
        thread = threading.Thread(target=lambda: accepted.setdefault("server", self.listener.accept()))
        thread.start()
        time.sleep(0.02)
        self.client = pipe.connect(self.name, 1.0)
        thread.join(2)
        self.server = accepted["server"]

    def close(self):
        self.client.close()
        self.server.close()
        self.listener.close()


class PipeTests(unittest.TestCase):
    def setUp(self):
        self.pair = PipePair()
        self.addCleanup(self.pair.close)

    def _pending_read(self, end):
        got = []
        thread = threading.Thread(target=lambda: got.append((end.read(), time.perf_counter())))
        thread.start()
        time.sleep(0.1)  # the read is now pending
        return thread, got

    def test_a_client_with_a_read_pending_can_write(self):
        reader, got_by_client = self._pending_read(self.pair.client)
        server_reader, got_by_server = self._pending_read(self.pair.server)
        started = time.perf_counter()
        self.pair.client.write(b'{"type":"call"}\n')
        server_reader.join(1)
        self.assertEqual(b'{"type":"call"}\n', got_by_server[0][0])
        self.assertLess(got_by_server[0][1] - started, 0.1)
        started = time.perf_counter()
        self.pair.server.write(b'{"type":"reply"}\n')
        reader.join(1)
        self.assertEqual(b'{"type":"reply"}\n', got_by_client[0][0])
        self.assertLess(got_by_client[0][1] - started, 0.1)

    def test_closing_ends_a_pending_read(self):
        reader, got = self._pending_read(self.pair.client)
        self.pair.client.close()
        reader.join(1)
        self.assertFalse(reader.is_alive())
        self.assertEqual(b"", got[0][0])

    def test_the_other_end_closing_ends_a_read(self):
        reader, got = self._pending_read(self.pair.client)
        self.pair.server.close()
        reader.join(1)
        self.assertEqual(b"", got[0][0])

    def test_writing_to_a_closed_pipe_raises(self):
        self.pair.server.close()
        time.sleep(0.05)
        with self.assertRaises(OSError):
            for _ in range(3):
                self.pair.client.write(b"x\n")

    def test_a_large_write_arrives_whole(self):
        data = b"y" * (1 << 20) + b"\n"
        received = bytearray()

        def drain():
            while len(received) < len(data):
                chunk = self.pair.server.read()
                if not chunk:
                    return
                received.extend(chunk)

        thread = threading.Thread(target=drain)
        thread.start()
        self.pair.client.write(data)
        thread.join(5)
        self.assertEqual(data, bytes(received))


class ConnectTests(unittest.TestCase):
    def test_a_missing_pipe_says_so(self):
        with self.assertRaises(pipe.PipeMissing) as raised:
            pipe.connect(f"StationGodNoSuchPipe-{time.time_ns()}", 0.1)
        self.assertIn("not running", str(raised.exception))

    def test_a_busy_pipe_says_so(self):
        pair = PipePair()   # its only instance is taken
        self.addCleanup(pair.close)
        with self.assertRaises(pipe.PipeBusy) as raised:
            pipe.connect(pair.name, 0.2)
        self.assertIn("busy", str(raised.exception))


if __name__ == "__main__":
    unittest.main()
