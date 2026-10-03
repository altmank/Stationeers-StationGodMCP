"""Check the mod's shaping against a live game: the same call with and without `shape`, compared.

Sends calls on one connection to the pipe named on the command line: once unshaped, once with `shape.fields`. Passes
when the shaped reply carries "shaped": true and equals the unshaped reply projected here by the same rules (key order
included), and, with --max-ratio, when it is at most that fraction of the unshaped reply's bytes. Read only: it calls
only the method named (plus find_things for --ids-from-find) and writes nothing anywhere. Pause the world first, so
both reads see the same state.

    py -3.12 tools/shape_check.py --pipe StationGodMCP-Test --method thing_health \
        --ids-from-find kind=structure,limit=84 --fields reference_id,damage_ratio,is_broken,condition --max-ratio 0.25
    py -3.12 tools/shape_check.py --pipe StationGodMCP-Test --method list_devices \
        --fields reference_id,prefab_name,display_name
    py -3.12 tools/shape_check.py --pipe StationGodMCP-Test --method find_things \
        --params '{"kind": "structure", "limit": 50}' --fields things.position.x
    py -3.12 tools/shape_check.py --self-test
"""
import argparse
import itertools
import json
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "clients", "python"))
from stationgod.connection import PipeTarget, encode, open_connection  # noqa: E402

NAME = re.compile(r"^[A-Za-z0-9_]+$")


class RawCalls:
    """One protocol-2 connection on the named pipe (hello and welcome through the Python library in clients/python),
    then each call line written and its reply line read here, as text, one call at a time."""

    def __init__(self, pipe_name, client):
        self._connection = open_connection(PipeTarget(pipe_name), client=client, connect_timeout=10.0)
        self._ids = itertools.count(1)

    def text(self, method, params, shape=None):
        call_id = str(next(self._ids))
        message = {"type": "call", "id": call_id, "method": method, "params": params}
        if shape:
            message["shape"] = shape
        self._connection.stream.write(encode(message))
        while True:
            line = self._connection.lines.next()
            if line is None:
                raise SystemExit(f"{method}: the game closed the connection without replying")
            reply = json.loads(line)
            if reply.get("type") == "reply" and reply.get("id") == call_id:
                return line.decode("utf-8")


def project(reply, selectors):
    """The reply with the selectors applied by the lenient rules (protocol.md, Shaping)."""
    selectors = list(dict.fromkeys(s.strip() for s in selectors))
    if not isinstance(reply, dict):
        return reply
    singles = [s for s in selectors if NAME.match(s)]
    paths = {}
    for s in selectors:
        parts = s.split(".")
        if len(parts) > 1 and all(NAME.match(p) for p in parts):
            paths.setdefault(parts[0], []).append(parts[1:])
    matched = set()
    any_entry = False

    def tree_for(list_name):
        root = {}
        for name in singles:
            root.setdefault(name, {"whole": False, "children": {}, "ends": []})
            root[name]["whole"] = True
            root[name]["ends"].append(name)
        for rest in paths.get(list_name, []):
            level = root
            for index, key in enumerate(rest):
                node = level.setdefault(key, {"whole": False, "children": {}, "ends": []})
                if index == len(rest) - 1:
                    node["whole"] = True
                    node["ends"].append(list_name + "." + ".".join(rest))
                level = node["children"]
        return root

    def mark(value, children):
        if isinstance(value, dict):
            for key, child in value.items():
                if key in children:
                    matched.update(children[key]["ends"])
                    mark(child, children[key]["children"])
        elif isinstance(value, list):
            for item in value:
                if isinstance(item, dict):
                    mark(item, children)

    def entry(obj, tree):
        out = {}
        for key, value in obj.items():
            node = tree.get(key)
            if node is None:
                continue
            matched.update(node["ends"])
            if node["whole"]:
                mark(value, node["children"])
                out[key] = value
            elif isinstance(value, dict):
                out[key] = entry(value, node["children"])
            elif isinstance(value, list):
                out[key] = [entry(item, node["children"]) if isinstance(item, dict) else item for item in value]
            else:
                out[key] = value
        return out

    shaped = {}
    for key, value in reply.items():
        if isinstance(value, list):
            tree = tree_for(key)
            items = []
            for item in value:
                if isinstance(item, dict):
                    any_entry = True
                    items.append(entry(item, tree))
                else:
                    items.append(item)
            shaped[key] = items
        else:
            shaped[key] = value
    unmatched = [s for s in selectors if s not in matched]
    if any_entry and unmatched:
        shaped["fields_unmatched"] = unmatched
    return shaped


def ordered_equal(a, b):
    if isinstance(a, dict) and isinstance(b, dict):
        return list(a) == list(b) and all(ordered_equal(a[k], b[k]) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(ordered_equal(x, y) for x, y in zip(a, b))
    return a == b


def run(pipe, method, params, fields, max_ratio):
    plain_text = pipe.text(method, params)
    shaped_text = pipe.text(method, params, {"fields": fields})
    plain, shaped = json.loads(plain_text), json.loads(shaped_text)
    if not plain.get("ok") or not shaped.get("ok"):
        raise SystemExit(f"error reply: {plain.get('error') or shaped.get('error')}")
    problems = []
    if shaped.get("shaped") is not True:
        problems.append("the shaped reply is not marked shaped: true")
    if plain.get("shaped") is not False:
        problems.append("the unshaped reply is not marked shaped: false")
    expected = project(plain["result"], fields)
    if not ordered_equal(expected, shaped["result"]):
        problems.append("the shaped result differs from the unshaped result projected here")
    ratio = len(shaped_text.encode()) / len(plain_text.encode())
    if max_ratio is not None and ratio > max_ratio:
        problems.append(f"shaped is {ratio:.3f} of the unshaped bytes, more than {max_ratio}")
    print(json.dumps({"method": method, "fields": fields, "unshaped_bytes": len(plain_text.encode()),
                      "shaped_bytes": len(shaped_text.encode()), "ratio": round(ratio, 4),
                      "fields_unmatched": shaped["result"].get("fields_unmatched"), "problems": problems}, indent=2))
    return not problems


def self_test():
    reply = {"count": 2, "things": [{"reference_id": "1", "position": {"x": 1, "y": 2}, "n": "a"},
                                    {"reference_id": "2", "position": {"x": 3, "y": 4}}],
             "local_player": {"reference_id": "9"}}
    assert project(reply, ["reference_id"]) == {"count": 2, "things": [{"reference_id": "1"}, {"reference_id": "2"}],
                                                "local_player": {"reference_id": "9"}}
    assert project(reply, ["things.position.x", "nope"])["things"][0] == {"position": {"x": 1}}
    assert project(reply, ["things.position.x", "nope"])["fields_unmatched"] == ["nope"]
    assert "fields_unmatched" not in project({"things": []}, ["a"])
    assert ordered_equal({"a": 1, "b": 2}, {"a": 1, "b": 2}) and not ordered_equal({"a": 1, "b": 2}, {"b": 2, "a": 1})
    print("self-test passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pipe")
    parser.add_argument("--method")
    parser.add_argument("--params", default="{}", help="the method's params as JSON")
    parser.add_argument("--ids-from-find", help="find_things filters k=v,... whose ids become reference_ids")
    parser.add_argument("--fields", help="comma-separated selectors")
    parser.add_argument("--max-ratio", type=float)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    if not (args.pipe and args.method and args.fields):
        parser.error("--pipe, --method and --fields are required")
    params = json.loads(args.params)
    calls = RawCalls(args.pipe, "shape-check")
    if args.ids_from_find:
        filters = {}
        for pair in args.ids_from_find.split(","):
            key, value = pair.split("=", 1)
            filters[key] = int(value) if value.isdigit() else value
        found = json.loads(calls.text("find_things", filters))
        params["reference_ids"] = [thing["reference_id"] for thing in found["result"]["things"]]
    return 0 if run(calls, args.method, params, args.fields.split(","), args.max_ratio) else 1


if __name__ == "__main__":
    sys.exit(main())
