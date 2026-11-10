"""Test-world library helpers that need no game: path guards, reading a save, md5s, the structure count, the layout
of a world's site, and the library entry (<world>.save, world.txt, README.md, fixtures.json).

The library root is --library, else the environment variable STATIONGOD_TEST_WORLDS; with neither the tools refuse.
Every write stays inside that root (an allow-list: nothing outside it is ever written or replaced).

A library entry is the shared library's flat layout, <library>\\<world>\\ holding:
  <world>.save         the save itself, nothing nested (a run that needs the game's <saves>\\<world>\\<world>.save
                       folder shape builds it at staging time: stage_save_folder);
  world.txt            'key value' lines other mods' tools read: md5, structures, things, world, landed, game-version;
  README.md            what the world holds and where, how it was built;
  fixtures.json        the role of each fixture and its reference id, for tests that look things up by id.
"""
import hashlib
import json
import math
import os
import re
import zipfile

LIBRARY_ENV = "STATIONGOD_TEST_WORLDS"

# The game's caps (decompile: OcclusionManager.cs:97,101, GridController.cs:57, Device.cs:24), as make_large_save.py
# records them; a save past one fails to load.
CAP_THINGS = 65535
CAP_STRUCTURES = 65536
CAP_DEVICES = 8192

THING_BLOCK = re.compile(r"<ThingSaveData xsi:type=\"([^\"]+)\">(.*?)</ThingSaveData>", re.S)


def library_root(given=None, environ=None):
    """The library root: --library, else STATIONGOD_TEST_WORLDS; refuses with neither."""
    environ = os.environ if environ is None else environ
    root = given or environ.get(LIBRARY_ENV)
    if not root:
        raise SystemExit(f"no world library: pass --library <folder> or set {LIBRARY_ENV}")
    return os.path.abspath(root)


def _norm(path):
    return os.path.normcase(os.path.abspath(path)).rstrip("\\/")


def inside(path, root):
    """Whether path is root or lies under it."""
    p, r = _norm(path), _norm(root)
    try:
        return os.path.commonpath([p, r]) == r
    except ValueError:          # another drive
        return False


def path_refusal(path, root):
    """Why a test-world tool may not write this path, or None: every write stays inside the library root."""
    if inside(path, root) and _norm(path) != _norm(root):
        return None
    return f"{path} is not inside the world library {root}"


def check_write(path, root):
    why = path_refusal(path, root)
    if why:
        raise SystemExit("refusing to write: " + why)
    return path


def check_save_folder(save_folder, world):
    """A world's save as a test server wrote it: a folder named for the world holding <world>.save. A save of any
    other name (a player's base, a pristine copy) is refused by its name alone."""
    name = os.path.basename(os.path.normpath(save_folder))
    if name != world:
        raise SystemExit(f"refusing {save_folder}: a {world} save folder is named {world}")
    if not os.path.isfile(os.path.join(save_folder, world + ".save")):
        raise SystemExit(f"no {world}.save in {save_folder}")
    return save_folder


def odd(v):
    """The nearest odd whole metre: a 2 m cell's centre."""
    n = int(math.floor(v))
    return n if n % 2 else n + 1


def read_world_xml(save_path):
    with zipfile.ZipFile(save_path) as z:
        return z.read("world.xml").decode("utf-8")


def things(xml):
    """(save type, prefab, block) of every thing in a world.xml."""
    for m in THING_BLOCK.finditer(xml):
        body = m.group(2)
        prefab = re.search(r"<PrefabName>([^<]*)</PrefabName>", body)
        yield m.group(1), prefab.group(1) if prefab else None, body


def is_organ(prefab):
    """find thing never keeps an organ (ThingSearch: organs are the body's, not the world's), so a loaded world's
    thing count is the save's without them."""
    return bool(prefab) and prefab.startswith("Organ")


def counts(xml):
    """The world's size as the caps count it: things, structures (anything with a build state) and prefabs.
    findable is things less the organs: what `find thing` totals on the loaded world (the load check's expected)."""
    total = structures = organs = 0
    by_prefab = {}
    for _type, prefab, body in things(xml):
        total += 1
        organs += is_organ(prefab)
        if "<CurrentBuildState>" in body:
            structures += 1
            by_prefab[prefab] = by_prefab.get(prefab, 0) + 1
    return {"things": total, "findable": total - organs, "structures": structures, "structure_prefabs": dict(sorted(by_prefab.items()))}


def md5_of(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def md5_lines(folder):
    """'<md5>  <relative path>' for every file under folder, sorted by path."""
    rows = []
    for root, _dirs, files in os.walk(folder):
        for name in files:
            full = os.path.join(root, name)
            rows.append((os.path.relpath(full, folder).replace("\\", "/"), md5_of(full)))
    return [f"{digest}  {rel}" for rel, digest in sorted(rows)]


# ---- The site: where a world's fixtures stand ----------------------------------------------------------------------

def bay_cells(render_box, grid_m=2.0):
    """How many 2 m cells a prefab's bay needs along x and z: its render box rounded up, plus a free cell between
    bays. render_box is {min: {x, y, z}, max: {...}} or [[x, y, z], [x, y, z]], relative to the prefab's origin; a
    bay is centred on the origin, so the larger side from the origin sets the width."""
    if render_box is None:
        return 2
    if isinstance(render_box, dict):
        lo, hi = render_box.get("min"), render_box.get("max")
        lo = [lo["x"], lo["y"], lo["z"]] if isinstance(lo, dict) else lo
        hi = [hi["x"], hi["y"], hi["z"]] if isinstance(hi, dict) else hi
    else:
        lo, hi = render_box
    half = max(abs(lo[0]), abs(hi[0]), abs(lo[2]), abs(hi[2]))
    cells = 2 * math.ceil(max(half - grid_m / 2, 0) / grid_m) + 1   # odd, so the origin's cell is the centre one
    return cells + 1


class Site:
    """A floating floor of frames: rows of bays along +x, one fixture centred in each, then clear rows (+z) for the
    player and ground drops. Cell (i, k) is the 2 m cell at x0 + 2i, z0 + 2k on the floor; fixtures stand one cell
    above it."""

    def __init__(self, x0, floor_y, z0, bays, clear_rows=3):
        self.x0, self.floor_y, self.z0 = odd(x0), odd(floor_y), odd(z0)
        self.bays = list(bays)                  # [(role, cells)]
        self.depth_bays = max([c for _r, c in self.bays] or [1])
        self.width = max(sum(c for _r, c in self.bays), 4)
        self.depth = self.depth_bays + clear_rows

    def cell(self, i, k, up=1):
        return [self.x0 + 2 * i, self.floor_y + 2 * up, self.z0 + 2 * k]

    def frames(self):
        return [self.cell(i, k, up=0) for k in range(self.depth) for i in range(self.width)]

    def spots(self):
        """role -> the point at the centre of its bay, one cell above the floor."""
        out, i = {}, 0
        for role, cells in self.bays:
            out[role] = self.cell(i + (cells - 1) // 2, (cells - 1) // 2)
            i += cells
        return out

    def clear(self, i=1, k=None):
        """A point on the floor in the clear rows (standing height), for the player and loose things."""
        k = self.depth_bays + 1 if k is None else k
        x, _y, z = self.cell(i, k)
        return [x, self.floor_y + 1.2, z]

    def describe(self):
        x1, z1 = self.x0 + 2 * (self.width - 1), self.z0 + 2 * (self.depth - 1)
        return (f"a floor of {self.width} x {self.depth} frames, cell centres x {self.x0}..{x1}, z {self.z0}..{z1}, "
                f"y {self.floor_y}; fixtures stand at y {self.floor_y + 2}")


def chunks(items, n=64):
    return [items[i:i + n] for i in range(0, len(items), n)]


# ---- The library entry ---------------------------------------------------------------------------------------------

def _floats(values):
    """The numbers, or None when any does not parse (the store gate then refuses the player's position)."""
    try:
        return [float(v) for v in values]
    except ValueError:
        return None


def player_rows(xml):
    """Each player character in a world.xml: {id, position, parent, slot, owner}."""
    rows = []
    for _kind, prefab, block in things(xml):
        if prefab != "Character":
            continue
        at = re.search(r"<WorldPosition>\s*<x>([^<]+)</x>\s*<y>([^<]+)</y>\s*<z>([^<]+)</z>", block)
        parent = re.search(r"<ParentReferenceId>(\d+)", block)
        slot = re.search(r"<ParentSlotId>(\d+)", block)
        rid = re.search(r"<ReferenceId>(\d+)", block)
        owner = re.search(r"<OwnerSteamId>(\d+)", block)
        heading = re.search(r"<WorldRotation>.*?<eulerAngles>\s*<x>[^<]+</x>\s*<y>([^<]+)</y>", block, re.S)
        rows.append({"heading": round(float(heading.group(1)), 1) if heading else None, "id": rid.group(1) if rid else None, "position": _floats(at.groups()) if at else None,
                     "parent": int(parent.group(1)) if parent else 0, "slot": int(slot.group(1)) if slot else 0,
                     "owner": owner.group(1) if owner else None})
    return rows


def lander_ys(xml):
    """The y of each lander capsule in a world.xml."""
    ys = []
    for _kind, prefab, block in things(xml):
        if prefab == "LanderCapsuleSmall":
            at = re.search(r"<WorldPosition>\s*<x>[^<]+</x>\s*<y>([^<]+)</y>", block)
            if at:
                ys.append(float(at.group(1)))
    return ys


def landed_problems(xml, landed, ground_m=2.0, lander_m=0.5):
    """What stops a world.xml counting as landed (P51), against the builder's landed record; [] when it does: every
    player out of a seat with their feet at the settled ground, the lander at its settled y."""
    if not landed:
        return ["no landed record: the builder did not see this world settle"]
    problems = []
    players = player_rows(xml)
    if not players:
        problems.append("no player character in the save")
    for row in players:
        if row["parent"]:
            problems.append(f"player {row['id']} is seated (parent {row['parent']}, slot {row['slot']})")
        if not row["position"]:
            problems.append(f"player {row['id']} position unreadable: not stored")
        elif abs(row["position"][1] - landed["ground_y"]) > ground_m:
            problems.append(f"player {row['id']} is mid-air: y {row['position'][1]}, ground {landed['ground_y']}")
    ys = lander_ys(xml)
    if not ys:
        problems.append("no lander in the save")
    for y in ys:
        if abs(y - landed["lander_y"]) > lander_m:
            problems.append(f"the lander is in the air: y {y}, settled {landed['lander_y']}")
    return problems


def verify_stored(world, library):
    """What is wrong with a stored world's save against its fixtures.json; [] when it is a landed world."""
    entry = os.path.join(library, world)
    with open(os.path.join(entry, "fixtures.json"), encoding="utf-8") as f:
        stored = json.load(f)
    return landed_problems(read_world_xml(save_path(library, world)), stored.get("landed"))


def readme(world, spec_text, manifest, size, md5):
    lines = [f"# Test world: {world}", "",
             spec_text.strip(), "",
             "## What it holds and where", ""]
    for role, row in sorted(manifest.get("fixtures", {}).items()):
        where = row.get("position")
        pos = f" at {where}" if where else ""
        lines.append(f"- `{role}`: {row.get('prefab', '?')} id {row.get('id')}{pos}" +
                     (f" ({row['note']})" if row.get("note") else ""))
    if manifest.get("site"):
        lines += ["", f"Site: {manifest['site']}."]
    landed = manifest.get("landed")
    if landed:
        player = landed.get("player") or {}
        lines += ["", "## Landed (P51: loads with no landing)", "",
                  f"- player {player.get('id')} at {player.get('position')}, facing {player.get('facing')}, "
                  f"owner id {player.get('owner')}, out of the seat",
                  f"- ground y {landed.get('ground_y')} ({landed.get('ground')})",
                  f"- lander settled at y {landed.get('lander_y')}"]
    lines += ["", "## Size", "",
              f"- things: {size['things']}",
              f"- structures: {size['structures']}", "",
              "| prefab | count |", "| --- | --- |"]
    lines += [f"| {p} | {n} |" for p, n in size["structure_prefabs"].items()]
    lines += ["", "## How it was built", ""]
    lines += [f"- {k}: {v}" for k, v in manifest.get("built", {}).items()]
    lines += ["", "Rebuild after a game patch: `tools/test-worlds/README.md` in the StationGodMCP repo.", "",
              "## md5", "", "```", md5, "```", ""]
    return "\n".join(lines)


def save_path(library, world):
    """Where a library world's save is: <library>\\<world>\\<world>.save, flat."""
    return os.path.join(library, world, world + ".save")


def stage_save_folder(library, world, saves):
    """Builds the game's <saves>\\<world>\\<world>.save folder shape from a library entry (for a run that loads the
    world by its folder name). Returns the folder."""
    import shutil
    src = save_path(library, world)
    if not os.path.isfile(src):
        raise SystemExit(f"no {world}.save in {os.path.join(library, world)}")
    folder = os.path.join(saves, world)
    os.makedirs(folder, exist_ok=True)
    shutil.copyfile(src, os.path.join(folder, world + ".save"))
    return folder


def world_meta(save_file):
    """WorldName and GameVersion from the save's world_meta.xml; '' for a field it lacks."""
    try:
        with zipfile.ZipFile(save_file) as z:
            xml = z.read("world_meta.xml").decode("utf-8", "replace")
    except (KeyError, zipfile.BadZipFile):
        xml = ""
    out = {}
    for key in ("WorldName", "GameVersion"):
        m = re.search(rf"<{key}>([^<]*)</{key}>", xml)
        out[key] = m.group(1).strip() if m else ""
    return out


def write_world_txt(entry, world, landed):
    """world.txt for a flat entry whose <world>.save is in place: 'key value' lines. Returns them as a dict."""
    save = os.path.join(entry, world + ".save")
    size = counts(read_world_xml(save))
    meta = world_meta(save)
    rows = [("md5", md5_of(save)), ("structures", size["structures"]), ("things", size["things"]),
            ("world", meta["WorldName"] or world), ("landed", "yes" if landed else "no"),
            ("game-version", meta["GameVersion"])]
    with open(os.path.join(entry, "world.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("".join(f"{k} {v}\n" for k, v in rows))
    return dict(rows)


def migrate_entry(library, world):
    """One-shot: converts a nested entry (<world>\\<world>\\<world>.save, md5.txt) to flat and writes world.txt.
    Returns 'migrated', or 'flat' when it was already flat (world.txt is refreshed); raises SystemExit when unsure."""
    import shutil
    entry = check_write(os.path.join(library, world), library)
    nested = os.path.join(entry, world)
    flat = os.path.join(entry, world + ".save")
    did = "flat"
    if os.path.isdir(nested):
        inner = os.path.join(nested, world + ".save")
        if not os.path.isfile(inner):
            raise SystemExit(f"{nested} has no {world}.save")
        if os.path.isfile(flat) and md5_of(flat) != md5_of(inner):
            raise SystemExit(f"{entry} holds two different saves (flat and nested): not touching it")
        shutil.copyfile(inner, flat)
        shutil.rmtree(nested)
        did = "migrated"
    if not os.path.isfile(flat):
        raise SystemExit(f"no {world}.save in {entry}")
    md5_txt = os.path.join(entry, "md5.txt")
    if os.path.isfile(md5_txt):
        os.remove(md5_txt)
    landed = None
    fixtures = os.path.join(entry, "fixtures.json")
    if os.path.isfile(fixtures):
        with open(fixtures, encoding="utf-8") as f:
            landed = json.load(f).get("landed")
    write_world_txt(entry, world, bool(landed) and not landed_problems(read_world_xml(flat), landed))
    return did


def migrate_library(library):
    """Migrates every nested entry of a library (one with <name>\\<name>\\ or an md5.txt); returns {world: result}."""
    out = {}
    for name in sorted(os.listdir(library)):
        entry = os.path.join(library, name)
        if os.path.isdir(os.path.join(entry, name)) or os.path.isfile(os.path.join(entry, "md5.txt")):
            out[name] = migrate_entry(library, name)
    return out


def store(world, save_folder, library, manifest, spec_text):
    """Copies a saved world into the library, flat, and writes its world.txt, README.md and fixtures.json. Returns the size."""
    import shutil
    check_save_folder(save_folder, world)
    entry = check_write(os.path.join(library, world), library)
    xml = read_world_xml(os.path.join(save_folder, world + ".save"))
    problems = landed_problems(xml, manifest.get("landed"))
    if problems:
        raise SystemExit(f"refusing to store {world}, it is not a landed world (P51): " + "; ".join(problems))
    players = player_rows(xml)
    if players:
        kept = dict(manifest["landed"].get("player") or {}, owner=players[0]["owner"])
        # find thing carries no rotation, so a builder's facing is often empty: the save's own heading (degrees about y).
        if kept.get("facing") is None and players[0]["heading"] is not None:
            kept["facing"] = f"heading {players[0]['heading']} deg"
        manifest["landed"]["player"] = kept
    if inside(save_folder, entry):
        raise SystemExit(f"refusing {save_folder}: it is inside the entry it would replace")
    os.makedirs(entry, exist_ok=True)
    nested = os.path.join(entry, world)       # an old nested layout is replaced by the flat one
    if os.path.isdir(nested):
        shutil.rmtree(nested)
    old_md5 = os.path.join(entry, "md5.txt")
    if os.path.isfile(old_md5):
        os.remove(old_md5)
    shutil.copyfile(os.path.join(save_folder, world + ".save"), os.path.join(entry, world + ".save"))
    size = counts(read_world_xml(os.path.join(entry, world + ".save")))
    info = write_world_txt(entry, world, True)
    with open(os.path.join(entry, "fixtures.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"world": world, "fixtures": manifest.get("fixtures", {}), "landed": manifest.get("landed"), "size": {
            "things": size["things"], "structures": size["structures"]}}, f, indent=1)
        f.write("\n")
    with open(os.path.join(entry, "README.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(readme(world, spec_text, manifest, size, info["md5"]))
    return size


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser(description="Migrate a world library's nested entries to the flat layout.")
    ap.add_argument("migrate", help="the word migrate")
    ap.add_argument("--library", help="the world library (default: STATIONGOD_TEST_WORLDS)")
    ap.add_argument("--world", help="one world (default: every nested entry)")
    a = ap.parse_args()
    lib = library_root(a.library)
    if a.world:
        print(json.dumps({a.world: migrate_entry(lib, a.world)}))
    else:
        print(json.dumps(migrate_library(lib)))
