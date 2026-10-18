def inspect(what: Literal['job'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str] = None,  # A job id (place-12, run-4, remove-3...), a script run (script-14), a gas transfer id, or "paste" (the last blueprint paste). Left out: the list above.
    max_wait: float = 30,  # Seconds this call waits for the job to finish (default 30); 0 answers at once. The job goes on.
    verbose: bool = False,  # The whole job and preflight.
) -> dict:
    """One build, removal, swap or gas move by its id (place-41), or a script run (script-14): running, queued or finished. With no id: runs with notices pending (stopped or killed with something left, writes not done, or done and not yet seen).
    status: queued or waiting (not started yet), then applied, applied_with_differences, applied_unchecked, stopped or refused (jobs); queued, then applied or failed (gas moves). Only queued and waiting are not final.
    A script run answers run_script's text block: script-N and its state (done, REPORT, stopped, killed, or running with the line and what it waits on), the returned value, and the footer so far, with its cost line.
    On a live confirmed run it answers within 30 s, whatever max_wait says.
    Reply, one of:
    (a build or removal job)
      BuildJob
    (a piece run)
      RunJob
    (a swap job)
      SwapJob
    (an upgrade job)
      UpgradeJob
    (queued)
      JobQueued
    (a script run)
      text: str
    Job views, one per help call (help {topic: "types", subtopic: "<name>"} or sg.help('types', '<name>')): BuildJob, JobQueued, RunJob, SwapJob,
        UpgradeJob
    Example: sg.inspect('job', id='place-41')
    One id answers the reply above; an id list answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above.
    """
