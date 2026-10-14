"""The fake hub's own fidelity: it refuses what the session refuses, so a test cannot pass against the double where
the real hub refuses (the C-6f0 lesson, applied to this side).

What it says here is what `HubSession.HandleAsync`, `ExtrasAsync` and the pipeline's cursors answer: a method outside a
run's seven is `not_allowed`, the program face passes a mod method on, a `more` whose cursor none of its replies
carried is `cursor_expired` (and the page that ends a walk frees the cursor it was walked with), a second hello and an
unknown message type end the session with a `protocol_error`, `bye` ends it answering nothing, and a call a `cancel`
came for answers `cancelled`.
"""
import threading
import time
import unittest

from fakehub import FakeHub, ok
from sg import Error
from sg._transport import Transport


def wait_until(predicate, timeout=5.0):
    """Waits for a condition the double sets while it acts on a message (its notifications fire when the message
    arrives, so a waiter may wake before the condition holds)."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.01)
    return False


class FakeHubFidelityTests(unittest.TestCase):
    def connect(self, handler=None, face="run"):
        hub, reader, writer = FakeHub.over_pipes(handler, face=face)
        transport = Transport(reader, writer, client="sg-test")
        self.addCleanup(hub.close)
        self.addCleanup(transport.close)
        return hub, transport

    def test_a_method_outside_a_runs_seven_is_not_allowed(self):
        _, transport = self.connect()
        transport.hello(timeout=5)

        reply = transport.call("stream_costs", {})

        self.assertEqual(("not_allowed", False), (reply["error"]["code"], reply["ok"]))
        self.assertIn("go through verb", reply["error"]["message"])

    def test_the_program_face_passes_a_mod_method_on_instead(self):
        _, transport = self.connect(face="program")
        transport.hello(timeout=5)

        self.assertTrue(transport.call("stream_costs", {})["ok"])

    def test_more_with_a_cursor_no_reply_carried_is_cursor_expired(self):
        _, transport = self.connect()
        transport.hello(timeout=5)

        reply = transport.call("more", {"cursor": "c1", "line": 1})

        self.assertEqual("cursor_expired", reply["error"]["code"])
        self.assertEqual({"cursor": "c1"}, reply["error"]["data"])

    def test_more_without_a_cursor_is_invalid_argument(self):
        _, transport = self.connect()
        transport.hello(timeout=5)

        self.assertEqual("invalid_argument", transport.call("more", {"line": 1})["error"]["code"])

    def test_a_cursor_a_reply_carried_is_walked_until_the_page_that_ends_the_walk_frees_it(self):
        def handler(method, params):
            return ok({"things": [{"reference_id": "1"}], "cursor": "c1", "main": "things"}) if method == "verb" \
                else ok({"entries": [{"reference_id": "2"}]})

        _, transport = self.connect(handler)
        transport.hello(timeout=5)
        transport.call("verb", {"verb": "find", "what": "thing", "args": {}, "line": 1})

        page = transport.call("more", {"cursor": "c1", "line": 1})
        again = transport.call("more", {"cursor": "c1", "line": 1})

        self.assertEqual("2", page["result"]["entries"][0]["reference_id"])
        self.assertEqual("cursor_expired", again["error"]["code"])

    def test_a_call_before_hello_is_a_protocol_error_and_ends_the_session(self):
        _, transport = self.connect()

        with self.assertRaises(Error) as unanswered:
            # The call is never answered: the session answers the broken first message and stops
            transport.call("verb", {})
        with self.assertRaises(Error) as refused:
            transport.next_welcome(timeout=5)

        self.assertEqual(("unreachable", "protocol_error"),
                         (unanswered.exception.code, refused.exception.code))

    def test_a_second_hello_is_a_protocol_error_and_ends_the_session(self):
        _, transport = self.connect()
        transport.hello(timeout=5)

        with self.assertRaises(Error) as raised:
            transport.hello(timeout=5)

        self.assertEqual("protocol_error", raised.exception.code)

    def test_an_unknown_message_type_ends_the_session(self):
        hub, transport = self.connect()
        transport.hello(timeout=5)

        transport.send({"type": "shout"})

        with self.assertRaises(Error) as raised:
            transport.next_welcome(timeout=5)

        self.assertEqual("protocol_error", raised.exception.code)
        self.assertTrue(hub.stopped)

    def test_a_runs_stream_end_is_refused_and_the_session_goes_on(self):
        _, transport = self.connect()
        transport.hello(timeout=5)

        transport.send({"type": "stream_end", "stream": 1})
        with self.assertRaises(Error) as raised:
            # The refusal is the session's: an error line with a null id, which the module reads as a hello answer.
            transport.next_welcome(timeout=5)
        reply = transport.call("verb", {})

        self.assertEqual("not_allowed", raised.exception.code)
        self.assertTrue(reply["ok"])

    def test_bye_ends_the_session_and_answers_nothing(self):
        hub, transport = self.connect()
        transport.hello(timeout=5)

        transport.send({"type": "bye"})

        self.assertTrue(wait_until(lambda: transport.closed), "the fake did not end the session")
        self.assertTrue(hub.stopped)
        with self.assertRaises(Error) as raised:
            transport.call("verb", {})
        self.assertEqual("unreachable", raised.exception.code)

    def test_a_cancelled_call_answers_cancelled(self):
        started = threading.Event()

        def handler(method, params):
            started.set()
            time.sleep(0.5)
            return ok({})

        _, transport = self.connect(handler)
        transport.hello(timeout=5)
        replies = []
        # The transport numbers its own calls from 1, and this is its first.
        thread = threading.Thread(target=lambda: replies.append(transport.call("verb", {})))
        thread.start()
        self.assertTrue(started.wait(5))

        transport.send({"type": "cancel", "id": "1"})
        thread.join(10)

        self.assertEqual(("cancelled", True), (replies[0]["error"]["code"], replies[0]["error"]["maybe_ran"]))
