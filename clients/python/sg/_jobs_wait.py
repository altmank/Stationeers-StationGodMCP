"""sg.until_job: wait for the jobs a write started and merge what they changed.

A job_ended event goes only to the jobs subscriptions open when the job ends: the mod keeps no backlog for a later
subscriber (Protocol/SubscriptionHub.cs JobEnded, Pure/Subscriptions/SubscriptionEngine.cs SubscribeJobs) and the hub
relays live events alone (Hub/HubSession.Relay.cs OnEvent). So until_job subscribes first, then reads each job with
inspect job: a job that ended before the subscription is seen by the read, one that ends after it by the event.

The lists are the job's facts as the mod's JobEndFacts (Pure/Layout/JobEndFacts.cs) gives them in a job_ended event:
created_ids, removed (ids), changed {old_reference_id, new_reference_id, target_prefab_name} and placed
{reference_id, prefab_name}. A final reply read with inspect is turned into the same lists: a piece run's log, a
structure job's result, and a swap or upgrade job's top-level swapped (as changed and created_ids) and removed.
"""
import time
from collections.abc import Mapping

from . import _session, _trace
from ._errors import Error
from ._helpers import _JobsSubscription, _stopped, _subscriptions_offered
from ._verbs import inspect as _inspect

POLL_S = 1.0
# With a live subscription the jobs are still read this often: a run hears only the job_ended events of jobs it
# started itself (Hub/HubSession.Relay.cs HearsJob), so a job id it was given from elsewhere is seen by a read alone.
SUBSCRIBED_READ_S = 5.0

# The final statuses, as the hub's own job wait reads them (Verbs/VerbPipeline.cs Final; inspect.json:861 lists them).
_FINAL = frozenset({"applied", "applied_with_differences", "applied_unchecked", "stopped", "refused", "failed"})
# Final statuses that are failures: stopped and refused (jobs), failed (gas moves) (catalogue/verbs/inspect.json:861).
_FAILED = frozenset({"stopped", "refused", "failed"})
_LISTS = ("created_ids", "removed", "changed", "placed")


def until_job(reply_or_ids, timeout):
    """Waits until the jobs a write reply started (or a job id, or a list of ids) end; returns {job_ids, created_ids,
    removed, changed, placed} merged across them in order. Raises sg.Error("job_failed") when a job ended with an
    error, sg.Error("timeout", maybe_ran=True) at timeout. A report reply (report_only) is returned as it is, with
    report_only: True, since a report run starts no job; in a report run a wait that the hub answers at once ends the
    wait with the jobs ended so far and report_only: True."""
    # Every report, direct or scripted, carries report_only: true (catalogue/verbs/_shared.json:188; hub.md:189-191).
    if isinstance(reply_or_ids, Mapping) and reply_or_ids.get("report_only"):
        return dict(reply_or_ids, report_only=True)
    job_ids, given = _jobs_of(reply_or_ids)
    started = time.monotonic()
    subscription = _JobsSubscription() if _subscriptions_offered() and not _stopped() else None
    if subscription is not None and not subscription.start():
        subscription = None
    try:
        finished = {job_id: job for job_id, job in given.items() if _final(job)}
        _read_unfinished(job_ids, finished)
        last_read = time.monotonic()
        while True:
            if subscription is not None:
                job = subscription.take()
                while job is not None:
                    job_id = str(job.get("job_id"))
                    if job_id in job_ids and job_id not in finished:
                        finished[job_id] = job
                    job = subscription.take()
            for job_id in job_ids:
                if job_id in finished:
                    _raise_if_failed(job_id, finished[job_id])
            if len(finished) == len(job_ids):
                return _merge(job_ids, finished)
            left = timeout - (time.monotonic() - started)
            if left <= 0:
                waiting = [job_id for job_id in job_ids if job_id not in finished]
                error = Error("timeout", f"{', '.join(waiting)} had not ended after {timeout} s; the job goes on",
                              fix=f"inspect {{what: \"job\", id: \"{waiting[0]}\"}}", maybe_ran=True)
                _trace.annotate(error)
                raise error
            answer = _session.request("wait", {"s": min(POLL_S, left), "kind": "watch",
                                               "in_loop": _session.in_loop()})
            if isinstance(answer, Mapping) and answer.get("instant"):
                return dict(_merge(job_ids, finished), report_only=True)
            live = subscription is not None and subscription.live
            if not live or time.monotonic() - last_read >= SUBSCRIBED_READ_S:
                _read_unfinished(job_ids, finished)
                last_read = time.monotonic()
    finally:
        if subscription is not None:
            subscription.end()


def _jobs_of(reply_or_ids):
    """The job ids, and the job views the reply already holds by id: one id, a list of ids, or a write reply naming
    job_id (JobQueued, BuildJob, RunJob ... all have job_id: catalogue/verbs/_shared.json:191-198), as one id's shape
    (the script face) or rows {results: [...]} (a list, or one id on the program face; hub.md *Faces*)."""
    if isinstance(reply_or_ids, (str, int)) and not isinstance(reply_or_ids, bool):
        return [str(reply_or_ids)], {}
    if isinstance(reply_or_ids, Mapping):
        rows = reply_or_ids.get("results") if isinstance(reply_or_ids.get("results"), list) else [reply_or_ids]
        jobs = [_payload(row) for row in rows]
        jobs = [job for job in jobs if job.get("job_id")]
        if not jobs:
            raise Error("invalid_argument", "the reply names no job_id: it started no job",
                        "pass the reply of a build, remove, swap or move call, or the job ids")
        ids = [str(job["job_id"]) for job in jobs]
        return list(dict.fromkeys(ids)), {str(job["job_id"]): job for job in jobs}
    ids = [str(job_id) for job_id in reply_or_ids]
    if not ids:
        raise Error("invalid_argument", "no job ids given", "pass a job id, a list of them, or a write's reply")
    return list(dict.fromkeys(ids)), {}


def _payload(row):
    """A row's job view: the row itself (inspect job rows carry the job's keys), or its payload when it has one
    (_shared.json topic targets)."""
    if not isinstance(row, Mapping):
        return {}
    return row["payload"] if isinstance(row.get("payload"), Mapping) else row


def _read_unfinished(job_ids, finished):
    for job_id in job_ids:
        if job_id not in finished:
            job = _inspect_job(job_id)
            if job is not None and _final(job):
                finished[job_id] = job


def _inspect_job(job_id):
    """inspect {what: "job", id, max_wait: 0} (catalogue/verbs/inspect.json:847-877): the job's view now, without the
    hub's own wait for it (30 s by default). One id answers the job's view on the script face and {results: [row]} on
    the program face (hub.md *Faces*); a row that failed raises its error."""
    reply = _inspect("job", id=job_id, max_wait=0)
    if isinstance(reply, Mapping) and isinstance(reply.get("results"), list):
        rows = list(reply["results"])
        if not rows:
            return None
        row = rows[0]
        if isinstance(row, Mapping) and row.get("ok") is False:
            error = row.get("error") if isinstance(row.get("error"), Mapping) else {}
            raised = Error(error.get("code") or "job_not_found", error.get("message") or f"no job {job_id}",
                           error.get("fix"), data=error.get("data"))
            _trace.annotate(raised)
            raise raised
        reply = _payload(row)
    return reply if isinstance(reply, Mapping) else None


def _final(job):
    """A final status, or (a job dropped before it started, JobDropped) no status but an error."""
    status = job.get("status")
    return status in _FINAL or (status is None and isinstance(job.get("error"), Mapping))


def _raise_if_failed(job_id, job):
    error = job.get("error")
    if not isinstance(error, Mapping) and isinstance(job.get("result"), Mapping):
        error = job["result"].get("error")
    if error or job.get("status") in _FAILED:
        detail = f"{error.get('code')}: {error.get('message')}" if isinstance(error, Mapping) else \
            f"status {job.get('status')}"
        raised = Error("job_failed", f"job {job_id} ended with an error: {detail}", data=dict(job))
        _trace.annotate(raised)
        raise raised


def _id(value):
    """An id from an id or a thing with a reference_id, as text; None when it is neither."""
    if isinstance(value, Mapping):
        value = value.get("reference_id")
    return None if value is None or isinstance(value, bool) else str(value)


def _brief(entry, ids, texts):
    """entry's id keys (as text) and text keys, each only when it is there, as JobEndFacts copies them."""
    brief = {}
    for key in ids:
        if _id(entry.get(key)) is not None:
            brief[key] = _id(entry.get(key))
    for key in texts:
        if isinstance(entry.get(key), str):
            brief[key] = entry[key]
    return brief


def _facts(job):
    """A job view's lists as a job_ended event carries them (JobEndFacts.Of), whichever view it is: the event's job
    (the lists at the top level), a piece run's final reply (log), a structure job's (result), or a swap or upgrade
    job's (top-level swapped, as changed and created_ids, and removed)."""
    log = job.get("log") if isinstance(job.get("log"), Mapping) else {}
    result = job.get("result") if isinstance(job.get("result"), Mapping) else {}
    swapped = [entry for entry in job.get("swapped") or [] if isinstance(entry, Mapping)]

    def joined(name, *holders):
        return [entry for holder in holders for entry in (holder.get(name) or [])]

    created = [_id(value) for value in joined("created_ids", job, log)]
    created += [_id(entry.get("new_reference_id")) for entry in swapped]
    changed = [_brief(change, ("old_reference_id", "new_reference_id"), ("target_prefab_name",))
               for change in joined("changed", job, log) + swapped if isinstance(change, Mapping)]
    placed = [_brief(thing, ("reference_id",), ("prefab_name",))
              for thing in joined("placed", job, log, result) if isinstance(thing, Mapping)]
    removed = [_id(value) for value in joined("removed", job, log, result)]
    return {"created_ids": list(dict.fromkeys(value for value in created if value is not None)),
            "removed": [value for value in removed if value is not None],
            "changed": changed, "placed": placed}


def _merge(job_ids, finished):
    merged = {"job_ids": list(job_ids)}
    for name in _LISTS:
        merged[name] = []
    for job_id in job_ids:
        if job_id in finished:
            lists = _facts(finished[job_id])
            for name in _LISTS:
                merged[name].extend(lists[name])
    return merged
