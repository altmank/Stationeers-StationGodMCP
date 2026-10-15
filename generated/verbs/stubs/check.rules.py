def check(what: Literal['rules'], *,
    fields: list[str] = None,  # Keys to keep in each entry (dotted paths go deeper).
    action: Literal['list', 'validate', 'fields', 'functions', 'test', 'explain'] = 'list',  # list the rules, validate a rule file, list fields or functions, test examples, or explain a finding.
    rule: str = None,  # One rule (list, test, explain).
    full: bool = False,  # list: every rule's file and expressions.
    rules_text: str = None,  # validate: a lint-rules.json's content.
    id: str | list[str] = None,  # explain: a thing, network or room id.
    port: int = None,  # explain: the port index.
    other: str = None,  # explain: the other thing of a pair rule.
    at: list = None,  # explain: a cell.
) -> dict:  # main list: r['rules']: the page answered; .walk() goes through every entry, .all() pages in the rest
    """The lint rule set: list, validate a file, fields, functions, run examples, explain a finding.
    Reply:
      action: str
      rule_source: {mod_file: str | None, save_file: str | None, rules: int, from_save: list[str], disabled: list[str], errors: list[{file: str,
          rule_id: str | None, field: str | None, line: int, column: int, position: int | None, message: str}]}
      rules: list[{id: str, level: str, level_when: str | None, on: list[str], source: str, file: str | None, description: str | None, select: str |
          None, let: dict[str, str] | None, assert: str | None, message: str | None, other: str | None, examples: int}] | None
      types: list[{name: str, doc: str, fields: list[{name: str, type: str, doc: str}]}] | None
      sets: dict[str, str] | None
      functions: list[{name: str, signature: str, doc: str, form: bool, cached: bool}] | None
      tests: list[{id: str, passed: bool, examples: list[{expected: str, index: int, actual: str, ok: bool, message: str | None, error: str | None}]}]
          | None
      all_passed: bool | None
      explained: list[{rule: str, subject: str, outcome: str, level: str | None, message: str | None, error: str | None, values: list[{expression:
          str, values: list[str]}]}] | None
      valid: bool | None
    Example: sg.check('rules', action='explain', rule='door_keepout', id='1199811')
    In a script this always answers {results: [{index, ok, reference_id, payload | error}], count, success_count, error_count}, payload the reply above, given one id or a list.
    """
