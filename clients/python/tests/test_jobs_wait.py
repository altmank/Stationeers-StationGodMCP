"""sg.until_job against the fake hub: jobs that end after or before the wait, merging, failure, timeout, report run,
both faces' inspect replies, and the swap and upgrade jobs' own lists."""
import time
from unittest import mock

from _support import HubCase, ok
import sg
from sg import _jobs_wait


def ended(job):
    return {"type": "event", "event": "job_ended", "subscription": "s1", "seq": 1, "frame": 9, "job": job}


def as_row(job):
    """inspect job's one id on the program face: {results: [row]}, the job's keys in the row (hub.md *Faces*)."""
    return {"results": [dict(job, index=0, ok=True, reference_id=job["job_id"])], "count": 1, "success_count": 1,
            "error_count": 0}


class UntilJobTests(HubCase):
    features = ["pipeline", "subscriptions"]

    def make_handler(self, inspected, on_wait=None, rows=True, wait_answer=None):
        """inspected: job id -> the job object inspect job answers; rows: the program face's {results: [row]},
        else the script face's one-id shape."""
        def handler(method, params):
            if method == "subscribe":
                return ok({"subscription": "s1", "interval_s": 1.0, "values": 0, "frame": 5})
            if method == "verb" and params["verb"] == "inspect":
                job = inspected[params["args"]["id"]]
                return ok(as_row(job) if rows else job)
            if method == "wait":
                if on_wait is not None:
                    on_wait()
                elif wait_answer is None:
                    time.sleep(params["s"])
                return ok(wait_answer or {})
            return ok({})
        return handler

    def inspects(self):
        return [{k: p[k] for k in ("verb", "what", "args")} for p in self.params("verb")]

    def test_a_job_that_ends_after_the_wait_starts(self):
        job = {"job_id": "place-1", "tool": "build", "status": "applied", "created_ids": ["7"],
               "placed": [{"reference_id": "7", "prefab_name": "Pipe"}]}
        self.handler = self.make_handler({"place-1": {"job_id": "place-1", "status": "waiting"}},
                                         on_wait=lambda: self.hub.write(ended(job)))
        result = sg.until_job({"job_id": "place-1", "tool": "build", "position": 0}, timeout=30)
        self.assertEqual({"job_ids": ["place-1"], "created_ids": ["7"], "removed": [], "changed": [],
                          "placed": [{"reference_id": "7", "prefab_name": "Pipe"}]}, result)
        self.assertEqual([{"topic": "jobs"}], self.params("subscribe"))
        self.assertEqual([{"subscription": "s1"}], self.params("unsubscribe"))

    def test_a_job_that_ended_before_the_wait_is_read_by_inspect_on_both_faces(self):
        final = {"job_id": "run-2", "tool": "build", "status": "applied",
                 "log": {"created_ids": ["9"], "removed": ["3"], "changed": [], "placed": [{"reference_id": "9"}]}}
        for rows in (True, False):
            with self.subTest(program_face=rows):
                self.hub.calls.clear()
                self.handler = self.make_handler({"run-2": final}, rows=rows)
                result = sg.until_job("run-2", timeout=30)
                self.assertEqual({"job_ids": ["run-2"], "created_ids": ["9"], "removed": ["3"], "changed": [],
                                  "placed": [{"reference_id": "9"}]}, result)
                self.assertEqual([], self.params("wait"))
                self.assertEqual([{"verb": "inspect", "what": "job", "args": {"id": "run-2", "max_wait": 0}}],
                                 self.inspects())

    def test_a_running_job_read_on_the_script_face_is_waited_for(self):
        self.handler = self.make_handler({"place-5": {"job_id": "place-5", "status": "waiting", "running": True}},
                                         rows=False, on_wait=lambda: self.hub.write(ended(
                                             {"job_id": "place-5", "status": "applied",
                                              "placed": [{"reference_id": "11", "prefab_name": "Frame"}]})))
        result = sg.until_job("place-5", timeout=30)
        self.assertEqual([{"reference_id": "11", "prefab_name": "Frame"}], result["placed"])
        self.assertEqual(1, len(self.params("wait")))

    def test_a_final_reply_given_is_used_as_it_is(self):
        # A script face's build call waits for its job (up to 30 s) and answers the final view: nothing more to read.
        given = {"job_id": "place-6", "tool": "build", "status": "applied",
                 "result": {"placed": [{"index": 0, "reference_id": "12", "prefab_name": "Wall"}],
                            "removed": [{"index": 0, "reference_id": "4", "prefab_name": "Frame"}]}}
        self.handler = self.make_handler({})
        result = sg.until_job(given, timeout=30)
        self.assertEqual({"job_ids": ["place-6"], "created_ids": [], "removed": ["4"], "changed": [],
                          "placed": [{"reference_id": "12", "prefab_name": "Wall"}]}, result)
        self.assertEqual([], self.inspects())
        self.assertEqual([], self.params("wait"))

    def test_a_program_faces_rows_reply_names_its_jobs(self):
        given = {"results": [{"index": 0, "ok": True, "job_id": "place-7", "status": "applied", "created_ids": ["1"]},
                             {"index": 1, "ok": True, "job_id": "place-8", "status": "applied", "created_ids": ["2"]}]}
        self.handler = self.make_handler({})
        result = sg.until_job(given, timeout=30)
        self.assertEqual((["place-7", "place-8"], ["1", "2"]), (result["job_ids"], result["created_ids"]))

    def test_two_jobs_are_merged(self):
        a = {"job_id": "place-1", "status": "applied", "created_ids": ["1"], "placed": [{"reference_id": "1"}]}
        b = {"job_id": "remove-2", "status": "applied", "removed": ["5"],
             "changed": [{"old_reference_id": "6", "new_reference_id": "8"}]}
        self.handler = self.make_handler({"place-1": a, "remove-2": {"job_id": "remove-2", "status": "queued"}},
                                         on_wait=lambda: self.hub.write(ended(b)))
        result = sg.until_job(["place-1", "remove-2"], timeout=30)
        self.assertEqual({"job_ids": ["place-1", "remove-2"], "created_ids": ["1"], "removed": ["5"],
                          "changed": [{"old_reference_id": "6", "new_reference_id": "8"}],
                          "placed": [{"reference_id": "1"}]}, result)

    def test_a_swap_jobs_event_carries_its_swapped_as_changed_and_created(self):
        # JobEndFacts reads a swap or upgrade job's top-level swapped and removed: the event alone gives them, and
        # the job is not read again once it has ended.
        inspected = {"swap-3": {"job_id": "swap-3", "status": "waiting"}}
        event = {"job_id": "swap-3", "tool": "swap", "status": "applied", "created_ids": ["21"],
                 "changed": [{"old_reference_id": "20", "new_reference_id": "21", "target_prefab_name": "WallIron"}]}
        self.handler = self.make_handler(inspected, on_wait=lambda: self.hub.write(ended(event)))
        result = sg.until_job("swap-3", timeout=30)
        self.assertEqual({"job_ids": ["swap-3"], "created_ids": ["21"], "removed": [],
                          "changed": [{"old_reference_id": "20", "new_reference_id": "21",
                                       "target_prefab_name": "WallIron"}], "placed": []}, result)

    def test_an_upgrade_jobs_final_reply_gives_swapped_and_removed(self):
        final = {"job_id": "upgrade-4", "tool": "upgrade", "status": "applied", "removed": ["30", "31"],
                 "swapped": [{"old_reference_id": "32", "new_reference_id": "33", "target_prefab_name": "PipeIns"}]}
        self.handler = self.make_handler({"upgrade-4": final}, rows=False)
        result = sg.until_job("upgrade-4", timeout=30)
        self.assertEqual((["33"], ["30", "31"]), (result["created_ids"], result["removed"]))
        self.assertEqual([{"old_reference_id": "32", "new_reference_id": "33", "target_prefab_name": "PipeIns"}],
                         result["changed"])

    def test_a_job_the_run_does_not_hear_is_read_again(self):
        # A run hears only its own jobs' events: a job id from elsewhere ends unheard and a read finds it.
        inspected = {"place-9": {"job_id": "place-9", "status": "waiting"}}

        def ends():
            inspected["place-9"] = {"job_id": "place-9", "status": "applied", "created_ids": ["40"]}
        self.handler = self.make_handler(inspected, on_wait=ends)
        with mock.patch.object(_jobs_wait, "SUBSCRIBED_READ_S", 0):
            result = sg.until_job("place-9", timeout=30)
        self.assertEqual(["40"], result["created_ids"])

    def test_an_unknown_job_raises_the_rows_error_on_the_program_face(self):
        def handler(method, params):
            if method == "verb":
                return ok({"results": [{"index": 0, "ok": False, "reference_id": "place-0",
                                        "error": {"code": "job_not_found", "message": "no job place-0"}}]})
            return ok({"subscription": "s1"} if method == "subscribe" else {})
        self.handler = handler
        with self.assertRaises(sg.Error) as caught:
            sg.until_job("place-0", timeout=30)
        self.assertEqual("job_not_found", caught.exception.code)

    def test_a_failed_job_raises_job_failed_naming_it(self):
        bad = {"job_id": "swap-3", "status": "stopped", "error": {"code": "breach", "message": "would breach"}}
        self.handler = self.make_handler({"swap-3": {"job_id": "swap-3", "status": "waiting"}},
                                         on_wait=lambda: self.hub.write(ended(bad)))
        with self.assertRaises(sg.Error) as caught:
            sg.until_job("swap-3", timeout=30)
        self.assertEqual("job_failed", caught.exception.code)
        self.assertIn("swap-3", caught.exception.message)
        self.assertIn("would breach", caught.exception.message)

    def test_a_structure_jobs_result_error_is_a_failure(self):
        final = {"job_id": "place-2", "status": "applied", "result": {"error": {"code": "blocked", "message": "x"}}}
        self.handler = self.make_handler({"place-2": final}, rows=False)
        with self.assertRaises(sg.Error) as caught:
            sg.until_job("place-2", timeout=30)
        self.assertEqual("job_failed", caught.exception.code)

    def test_timeout_raises_timeout_with_maybe_ran(self):
        self.handler = self.make_handler({"place-4": {"job_id": "place-4", "status": "waiting"}})
        started = time.monotonic()
        with self.assertRaises(sg.Error) as caught:
            sg.until_job("place-4", timeout=0.3)
        self.assertEqual("timeout", caught.exception.code)
        self.assertTrue(caught.exception.maybe_ran)
        self.assertLess(time.monotonic() - started, 5)
        self.assertEqual([{"subscription": "s1"}], self.params("unsubscribe"))

    def test_a_report_run_returns_the_planned_reply_without_waiting(self):
        planned = {"report_only": True, "targets": [["[1,2,3]", "Pipe", "- -> Pipe"]], "placed": 1}
        result = sg.until_job(planned, timeout=30)
        self.assertEqual(dict(planned, report_only=True), result)
        self.assertEqual([], self.hub.calls)

    def test_an_instant_wait_ends_the_wait_as_a_report(self):
        self.handler = self.make_handler({"place-4": {"job_id": "place-4", "status": "waiting"}},
                                         wait_answer={"instant": True})
        result = sg.until_job("place-4", timeout=30)
        self.assertEqual({"job_ids": ["place-4"], "created_ids": [], "removed": [], "changed": [], "placed": [],
                          "report_only": True}, result)
        self.assertEqual(1, len(self.params("wait")))

    def test_empty_ids_and_a_reply_without_a_job_are_invalid_arguments(self):
        for given in ([], {"results": [{"index": 0, "ok": True}]}, {"reference_id": "5"}):
            with self.subTest(given=given), self.assertRaises(sg.Error) as caught:
                sg.until_job(given, timeout=1)
            self.assertEqual("invalid_argument", caught.exception.code)
