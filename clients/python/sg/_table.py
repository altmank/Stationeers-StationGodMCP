"""print() inside a run: a list of row dicts prints as a table, everything else as print would (SPEC, scripts topic).

A table has one column per key, in the order keys first appear, a header line and a dashed rule. Cells are cut at
CELL_WIDTH characters so one long value cannot push the rest off the screen.
"""
import builtins
import json

CELL_WIDTH = 40


def is_rows(value):
    """A non-empty list whose entries are all dicts."""
    return isinstance(value, list) and len(value) > 0 and all(isinstance(entry, dict) for entry in value)


def cell(value):
    if value is None:
        return ""
    if isinstance(value, (dict, list)):
        text = json.dumps(value, ensure_ascii=False, separators=(",", ":"), default=str)
    else:
        text = str(value)
    text = text.replace("\n", " ")
    return text if len(text) <= CELL_WIDTH else text[:CELL_WIDTH - 3] + "..."


def table(rows):
    """The rows as table text (no final line feed)."""
    columns = []
    for row in rows:
        for key in row:
            if key not in columns:
                columns.append(key)
    cells = [[cell(row.get(column)) for column in columns] for row in rows]
    widths = [max([len(str(column))] + [len(line[index]) for line in cells]) for index, column in enumerate(columns)]
    lines = ["  ".join(str(column).ljust(width) for column, width in zip(columns, widths)).rstrip(),
             "  ".join("-" * width for width in widths)]
    lines += ["  ".join(text.ljust(width) for text, width in zip(line, widths)).rstrip() for line in cells]
    return "\n".join(lines)


def run_print(*values, sep=" ", end="\n", file=None, flush=False):
    """The print a run's code sees: lists of row dicts become tables; the rest is the built-in print."""
    shown = [table(list(value)) if is_rows(value) else value for value in values]
    builtins.print(*shown, sep=sep, end=end, file=file, flush=flush)
