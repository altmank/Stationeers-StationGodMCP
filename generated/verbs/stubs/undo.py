def undo(*,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # The job, the bulk write (write-N), the script run (script-14), or "paste"; a list is undone newest first, in one report.
) -> dict:  # main list: r['piece_runs']; iterating or indexing it walks every page, in order
    """A job id (place-12, run-4, remove-3...), a bulk write (write-N), a script run (script-14: the whole run), or "paste".
    A bulk write undo lists every step with its mark: build and removal jobs [undo] (undo of each, newest first); swap jobs [cannot undo: change job]; logic writes of held settings, memory, label, paint, pins, uplink and genes [undo] (previous values written back); action and pulse logic types [cannot undo: action]; battery [undo: restores the value, not what happened since]; item slot moves [undo] unless merged; chip source, run controls, screen presses, gas moves, player moves, trades, console commands and store moves [cannot undo].
    A script run (script-14) is reverted whole, newest first, each target restored to its value before the run; jobs still in flight at a stop are included once they settle, and groups that cannot be undone are named [cannot undo]. Each target is compared with the run's last written value: where it changed since (the user, a chip, another run), the target is reported and not written unless allow: ["overwrite"]. Undo of a running run is refused: stop it first (server {what: "stop", id}). Past the memory bound undo answers undo_expired, naming what it can no longer revert.
    Reports in a report run; acts in a confirmed run.
    Reply:
      job_id: str
      tool: str
      status: str
      plan: {remove: list[Any], restore: list[Any], diverged: list[Any], notes: list[str], ready: bool}
      remove_arguments: dict | None
      place_arguments: dict | None
      removal: dict | None
      placement: dict | None
      piece_runs: list[{tool: str, arguments: dict, reply: dict}]
      resolved_networks: list | None
      gas_hold: dict | None
    Example: sg.undo(id='place-41')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
