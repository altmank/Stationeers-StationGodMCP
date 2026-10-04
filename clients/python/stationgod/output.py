"""output_file: a reply written to a file on the caller's machine, answered by a short pointer (clients.md, Output
files). The same rules as the sidecar's OutputFolder and OutputFileName (src/StationGodMCP.Server/OutputFolder.cs,
ReplyShaping.cs), checked against the shared fixtures in clients/fixtures/output_file."""
import datetime
import json
import os
import re
import secrets
from pathlib import Path

from .errors import InvalidArgument

ENVIRONMENT_VARIABLE = "STATIONGODMCP_OUTPUT_DIR"
MAXIMUM_FILES = 200
MAXIMUM_AGE_S = 7 * 24 * 3600
SUMMARY_VALUE_CHARACTERS = 300
_NAME = re.compile(r"[A-Za-z0-9._-]{1,80}")


def target(value):
    """The file name output_file asks for: None for absent or false, "" for true (a name made per call), else the
    checked name with .json. A bad value raises invalid_argument before the call is sent."""
    if value is None or value is False:
        return None
    if value is True:
        return ""
    if isinstance(value, str):
        name = value.strip()
        if _NAME.fullmatch(name) and not name.startswith(".") and ".." not in name:
            return name if name.lower().endswith(".json") else name + ".json"
        raise InvalidArgument(
            "invalid_argument",
            "Argument 'output_file' must be true or a plain file name (letters, digits, '-', '_' and '.', at most 80 "
            f"characters, not starting with '.', no folders); '{value}' is not.",
            {"problems": [{"path": "output_file", "problem": "not a plain file name"}]})
    raise InvalidArgument("invalid_argument", "Argument 'output_file' must be true, false or a file name.",
                          {"problems": [{"path": "output_file", "problem": "must be true, false or a file name"}]})


def folder(option=None):
    """The caller's option, else STATIONGODMCP_OUTPUT_DIR, else %LOCALAPPDATA%\\StationGodMCP\\output."""
    for given in (option, os.environ.get(ENVIRONMENT_VARIABLE)):
        if given is not None and str(given).strip():
            return Path(os.path.abspath(str(given).strip()))
    base = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), "AppData", "Local")
    return Path(os.path.abspath(os.path.join(base, "StationGodMCP", "output")))


def _automatic_name(method):
    now = datetime.datetime.now()
    return f"{method}-{now:%Y%m%d-%H%M%S}-{now.microsecond // 1000:03d}-{secrets.token_hex(2)}.json"


def write(method, name, result, directory):
    """Writes result (already shaped) and returns the pointer. A write that fails answers result itself with
    output_file_error added: the call has run, so its reply must not be lost."""
    directory = Path(directory)
    path = directory / (name or _automatic_name(method))
    try:
        directory.mkdir(parents=True, exist_ok=True)
        content = json.dumps(result, indent=2, ensure_ascii=False).encode("utf-8")
        temporary = path.with_name(f"{path.name}.{secrets.token_hex(4)}.tmp")
        temporary.write_bytes(content)
        os.replace(temporary, path)
        _prune(directory, path)
        return pointer(method, str(path), len(content), result)
    except OSError as error:
        inline = dict(result) if isinstance(result, dict) else {"reply": result}
        inline["output_file_error"] = f"Could not write {path}: {error} The reply is given here instead."
        return inline


def pointer(method, path, size, result):
    """{output_file, bytes, tool, counts, summary, in_file_only, truncated}: every top-level list's length, every other
    top-level value whose JSON text is at most 300 characters, the names of the rest (left out when none), and the
    reply's truncated notice whole, whatever its size."""
    counts, summary, in_file_only = {}, {}, []
    truncated = None
    if isinstance(result, dict):
        for key, value in result.items():
            if key == "truncated":
                truncated = value
            elif isinstance(value, list):
                counts[key] = len(value)
            elif len(json.dumps(value, ensure_ascii=False, separators=(",", ":"))) <= SUMMARY_VALUE_CHARACTERS:
                summary[key] = value
            else:
                in_file_only.append(key)
    answer = {"output_file": path, "bytes": size, "tool": method, "counts": counts, "summary": summary}
    if in_file_only:
        answer["in_file_only"] = in_file_only
    if truncated is not None:
        answer["truncated"] = truncated
    return answer


def _prune(directory, kept):
    """Deletes *.json files older than 7 days, then all but the newest 200; never the file just written. Another
    client may hold or prune the same folder, so a file that cannot be deleted is skipped."""
    cutoff = datetime.datetime.now().timestamp() - MAXIMUM_AGE_S
    files = []
    with os.scandir(directory) as entries:
        for entry in entries:
            if entry.is_file() and entry.name.lower().endswith(".json"):
                try:
                    files.append((entry.stat().st_mtime, entry.path))
                except OSError:
                    pass
    files.sort(reverse=True)
    kept = os.path.normcase(os.path.abspath(kept))
    for index, (modified, path) in enumerate(files):
        if (index >= MAXIMUM_FILES or modified < cutoff) and os.path.normcase(os.path.abspath(path)) != kept:
            try:
                os.remove(path)
            except OSError:
                pass
