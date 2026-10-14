def undo(*,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    id: str | list[str],  # The job, the bulk write (write-N), the script run (script-14), or "paste" on its own; a list is undone newest first, in one report.
) -> dict:  # main list: r['plans']; iterating or indexing it walks every page, in order
    """A job id (place-12, run-4, remove-3...), a bulk write (write-N), a script run (script-14: the whole run), or "paste" on its own.
    A bulk write undo lists every step with its mark: build and removal jobs [undo] (undo of each, newest first); swap jobs [cannot undo: change job]; logic writes of held settings, memory, label, paint, pins, uplink and genes [undo] (previous values written back); action and pulse logic types [cannot undo: action]; battery [undo: restores the value, not what happened since]; item slot moves [undo] unless merged; chip source, run controls, screen presses, gas moves, player moves, trades, console commands and store moves [cannot undo].
    A script run (script-14) is reverted whole, newest first, each target restored to its value before the run; jobs still in flight at a stop are included once they settle, and groups that cannot be undone are named [cannot undo]. Each target is compared with the run's last written value: where it changed since (the user, a chip, another run), the target is reported and not written unless allow: ["overwrite"]. Undo of a running run is refused: stop it first (server {what: "stop", id}). Past the memory bound undo answers undo_expired, naming what it can no longer revert.
    Reports in a report run; acts in a confirmed run.
    Reply, one of:
    (UndoPlan)
      reference_id: str
      id: str
      tool: str
      remove: {id: list[str], refund_to: Any | None, acknowledge_gas_lost: str | None, materials: str | None} | None
      build: {placements: list[{prefab: str, at: list[float], rotation: list[float], build_state: int, label: str | None}], materials: str | None,
          acknowledge_gas_lost: str | None} | None
      piece_runs: list[{what: str, pieces: list[{at: list[float], ends: list[str]}], grade: str, remove_ids: list[str], assume_removed: list[str]}]
      notes: list[str]
      jobs: list[Any]
    (Paste)
      reference_id: str
      id: str
      tool: str
      remove: None
      build: None
      piece_runs: list
      notes: list[str]
      reply: {message: str}
    (UndoBatchResult)
      results: list[{index: int, ok: bool, reference_id: str, id: str, tool: str, remove: Any | None, build: Any | None, piece_runs: list, notes:
          list[str], jobs: list[Any]} | {index: int, ok: bool, reference_id: str, error: Error}]
      count: int
      success_count: int
      error_count: int
    (UndoReport)
      id: str
      tool: str
      remove: Any | None
      build: Any | None
      piece_runs: list
      notes: list[str]
      report_only: bool
      targets: list[{id: str | None, cell: list[float] | None, name: str | None, current: Any | None, new: Any | None, undo: str, passed: list[str] |
          None, driven_by: Any | None, text: str}]
      problems: list[{index: int | None, reference_id: str | None, code: str, message: str, data: Any | None}]
    (UndoBatchReport)
      plans: list[{id: str, tool: str, remove: Any | None, build: Any | None, piece_runs: list, notes: list[str]}]
      report_only: bool
      targets: list[{id: str | None, cell: list[float] | None, name: str | None, current: Any | None, new: Any | None, undo: str, passed: list[str] |
          None, driven_by: Any | None, text: str}]
      problems: list[{index: int | None, reference_id: str | None, code: str, message: str, data: Any | None}]
    Types: Error = {code: str, message: str, data: Any | None, see: {verb: str | None, what: str | None, topic: str | None, subtopic: str | None} |
        None}
    Example: sg.undo(id='place-41')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
