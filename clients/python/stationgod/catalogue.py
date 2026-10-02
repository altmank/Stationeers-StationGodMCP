"""The parts of the method catalogue a client needs, and nothing more.

The mod owns every rule about arguments, shaping and permissions. The library reads the catalogue for exactly four
things (clients.md, What every library does the same way):

- whether a call that may have reached the game is safe to send again: its effective class is read (the method's
  class, or the first x-class-when rule that matches the arguments) and it has no x-effects;
- how much longer than its deadline a call may take (x-duration);
- how to walk pages (x-paging);
- the generated checks run before a call is sent: argument names the method does not declare, and required ones left
  out. Types, ranges and the rest are the mod's to check.

extract() turns a catalogue (the dict catalogue.json holds, or the one the mod's `catalogue` method returns) into the
table this module works from. generate_catalogue.py writes the table of the built-in catalogue into _methods.py; when
the mod's catalogue hash differs, the client fetches the mod's and extracts it the same way.
"""
# Methods of the protocol itself (protocol.md, Protocol methods), used when a catalogue does not list them yet.
PROTOCOL_METHODS = {
    "catalogue": {"class": "read", "class_when": [], "effects": [], "paging": None, "duration": None,
                  "params": [], "required": [], "shaping": "none", "protocol": True},
    "subscribe": {"class": "read", "class_when": [], "effects": [], "paging": None, "duration": None,
                  "params": ["topic", "items", "include", "gateway_id", "interval_s"], "required": [],
                  "shaping": "none", "protocol": True},
    "unsubscribe": {"class": "read", "class_when": [], "effects": [], "paging": None, "duration": None,
                    "params": ["subscription"], "required": ["subscription"], "shaping": "none", "protocol": True},
}

# Subscriptions are kept by the library's own bookkeeping, which subscribes again only into the same world; the
# generic resend rule never sends these.
NEVER_RESENT = frozenset({"subscribe", "unsubscribe"})


def extract(catalogue):
    """The client's table from a catalogue dict: {method name: entry}."""
    table = {}
    for protocol, entries in ((True, catalogue.get("protocol_methods") or []), (False, catalogue.get("methods") or [])):
        for method in entries:
            params = method.get("params") or {}
            table[method["name"]] = {
                "class": method.get("class", "write"),
                "class_when": [rule for rule in method.get("x-class-when") or [] if "class" in rule],
                "effects": list(method.get("x-effects") or []),
                "paging": method.get("x-paging"),
                "duration": method.get("x-duration"),
                "params": list((params.get("properties") or {}).keys()),
                "required": list(params.get("required") or []),
                "shaping": method.get("x-shaping", "lists"),
                "protocol": protocol,
            }
    for name, entry in PROTOCOL_METHODS.items():
        table.setdefault(name, dict(entry))
    return table


class Catalogue:
    """A method table with the questions the client asks of it."""

    def __init__(self, table, hash=None, source="built-in"):
        self.table = table
        self.hash = hash
        self.source = source

    def __contains__(self, method):
        return method in self.table

    def effective_class(self, method, params):
        """read, write or cheat at these arguments; None for a method the catalogue does not have."""
        entry = self.table.get(method)
        if entry is None:
            return None
        for rule in entry["class_when"]:
            if _rule_matches(rule, params):
                return rule["class"]
        return entry["class"]

    def resend_safe(self, method, params):
        """True when a call that may have reached the game can be sent again: read at these arguments, no x-effects."""
        entry = self.table.get(method)
        if entry is None or method in NEVER_RESENT or entry["effects"]:
            return False
        return self.effective_class(method, params) == "read"

    def duration_s(self, method, params):
        """The seconds x-duration adds to the call's deadline: the argument given, else the method's maximum."""
        entry = self.table.get(method)
        duration = entry and entry["duration"]
        if not duration:
            return 0.0
        given = params.get(duration["param"])
        if isinstance(given, (int, float)) and not isinstance(given, bool) and given > 0:
            return min(float(given), float(duration["max_s"]))
        return float(duration["max_s"])

    def paging(self, method):
        entry = self.table.get(method)
        return entry and entry["paging"]

    def check(self, method, params):
        """The generated checks: [{path, problem}] for names the method does not declare and required names left out.
        A method the catalogue does not have is not checked (the mod answers method_not_found)."""
        entry = self.table.get(method)
        if entry is None:
            return []
        problems = []
        declared = entry["params"]
        for name, value in params.items():
            if value is None or name in declared:
                continue
            nearest = _nearest(name, declared)
            problems.append({"path": name, "problem": f"Unknown argument '{name}'; did you mean '{nearest}'?"
                             if nearest else f"Unknown argument '{name}'."})
        for name in entry["required"]:
            if params.get(name) is None:
                problems.append({"path": name, "problem": f"Argument '{name}' is required."})
        return problems


def _rule_matches(rule, params):
    if not _when_matches(rule["when"], params):
        return False
    alternatives = rule.get("any_of")
    return alternatives is None or any(_when_matches(when, params) for when in alternatives)


def _when_matches(when, params):
    for name, matcher in when.items():
        value = params.get(name)  # JSON null is an omitted argument
        if "present" in matcher:
            ok = value is not None
        elif "absent" in matcher:
            ok = value is None
        elif "equals" in matcher:
            ok = value is not None and _same(matcher["equals"], value)
        elif "in" in matcher:
            ok = value is not None and any(_same(expected, value) for expected in matcher["in"])
        else:
            ok = False
        if not ok:
            return False
    return True


def _is_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _same(expected, value):
    """The catalogue's equality: words trimmed and compared ignoring case, numbers by value, the rest as JSON."""
    if isinstance(expected, str) and isinstance(value, str):
        return expected.strip().casefold() == value.strip().casefold()
    if _is_number(expected) and _is_number(value):
        return float(expected) == float(value)
    if isinstance(expected, bool) or isinstance(value, bool):
        return expected is value
    return expected == value


def _nearest(given, known):
    """The declared name the caller probably meant, as the mod suggests it (src/StationGodMCP.Mod/Pure/NearestName.cs):
    a slip of a few letters, else the one name that is the given one plus a word."""
    lower = given.strip().lower()
    slip, best = None, None
    for name in known:
        distance = _distance(lower, name.lower())
        if distance <= max(2, len(name) // 4) and (best is None or distance < best):
            slip, best = name, distance
    if slip is not None or not lower:
        return slip
    extended = None
    for name in known:
        if _one_word_more(lower, name.lower()):
            if extended is not None:
                return None
            extended = name
    return extended


def _one_word_more(given, name):
    given_words, words = given.split("_"), name.split("_")
    if len(words) != len(given_words) + 1:
        return False
    return words[:-1] == given_words or words[1:] == given_words


def _distance(first, second):
    previous = list(range(len(second) + 1))
    for row in range(1, len(first) + 1):
        current = [row] + [0] * len(second)
        for column in range(1, len(second) + 1):
            substitution = previous[column - 1] + (first[row - 1] != second[column - 1])
            current[column] = min(substitution, min(previous[column], current[column - 1]) + 1)
        previous = current
    return previous[len(second)]
