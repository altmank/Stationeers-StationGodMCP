def build(what: Literal['blueprint'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    blueprint: str,  # A blueprint in BlueprintMod's folder (.blueprint optional) or an absolute path.
    at: list,  # World position of the blueprint's anchor.
    turn: Literal[0, 90, 180, 270] = 0,  # Degrees added to the copy angle.
) -> dict:
    """Paste a BlueprintMod blueprint at an anchor and turn, skipping placement checks.
    Progress: sg.inspect('job', id='paste'); take it back: sg.undo(id='paste').
    Reports in a report run; acts in a confirmed run.
    Cheat: ask the user first.
    Every build reply has placed: list[str] at the top: the new ids ([] in any report: a report run builds nothing, and writes on what it would build are not shown).
    Reply, one of:
    (BlueprintPasteStatus)
      known: bool
      active: bool
      complete: bool | None
      cancelled: bool | None
      created: int | None
      failed: int | None
      skipped: int | None
      pasted: int | None
      standing: int | None
      fingerprint: str | None
      file: str | None
      entries: int | None
      other_active: bool
    (BlueprintUndo)
      message: str
    (BlueprintPasteReport)
      file: str
      entries: int
      at: list[float]
      turn: int
      copy_y_angle: float
      expected_duration_s: float
      notes: list[str]
      problems: list[{code: str, message: str, index: int | None, reference_id: str | None}]
    (BlueprintPasteStarted)
      job_id: str
      file: str
      entries: int
      anchor: list[float]
      rotation: int
      copy_y_angle: float
      expected_duration_s: float
    Example: sg.build('blueprint', blueprint='airlock', at=[700, 202, 660])
    """
