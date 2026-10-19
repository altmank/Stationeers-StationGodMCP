"""sg.values and sg.value: logic values of many devices in as few `read logic` calls as the limits allow."""
from collections.abc import Mapping

from . import _trace
from ._errors import Error
from ._verbs import read

# catalogue/verbs/read.json:18 (reads maxItems 256) and :30 ("reads whose every entry is a pair, answer one row per
# (id, logic) pair ... up to 256"; "Any entry with a logic list ... one row per item ... up to 128 items and 1,024
# values").
MAX_PAIRS = 256
MAX_ITEMS = 128
MAX_VALUES = 1024


class Values(dict):
    """{id: {LogicName: value}}, keyed by the id as replies give it (a str); an int id finds the same entry."""

    def __missing__(self, key):
        if isinstance(key, int) and not isinstance(key, bool) and str(key) in self:
            return self[str(key)]
        raise KeyError(key)

    def __contains__(self, key):
        return dict.__contains__(self, key) or (isinstance(key, int) and dict.__contains__(self, str(key)))


def _id_of(item):
    if isinstance(item, Mapping):
        item = item["reference_id"] if "reference_id" in item else item["id"]
    return str(item).strip()


def _ids(ids):
    if isinstance(ids, (Mapping, str, int)):
        ids = [ids]
    return [_id_of(item) for item in ids]


def _code(error):
    return error.get("code") if isinstance(error, Mapping) else error


def _rows(reply):
    """read logic's rows. reads always answers rows, on every face (read.json:30: a reads list is the pairs or the
    items form, never the one-id shape); a reply without results is taken as the one-pair shape {reference_id,
    logic_type, value}, row 0."""
    if isinstance(reply, Mapping) and isinstance(reply.get("results"), list):
        return list(reply["results"])
    return [dict(reply, index=0, ok=True)] if isinstance(reply, Mapping) and "value" in reply else []


def values(ids, *logic):
    """{id: {LogicName: value}} for every id and logic type; raises one sg.Error("read_failed") naming every failed
    (id, logic, error code) after reading every chunk."""
    if not logic:
        raise Error("invalid_argument", "sg.values needs at least one logic type", "sg.values(ids, 'Pressure')")
    ids = _ids(ids)
    out = Values((i, {}) for i in ids)
    failed = []
    if len(logic) == 1:
        name = logic[0]
        for start in range(0, len(ids), MAX_PAIRS):
            chunk = ids[start:start + MAX_PAIRS]
            reply = read("logic", reads=[{"id": i, "logic": name} for i in chunk])
            # Pair rows, index counting the pairs (read.json:30): {index, ok, reference_id, logic_type, value} or
            # {index, ok, reference_id, error}.
            for row in _rows(reply):
                i = chunk[row["index"]]
                if row.get("ok"):
                    out[i][name] = row["value"]
                else:
                    failed.append((i, name, _code(row.get("error"))))
    else:
        per_call = max(1, min(MAX_ITEMS, MAX_VALUES // len(logic)))
        for start in range(0, len(ids), per_call):
            chunk = ids[start:start + per_call]
            reply = read("logic", reads=[{"id": i, "logic": list(logic)} for i in chunk])
            # Item rows (read.json:30): {index, ok, reference_id, logic {Type: value}, logic_errors, ...} or
            # {index, ok, reference_id, error}; logic: dict[str, float], logic_errors: dict[str, Error] (read.json:63).
            for row in _rows(reply):
                i = chunk[row["index"]]
                if not row.get("ok"):
                    failed.extend((i, name, _code(row.get("error"))) for name in logic)
                    continue
                out[i].update(row.get("logic") or {})
                for name, error in (row.get("logic_errors") or {}).items():
                    failed.append((i, name, _code(error)))
    if failed:
        error = Error("read_failed", "failed reads (id, logic, code): " +
                      ", ".join(f"({i}, {name!r}, {code})" for i, name, code in failed),
                      data={"failed": [{"id": i, "logic": name, "code": code} for i, name, code in failed]})
        _trace.annotate(error)
        raise error
    return out


def value(id, logic):
    """One device's logic value as a float."""
    return values([id], logic)[_id_of(id)][logic]
