"""py -3.12 -m unittest discover -s tools/test-worlds -p "test_*.py"

The build scripts against a fake game (no game run), and the library files on a synthetic save.
"""
import io
import json
import os
import re
import shutil
import sys
import tempfile
import unittest
import zipfile
from contextlib import redirect_stderr, redirect_stdout

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import build_world  # noqa: E402
import builder  # noqa: E402
import make_large_world  # noqa: E402
import twlib  # noqa: E402

sys.path.insert(0, os.path.join(HERE, "..", "..", "tests", "gate"))
import cases  # noqa: E402


def text(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


BOX = {"min": {"x": -1.0, "y": 0.0, "z": -1.0}, "max": {"x": 1.0, "y": 2.0, "z": 1.0}}


class FakeGame:
    """Answers each verb as StationGod's replies are shaped; records every call."""

    def __init__(self, missing=(), players=1, not_ready=None, job_status="applied", queued=False):
        self.calls = []
        self.missing = set(missing)
        self.players = players
        self.not_ready = not_ready
        self.job_status = job_status
        self.queued = queued
        self.next_id = 1000
        self.jobs = {}
        self.named = {}           # id -> (prefab, label) of every structure placed with a label
        self.seated = True        # a new world's player, until a move takes them out
        self.player_at = [100.3, 50.2, 200.7]

    def new_ids(self, n):
        ids = list(range(self.next_id, self.next_id + n))
        self.next_id += n
        return ids

    def __call__(self, verb, what=None, **o):
        self.calls.append((verb, what, o))
        key = (verb, what)
        if key == ("inspect", "prefab"):
            if o["prefab"] in self.missing:
                return {"error": {"code": "prefab_missing"}}
            mount = o["prefab"] in ("StructureLogicMemory", "StructureCircuitHousing", "StructureLogicSwitch",
                                    "StructureActiveVent")
            return {"prefabs": [{"prefab_name": o["prefab"], "render_box": BOX, "small_grid": True,
                                 "placement": "face_mount" if mount else "grid"}]}
        if key == ("find", "thing"):
            if o.get("name"):
                return {"things": [{"reference_id": i, "prefab_name": p, "custom_name": label, "display_name": p}
                                   for i, (p, label) in self.named.items() if o["name"].lower() in label.lower()]}
            if "exclude_prefab" in o:
                rows = getattr(self, "small_rows", [])[o.get("offset", 0):][:o["limit"]]
                return {"things": rows, "has_more": False}
            if o.get("prefab") == builder.LANDER:
                ys = getattr(self, "lander_ys", None)
                y = ys.pop(0) if ys and len(ys) > 1 else (ys[0] if ys else 40.0)
                return {"things": [{"reference_id": 39, "prefab_name": builder.LANDER,
                                    "position": {"x": 90.0, "y": y, "z": 190.0}}]}
            if o.get("prefab"):
                return {"things": [], "total": getattr(self, "big", {}).get(o["prefab"], 0)}
            if o.get("limit") == 1 and not o.get("runtime_type") and not o.get("name"):
                return {"things": [], "total": getattr(self, "total", 25156), "count": 1}
            if o.get("runtime_type") == "Human":
                held = [{"reference_id": 39, "slot_name": "Seat"}] if self.seated else []
                x, y, z = self.player_at
                return {"things": [{"reference_id": 77, "prefab_name": "Character", "display_name": "Tester",
                                    "held_in": held, "rotation": {"facing": "+z"},
                                    "position": {"x": x, "y": y, "z": z}}][:self.players]}
            return {"things": []}
        if key in (("build", "structure"), ("build", "cable"), ("build", "pipe")):
            if not o.get("confirm"):
                return {"ready": self.not_ready != verb + what, "problems": []}
            rows = o.get("placements") or [{}]
            ids = self.new_ids(len(rows))
            for i, row in zip(ids, rows):
                if row.get("label"):
                    self.named[str(i)] = (row.get("prefab"), row["label"])
            # build's placed is a list of id strings (catalogue reply_top), listed newest first here so nothing
            # can lean on its order.
            placed = [str(i) for i in reversed(ids)]
            if self.queued:
                job = f"job-{len(self.jobs)}"
                self.jobs[job] = placed
                return {"job_id": job, "status": "queued"}
            return {"job_id": "j", "status": self.job_status, "result": {"placed": placed}}
        if key == ("inspect", "job"):
            return {"job_id": o["id"], "status": self.job_status, "result": {"placed": self.jobs[o["id"]]}}
        if key == ("inspect", "connections"):
            return {"ends": [{"index": 0, "type_name": "Power", "role_name": "Input", "network": {"id": 55}},
                             {"index": 1, "type_name": "Power", "role_name": "Output", "network": {"id": 56}},
                             {"index": 2, "type_name": "Data", "role_name": "Output"}]}
        if key == ("plan", "route"):
            return {"found": True, "build_arguments": {"what": o["kind"], "waypoints": [[1, 2, 3], [1, 2, 5]],
                                                       "grade": o.get("grade"), "materials": "player"}}
        if key == ("build", "item"):
            return {"placed": [{"reference_id": i} for i in self.new_ids(max(1, int(o["quantity"]) // builder.ORE_STACK)
                                                                          if o["prefab"] == "ItemIronOre"
                                                                          else int(o["quantity"]) if
                                                                          "Pumpkin" not in o["prefab"] else 1)]}
        if key == ("inspect", "contents"):
            return {"silo": {"count": getattr(self, "silo_count", builder.SILO_CAPACITY)}}
        if key == ("write", "logic"):
            ids = o["id"] if isinstance(o["id"], list) else [o["id"]]
            return {"results": [{"index": n, "ok": True, "reference_id": str(i)} for n, i in enumerate(ids)]}
        if key == ("read", "logic"):
            return {"value": 1.0}
        if key == ("move", "player"):
            self.seated = False
            self.player_at = list(o["at"])
            return {"moved_by": "seat_exit"}
        if key == ("find", "room"):
            return {"rooms": [{"room_id": 9}]}
        if key == ("server", "command"):
            return {"output": [], "completes_asynchronously": o["command"].startswith("save")}
        return {}

    def of(self, verb, what):
        return [o for v, w, o in self.calls if (v, w) == (verb, what)]


def quick(game):
    return builder.Builder(game, sleep=lambda s: None)


class SiloWorld(unittest.TestCase):
    def test_builds_every_fixture(self):
        g = FakeGame()
        m = builder.silo(quick(g))
        f = m["fixtures"]
        for role in ("silo_ore", "silo_room", "silo_full", "locker", "tray", "battery", "player", "suit", "backpack_full",
                     "backpacks_spare", "food_canned", "food_fresh", "ground"):
            self.assertIn(role, f)
        self.assertNotEqual(f["silo_ore"]["id"], f["silo_room"]["id"])
        # every structure is free and reported before it is confirmed
        builds = g.of("build", "structure")
        self.assertTrue(all(o["free"] for o in builds))
        self.assertEqual([o.get("confirm", False) for o in builds][:2], [False, True])
        # the ore goes into the ore silo by id, the seed into the tray
        deposit = [o for o in g.of("move", "item") if o["to"] == f["silo_ore"]["id"]]
        self.assertEqual(len(deposit), 1)
        self.assertEqual(len(deposit[0]["id"]), builder.ORE_STACKS)
        self.assertTrue(any(o["to"] == f["tray"]["id"] and o["slot"] == "auto" for o in g.of("move", "item")))
        # the three silos cabled from the battery's output, the battery full, silos on
        cables = g.of("plan", "route")
        self.assertEqual({c["end"]["id"] for c in cables},
                         {f["silo_ore"]["id"], f["silo_room"]["id"], f["silo_full"]["id"]})
        self.assertTrue(all(c["start"] == {"id": f["battery"]["id"], "port": 1} for c in cables))
        self.assertTrue(all("materials" not in o and o["free"] for o in g.of("build", "cable")))
        self.assertEqual(g.of("write", "battery")[0]["ratio"], 1.0)
        # fresh food: FOOD_STACKS loose stacks of FOOD_QUANTITY, the suit to the player, the player moved by name
        food = [o for o in g.of("build", "item") if o["prefab"] == "ItemCookedPumpkin"]
        self.assertEqual([o["quantity"] for o in food], [builder.FOOD_QUANTITY] * builder.FOOD_STACKS)
        self.assertTrue(any(o["prefab"] == "ItemHardSuit" and o["to"] == "77" for o in g.of("build", "item")))
        self.assertEqual(g.of("move", "player")[0]["player"], "Tester")
        # the difficulty is the last change
        self.assertEqual(g.calls[-1][2]["command"], "difficulty Normal")

    def test_floor_is_whole_and_frames_in_jobs_of_64(self):
        g = FakeGame()
        builder.silo(quick(g))
        frames = [p for o in g.of("build", "structure") if o.get("confirm") for p in o["placements"]
                  if p["prefab"] == "StructureFrame"]
        cells = {tuple(p["at"]) for p in frames}
        self.assertEqual(len(cells), len(frames))
        self.assertTrue(all(len(o["placements"]) <= 64 for o in g.of("build", "structure")))

    def test_missing_prefab_stops_before_any_change(self):
        g = FakeGame(missing={"SeedBag_Pumpkin"})
        with self.assertRaises(builder.Refused) as e:
            builder.silo(quick(g))
        self.assertIn("SeedBag_Pumpkin", str(e.exception))
        self.assertEqual({v for v, _w, _o in g.calls}, {"inspect"})

    def test_no_player_stops(self):
        g = FakeGame(players=0)
        with self.assertRaises(builder.Refused) as e:
            builder.silo(quick(g))
        self.assertIn("Steam-off client", str(e.exception))
        self.assertFalse(g.of("build", "structure"))

    def test_report_not_ready_is_never_confirmed(self):
        g = FakeGame(not_ready="buildstructure")
        with self.assertRaises(builder.Refused):
            builder.silo(quick(g))
        self.assertFalse([o for o in g.of("build", "structure") if o.get("confirm")])

    def test_queued_jobs_are_polled_to_the_end(self):
        g = FakeGame(queued=True)
        m = builder.silo(quick(g))
        self.assertTrue(g.of("inspect", "job"))
        self.assertIsNotNone(m["fixtures"]["silo_ore"]["id"])

    def test_a_job_that_does_not_apply_stops(self):
        g = FakeGame(job_status="refused")
        with self.assertRaises(builder.Refused):
            builder.silo(quick(g))

    def test_full_silo_is_filled_to_capacity_and_counted(self):
        g = FakeGame()
        m = builder.silo(quick(g))
        f = m["fixtures"]
        self.assertEqual(f["silo_full"]["prefab"], "StructureSDBSilo")
        self.assertEqual(len({f[r]["id"] for r in ("silo_ore", "silo_room", "silo_full")}), 3)
        into = [o for o in g.of("move", "item") if o["to"] == f["silo_full"]["id"]]
        self.assertEqual(sum(len(o["id"]) for o in into), builder.SILO_CAPACITY)
        self.assertTrue(all(len(o["id"]) <= builder.FILL_CHUNK for o in into))
        self.assertIn(f["silo_full"]["id"], {c["end"]["id"] for c in g.of("plan", "route")})
        self.assertTrue(g.of("inspect", "contents"))

    def test_full_silo_short_is_refused(self):
        g = FakeGame()
        g.silo_count = 599
        with self.assertRaises(builder.Refused) as e:
            builder.silo(quick(g))
        self.assertIn("599 entries", str(e.exception))

    def test_unpowered_silo_is_waited_for_then_refused(self):
        class Dark(FakeGame):
            def __call__(self, verb, what=None, **o):
                if (verb, what) == ("read", "logic"):
                    self.reads = getattr(self, "reads", 0) + 1
                    return {"value": 1.0 if self.reads > 2 else 0.0}
                return super().__call__(verb, what, **o)
        g = Dark()
        builder.silo(quick(g))
        self.assertGreater(g.reads, 3)            # the first silo waited two polls, then all three read powered
        b = builder.Builder(FakeGame(), sleep=lambda s: None)
        b.game = lambda verb, what=None, **o: {"value": 0.0} if (verb, what) == ("read", "logic") else FakeGame()(verb, what, **o)
        with self.assertRaises(builder.Refused) as e:
            b.wait_powered("445", timeout_s=0)
        self.assertIn("unpowered", str(e.exception))

    def test_the_battery_and_the_silos_are_switched_on(self):
        g = FakeGame()
        f = builder.silo(quick(g))["fixtures"]
        on = [i for o in g.of("write", "logic") if o["logic"] == "On" and o["value"] == 1 for i in o["id"]]
        for role in ("battery", "silo_ore", "silo_room", "silo_full"):
            self.assertIn(f[role]["id"], on)


class OtherWorlds(unittest.TestCase):
    def test_minimal_only_takes_the_player_out_and_sets_the_difficulty(self):
        g = FakeGame()
        m = builder.minimal(quick(g))
        self.assertEqual(m["fixtures"]["player"]["id"], "77")
        changes = [(v, w) for v, w, _o in g.calls if v not in ("find", "inspect")]
        self.assertEqual(changes, [("move", "player"), ("server", "command")])

    def test_gate_has_the_suite_fixtures(self):
        g = FakeGame()
        m = builder.gate(quick(g))
        f = m["fixtures"]
        for key in ("door", "mem1", "mem2", "doors", "door_switch", "door_chip", "vent", "vent_net", "o2_source"):
            self.assertIn(key, f)
        self.assertEqual(len(f["doors"]["id"]), builder.HANGAR_DOORS)
        self.assertEqual(f["vent_net"]["id"], "55")
        self.assertEqual(g.of("move", "gas")[0]["source"], "create")
        self.assertEqual(g.of("write", "chip")[0]["pins"], {"d0": f["door_switch"]["id"]})
        self.assertEqual(g.calls[-1][2]["command"], "difficulty Creative")

    def test_gate_is_vanilla_and_refuses_a_missing_door(self):
        self.assertFalse({"StructureForceFieldDoor", "StructureMatterlinkCapacitor"} &
                         (set(builder.GATE_PREFABS.values()) | set(builder.GATE_DOORS)))
        g = FakeGame(missing={"StructureGlassDoor"})
        with self.assertRaises(builder.Refused) as e:
            builder.gate(quick(g))
        self.assertIn("StructureGlassDoor", str(e.exception))

    def test_build_saves_under_the_world_name(self):
        g = FakeGame()
        m = build_world.build(g, "sg-minimal", 12.0, sleep=lambda s: None)
        self.assertEqual(g.calls[-1][2]["command"], "save sg-minimal")
        self.assertEqual(m["built"]["calls"], len(g.calls))

    def test_live_pipe_refused(self):
        with self.assertRaises(SystemExit):
            build_world.main(["build", "--world", "sg-minimal", "--pipe", "StationGodMCP", "--hub", "x",
                              "--manifest", os.path.join(tempfile.gettempdir(), "m.json")])


def thing(ref, prefab, x, y, z, structure):
    state = "<CurrentBuildState>0</CurrentBuildState>" if structure else "<Quantity>1</Quantity>"
    kind = "StructureSaveData" if structure else "StackableSaveData"
    return (f'    <ThingSaveData xsi:type="{kind}">\n      <ReferenceId>{ref}</ReferenceId>\n'
            f"      <PrefabName>{prefab}</PrefabName>\n      <WorldPosition>\n        <x>{x}</x>\n        <y>{y}</y>\n"
            f"        <z>{z}</z>\n      </WorldPosition>\n      {state}\n    </ThingSaveData>\n")


WORLD = ('<?xml version="1.0"?>\n<WorldData xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n  <AllThings>\n'
         + thing(5, "StructureFrame", 101, 61, 205, True) + thing(6, "StructureSDBSilo", 103, 63, 205, True)
         + thing(9, "ItemIronOre", 101, 62.2, 209, False) + "  </AllThings>\n</WorldData>\n")


def landed_xml(parent=0, player_y=63.0, lander_y=61.5):
    """A world.xml with a player and a lander."""
    player = (f'    <ThingSaveData xsi:type="HumanSaveData">\n      <ReferenceId>38</ReferenceId>\n'
              f"      <PrefabName>Character</PrefabName>\n      <WorldPosition>\n        <x>100</x>\n"
              f"        <y>{player_y}</y>\n        <z>200</z>\n      </WorldPosition>\n"
              f"      <OwnerSteamId>123</OwnerSteamId>\n      <ParentReferenceId>{parent}</ParentReferenceId>\n"
              f"      <ParentSlotId>1</ParentSlotId>\n    </ThingSaveData>\n")
    lander = (f'    <ThingSaveData xsi:type="LanderCapsuleSaveData">\n      <ReferenceId>39</ReferenceId>\n'
              f"      <PrefabName>LanderCapsuleSmall</PrefabName>\n      <WorldPosition>\n        <x>90</x>\n"
              f"        <y>{lander_y}</y>\n        <z>190</z>\n      </WorldPosition>\n    </ThingSaveData>\n")
    return WORLD.replace("  </AllThings>", player + lander + "  </AllThings>")


LANDED = {"lander_y": 61.5, "ground_y": 63.0, "ground": "the frame floor's top",
          "player": {"id": "38", "position": [100, 63, 200], "facing": "+z"}}


def write_save(folder, name, xml=None):
    xml = landed_xml() if xml is None else xml
    os.makedirs(folder, exist_ok=True)
    with zipfile.ZipFile(os.path.join(folder, name + ".save"), "w") as z:
        z.writestr("world.xml", xml)
        z.writestr("world_meta.xml", "<meta/>")
    with open(os.path.join(folder, "lint-rules.json"), "w") as f:
        f.write("{}")

    def test_check_names_what_differs_from_the_save_by_prefab(self):
        g = FakeGame()
        g.total, g.big = 1003, {"StructureFrame": 1000}
        g.small_rows = [{"prefab_name": "ItemA"}, {"prefab_name": "ItemA"}, {"prefab_name": "ItemC"}]
        saved = {"StructureFrame": 1000, "ItemA": 2, "ItemB": 3}
        seen = build_world.check(g, 1005, saved, sleep=lambda s: None)
        self.assertFalse(seen["ok"])
        self.assertEqual(seen["diff"], {"ItemB": [3, 0], "ItemC": [0, 1]})
        self.assertEqual(g.of("find", "thing")[-1]["exclude_prefab"], ["StructureFrame"])

    def test_check_compares_the_thing_count_with_the_save(self):
        g = FakeGame()
        self.assertTrue(build_world.check(g, 25156)["ok"])
        g.total = 25000
        seen = build_world.check(g, 25156)
        self.assertEqual((seen["ok"], seen["things"]), (False, 25000))


class Library(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="tw-test-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_writes_stay_inside_the_library(self):
        root = r"X:\lib"
        for bad in (r"X:\other\pristine\x", r"X:\lib", r"X:\library\sg-silo", r"X:\lib\..\other", r"Y:\lib\sg-silo",
                    "relative"):
            self.assertIsNotNone(twlib.path_refusal(bad, root), bad)
        for good in (r"X:\lib\sg-silo", r"x:\LIB\sg-silo\sg-silo", r"X:\lib\sg-gate\fixtures.json"):
            self.assertIsNone(twlib.path_refusal(good, root), good)
        with self.assertRaises(SystemExit):
            twlib.check_write(r"X:\other\pristine\x", root)

    def test_library_root_from_option_or_env_else_refused(self):
        self.assertEqual(twlib.library_root(r"X:\lib", {}), os.path.abspath(r"X:\lib"))
        self.assertEqual(twlib.library_root(None, {twlib.LIBRARY_ENV: r"X:\env"}), os.path.abspath(r"X:\env"))
        self.assertEqual(twlib.library_root(r"X:\lib", {twlib.LIBRARY_ENV: r"X:\env"}), os.path.abspath(r"X:\lib"))
        with self.assertRaises(SystemExit) as e:
            twlib.library_root(None, {})
        self.assertIn(twlib.LIBRARY_ENV, str(e.exception))

    def test_odd_and_bays(self):
        self.assertEqual([twlib.odd(v) for v in (100.3, 101.9, -0.5, 2.0)], [101, 101, -1, 3])
        self.assertEqual(twlib.bay_cells(BOX), 2)
        self.assertEqual(twlib.bay_cells([[-3, 0, -3], [3, 6, 3]]), 4)
        self.assertEqual(twlib.bay_cells(None), 2)

    def test_site_spots_are_inside_and_apart(self):
        s = twlib.Site(100.3, 62.2, 204.7, [("a", 2), ("b", 4), ("c", 2)])
        spots = s.spots()
        frames = {tuple(c) for c in s.frames()}
        self.assertEqual(len(frames), s.width * s.depth)
        for role, (x, y, z) in spots.items():
            self.assertIn((x, y - 2, z), frames, role)
        xs = sorted(p[0] for p in spots.values())
        self.assertTrue(all(b - a >= 4 for a, b in zip(xs, xs[1:])))
        cx, cy, cz = s.clear()
        self.assertTrue(all(cz > p[2] for p in spots.values()))
        self.assertEqual(cy, s.floor_y + 1.2)

    def test_counts(self):
        c = twlib.counts(WORLD)
        self.assertEqual((c["things"], c["structures"]), (3, 2))
        self.assertEqual(c["structure_prefabs"], {"StructureFrame": 1, "StructureSDBSilo": 1})

    def test_store_writes_the_entry(self):
        saves = os.path.join(self.tmp, "server", "saves", "sg-silo")
        write_save(saves, "sg-silo")
        lib = os.path.join(self.tmp, "worlds")
        manifest = {"landed": dict(LANDED), "fixtures": {"silo_ore": {"id": "6", "prefab": "StructureSDBSilo",
                                                                      "position": [103, 63, 205]}},
                    "site": "a floor", "built": {"difficulty": "Normal"}}
        size = twlib.store("sg-silo", saves, lib, manifest, "spec text")
        entry = os.path.join(lib, "sg-silo")
        self.assertEqual(size["structures"], 2)
        self.assertTrue(os.path.isfile(os.path.join(entry, "sg-silo", "sg-silo.save")))
        md5 = text(os.path.join(entry, "md5.txt")).splitlines()
        self.assertEqual([l.split("  ")[1] for l in md5], ["lint-rules.json", "sg-silo.save"])
        self.assertEqual(md5[1].split()[0], twlib.md5_of(os.path.join(entry, "sg-silo", "sg-silo.save")))
        readme = text(os.path.join(entry, "README.md"))
        for want in ("spec text", "`silo_ore`: StructureSDBSilo id 6", "structures: 2", md5[1]):
            self.assertIn(want, readme)
        self.assertEqual(json.loads(text(os.path.join(entry, "fixtures.json")))["size"]["structures"], 2)
        # a second store replaces the first
        twlib.store("sg-silo", saves, lib, manifest, "spec text")

    def test_store_takes_only_a_save_named_for_the_world(self):
        other = os.path.join(self.tmp, "saves", "SomeBase")
        write_save(other, "SomeBase")
        with self.assertRaises(SystemExit):
            twlib.store("sg-silo", other, os.path.join(self.tmp, "worlds"), {}, "")
        self.assertFalse(os.path.exists(os.path.join(self.tmp, "worlds")))

    def test_store_refuses_a_source_inside_the_entry_it_replaces(self):
        lib = os.path.join(self.tmp, "worlds")
        src = os.path.join(lib, "sg-silo", "sg-silo")
        write_save(src, "sg-silo")
        with self.assertRaises(SystemExit):
            twlib.store("sg-silo", src, lib, {}, "")
        self.assertTrue(os.path.isfile(os.path.join(src, "sg-silo.save")))

    def test_store_cli_needs_a_library(self):
        saves = os.path.join(self.tmp, "saves", "sg-minimal")
        write_save(saves, "sg-minimal")
        manifest = os.path.join(self.tmp, "m.json")
        with open(manifest, "w", encoding="utf-8") as f:
            json.dump({"fixtures": {}, "built": {}, "landed": LANDED}, f)
        args = ["store", "--world", "sg-minimal", "--save-folder", saves, "--manifest", manifest]
        held = os.environ.pop(twlib.LIBRARY_ENV, None)
        try:
            with self.assertRaises(SystemExit):
                build_world.main(args)
            lib = os.path.join(self.tmp, "worlds")
            with redirect_stdout(io.StringIO()):
                build_world.main(args + ["--library", lib])
            self.assertTrue(os.path.isfile(os.path.join(lib, "sg-minimal", "md5.txt")))
        finally:
            if held is not None:
                os.environ[twlib.LIBRARY_ENV] = held


class FindableCount(unittest.TestCase):
    """find thing never keeps organs, so the load check's expected count leaves them out (sg-cost-large: 3 short)."""

    def test_the_store_gate_refuses_an_unreadable_player_position(self):
        xml = landed_xml().replace("<y>63.0</y>", "<y>oops</y>")
        self.assertIn("oops", xml)
        problems = twlib.landed_problems(xml, dict(LANDED))
        self.assertTrue(any("position unreadable: not stored" in p for p in problems), problems)
        self.assertEqual(twlib.landed_problems(landed_xml(), dict(LANDED)), [])

    def test_organs_are_saved_but_not_findable(self):
        xml = landed_xml()
        organs = "".join('    <ThingSaveData xsi:type="OrganSaveData">\n      <ReferenceId>%d</ReferenceId>\n'
                         '      <PrefabName>%s</PrefabName>\n    </ThingSaveData>\n' % (70 + i, name)
                         for i, name in enumerate(("OrganBrain", "OrganLungs", "OrganStomach")))
        grown = xml.replace("  </AllThings>", organs + "  </AllThings>")
        before, after = twlib.counts(xml), twlib.counts(grown)
        self.assertEqual(after["things"], before["things"] + 3)
        self.assertEqual(after["findable"], before["findable"])
        live = build_world.saved_prefabs  # the by-prefab diff leaves them out too
        self.assertTrue(twlib.is_organ("OrganBrain") and not twlib.is_organ("Character"))
        self.assertIsNotNone(live)


class GateFixtures(unittest.TestCase):
    """tests/gate/cases.py reads its FIX from the fixtures.json the sg-gate build stores."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="tw-gate-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)
        cases.FIX.clear()

    def stored(self, fixtures):
        path = os.path.join(self.tmp, "fixtures.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump({"world": "sg-gate", "fixtures": fixtures}, f)
        return path

    def test_the_gate_build_gives_every_key_the_suite_reads(self):
        g = FakeGame()
        built = builder.gate(quick(g))["fixtures"]
        fix = cases.load_fix(self.stored(built))
        self.assertEqual(set(fix), set(cases.FIX_KEYS) | {"pipe_run"})
        self.assertEqual(fix["pipe_run"], built["pipe_run"]["position"])
        code = "sg.build('pipe', waypoints=%s)" % (fix["pipe_run"],)
        self.assertIn("waypoints=[[", code)
        self.assertEqual(fix["doors"], [str(i) for i in built["doors"]["id"]])
        self.assertTrue(all(isinstance(v, str) for k, v in fix.items() if k not in ("doors", "pipe_run")))
        # driven_by names the housing, so door_chip is the housing the program runs in
        housing = g.of("write", "chip")[0]["id"]
        self.assertEqual(fix["door_chip"], str(housing))

    def test_missing_keys_and_file_are_named(self):
        with self.assertRaises(RuntimeError) as e:
            cases.load_fix(self.stored({"door": {"id": "1"}, "doors": {"id": []}}))
        self.assertIn("doors", str(e.exception))
        self.assertIn("vent_net", str(e.exception))
        with self.assertRaises(RuntimeError) as e:
            cases.load_fix(os.path.join(self.tmp, "none.json"))
        self.assertIn("SG_GATE_FIXTURES", str(e.exception))

    def test_fixtures_path_option_then_env_else_named_error(self):
        env = {"SG_GATE_FIXTURES": r"X:\f.json", "STATIONGOD_TEST_WORLDS": r"X:\lib"}
        self.assertEqual(cases.fixtures_path(r"X:\given.json", env), r"X:\given.json")
        self.assertEqual(cases.fixtures_path(None, env), r"X:\f.json")
        self.assertEqual(cases.fixtures_path(None, {"STATIONGOD_TEST_WORLDS": r"X:\lib"}),
                         os.path.join(r"X:\lib", "sg-gate", "fixtures.json"))
        with self.assertRaises(RuntimeError) as e:
            cases.fixtures_path(None, {})
        for name in ("--fixtures", "SG_GATE_FIXTURES", "STATIONGOD_TEST_WORLDS"):
            self.assertIn(name, str(e.exception))

    def test_reset_command_option_then_env_else_named_error(self):
        env = {"SG_GATE_RESET": r"X:\rig\reset-env.cmd sg-gate"}
        self.assertEqual(cases.reset_command(r"X:\rig\reload.ps1 -World sg-gate", env),
                         ["powershell", "-NoProfile", "-File", r"X:\rig\reload.ps1", "-World", "sg-gate"])
        self.assertEqual(cases.reset_command(None, env), [r"X:\rig\reset-env.cmd", "sg-gate"])
        self.assertEqual(cases.reset_command(r'"X:\a rig\reload.PS1" sg-gate', {}),
                         ["powershell", "-NoProfile", "-File", r"X:\a rig\reload.PS1", "sg-gate"])
        held = cases.RESET
        try:
            cases.RESET = r"X:\from-option.ps1"
            self.assertEqual(cases.reset_command(None, env)[-1], r"X:\from-option.ps1")
        finally:
            cases.RESET = held
        with self.assertRaises(RuntimeError) as e:
            cases.reset_command(None, {})
        for name in ("--reset", "SG_GATE_RESET", "sg-gate"):
            self.assertIn(name, str(e.exception))

    def test_use_fix_fills_in_place(self):
        g = FakeGame()
        held = cases.FIX
        cases.use_fix(self.stored(builder.gate(quick(g))["fixtures"]))
        self.assertIs(cases.FIX, held)
        self.assertEqual(len(held), len(cases.FIX_KEYS) + 1)

    def test_every_case_script_formats_with_the_fixtures(self):
        """Each case's %-formatted script takes exactly the FIX values it is given (no game: the formatting only)."""
        import inspect
        import re
        cases.use_fix(self.stored(builder.gate(quick(FakeGame()))["fixtures"]))
        src = inspect.getsource(cases)
        checked = []
        for m in re.finditer(r'\(\s*((?:"[^"\n]*"\s*)+)%\s*\(([^()]*(?:\([^()]*\)[^()]*)*)\)', src):
            if not re.fullmatch(r'\s*(FIX\["\w+"\](\[\d*:?\d*\])?\s*,?\s*)+', m.group(2)):
                continue                                                  # a script built from other values
            fmt = "".join(re.findall(r'"([^"\n]*)"', m.group(1))).encode().decode("unicode_escape")
            args = eval("(" + m.group(2) + ",)", {"FIX": cases.FIX})   # noqa: S307 - our own source
            checked.append(fmt % args)
        self.assertGreaterEqual(len(checked), 5)
        self.assertTrue(any("waypoints=%s" % (cases.FIX["pipe_run"],) in c for c in checked))


class LargeWorld(unittest.TestCase):
    def test_grow_adds_the_block_far_away_with_new_ids(self):
        xml, added = make_large_world.grow(WORLD)
        c = twlib.counts(xml)
        self.assertEqual(c["things"], 3 + make_large_world.FRAMES + make_large_world.LIGHTS
                         + 2 * make_large_world.ITEMS)
        self.assertEqual(added["ids"][0], 10)
        self.assertEqual(added["origin"], [twlib.odd(303), twlib.odd(263), twlib.odd(409)])
        self.assertEqual(xml.count("<ReferenceId>"), len(set(
            __import__("re").findall(r"<ReferenceId>(\d+)</ReferenceId>", xml))))

    def test_main_reads_a_library_world(self):
        tmp = tempfile.mkdtemp(prefix="tw-large-")
        try:
            lib = os.path.join(tmp, "worlds")
            write_save(os.path.join(lib, "sg-silo", "sg-silo"), "sg-silo")
            with open(os.path.join(lib, "sg-silo", "fixtures.json"), "w") as f:
                json.dump({"fixtures": {"silo_ore": {"id": "6"}}, "landed": LANDED}, f)
            with redirect_stdout(io.StringIO()):
                make_large_world.main(["--library", lib])
            self.assertEqual(twlib.verify_stored("sg-cost-large", lib), [])
            out = os.path.join(lib, "sg-cost-large")
            self.assertTrue(os.path.isfile(os.path.join(out, "sg-cost-large", "sg-cost-large.save")))
            self.assertTrue(os.path.isfile(os.path.join(out, "sg-cost-large", "lint-rules.json")))
            self.assertEqual(json.loads(text(os.path.join(out, "fixtures.json")))["fixtures"], {"silo_ore": {"id": "6"}})
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class RigEnd(unittest.TestCase):
    """build's end for the rig watcher: the saved world waited for, and the done marker."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="tw-end-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_wait_save_waits_until_the_file_stops_growing(self):
        folder = os.path.join(self.tmp, "sg-silo")
        os.makedirs(folder)
        path = os.path.join(folder, "sg-silo.save")
        sizes = iter([0, 10, 20, 20])
        t = [0.0]

        def sleep(s):
            t[0] += s
            with open(path, "wb") as f:
                f.write(b"x" * next(sizes))
        self.assertEqual(build_world.wait_save(self.tmp, "sg-silo", sleep=sleep, clock=lambda: t[0]), path)
        self.assertEqual(os.path.getsize(path), 20)

    def test_wait_save_gives_up(self):
        t = [0.0]

        def sleep(s):
            t[0] += s
        with self.assertRaises(SystemExit) as e:
            build_world.wait_save(self.tmp, "sg-silo", timeout_s=10, sleep=sleep, clock=lambda: t[0])
        self.assertIn("was not written", str(e.exception))

    def test_done_marker_on_failure_and_success(self):
        out = os.path.join(self.tmp, "out")
        args = ["build", "--world", "sg-minimal", "--pipe", "SG-TEST", "--hub", "x",
                "--manifest", os.path.join(self.tmp, "m.json"), "--done", out]
        real = build_world.connect
        build_world.connect = lambda pipe, hub: (_ for _ in ()).throw(RuntimeError("no pipe"))
        try:
            with self.assertRaises(RuntimeError):
                build_world.main(args)
            self.assertEqual(text(os.path.join(out, "done-fail")), "RuntimeError: no pipe")
            g = FakeGame()
            build_world.connect = lambda pipe, hub: g
            real_build = build_world.build
            build_world.build = lambda game, world, lift, trace=False: {"fixtures": {"player": "1"}, "built": {}}
            try:
                with redirect_stdout(io.StringIO()):
                    self.assertEqual(build_world.main(args), 0)
            finally:
                build_world.build = real_build
            self.assertEqual(text(os.path.join(out, "done-ok")), "sg-minimal")
        finally:
            build_world.connect = real

    def test_smoke_saves_and_reports_the_host(self):
        g = FakeGame()
        seen = build_world.smoke(g, sleep=lambda s: None)
        self.assertIn("player", seen)
        self.assertTrue(any(c[0] == "server" and c[1] == "command" and "save sg-smoke" in str(c) for c in g.calls))

    def test_wait_world_waits_for_a_body(self):
        replies = iter([{"error": {"code": "unavailable"}}, {"things": []},
                        {"things": [{"reference_id": 5, "display_name": "Host"}]}])
        got = build_world.wait_world(lambda v, w, **o: next(replies), sleep=lambda s: None, clock=lambda: 0.0)
        self.assertEqual(got["reference_id"], 5)

    def test_wait_world_gives_up(self):
        t = [0.0]

        def sleep(s):
            t[0] += s
        with self.assertRaises(SystemExit) as e:
            build_world.wait_world(lambda v, w, **o: {"things": []}, timeout_s=20, sleep=sleep, clock=lambda: t[0])
        self.assertIn("no player yet", str(e.exception))

    def test_smoke_reads_the_welcome_again_when_it_had_no_server(self):
        g = FakeGame()
        g.welcome = {"server": None}
        g.rewelcome = lambda: {"server": {"role": "host", "dedicated": False, "pipe_name": "SG-VM"}}
        seen = build_world.smoke(g, sleep=lambda s: None)
        self.assertEqual((seen["role"], seen["dedicated"], seen["pipe"]), ("host", False, "SG-VM"))


class Replies(unittest.TestCase):
    """The builder reads what it needs wherever StationGod's reply puts it."""

    def test_field_reads_the_declared_key_or_a_single_list_result(self):
        self.assertEqual(builder.field({"ends": [1]}, "ends"), [1])
        reply = {"gateway_id": "world", "results": [{"index": 0, "ok": True, "ends": [{"index": 1, "type": "Power"}]}]}
        with redirect_stderr(io.StringIO()):
            self.assertEqual(builder.field(reply, "ends"), [{"index": 1, "type": "Power"}])
        # no search below that: a key deeper in the reply is not taken for the asked one
        self.assertIsNone(builder.field({"thing": {"ends": [9]}}, "ends"))
        self.assertIsNone(builder.field({"results": [{"ends": [1]}, {"ends": [2]}]}, "ends"))

    def test_power_port_from_a_wrapped_reply(self):
        class Wrapped(FakeGame):
            def __call__(self, verb, what=None, **o):
                reply = super().__call__(verb, what, **o)
                if (verb, what) == ("inspect", "connections"):
                    return {"results": [dict({"index": 0, "ok": True}, **reply)]}
                return reply
        self.assertEqual(quick(Wrapped()).power_port("5", prefer="output"), 1)

    def test_no_power_port_names_the_ends_seen(self):
        class NoPower(FakeGame):
            def __call__(self, verb, what=None, **o):
                if (verb, what) == ("inspect", "connections"):
                    return {"results": [{"ends": [{"index": 0, "type": "Data", "role": "Input"}]}]}
                return super().__call__(verb, what, **o)
        with self.assertRaises(builder.Refused) as e:
            quick(NoPower()).power_port("5")
        self.assertIn("Data", str(e.exception))


class Catalogue(unittest.TestCase):
    """Every reply key the builder reads is one StationGod's catalogue declares for that call (catalogue/verbs)."""

    ROOT = os.path.join(HERE, "..", "..", "catalogue", "verbs")
    SOURCES = ("builder.py", "build_world.py")

    @staticmethod
    def top_keys(text):
        """The top-level keys of a declared shape: "{a: int, b: {c: str}}" -> {a, b}; a bare type name -> set()."""
        keys, depth, word = set(), 0, ""
        for ch in str(text):
            if ch in "{[(":
                depth += 1
            elif ch in "}])":
                depth -= 1
            if depth == 1 and ch == ":" and word.strip():
                keys.add(word.strip().split()[-1])
            word = word + ch if (ch.isalnum() or ch == "_") else ("" if ch in ",{" else word + ch if ch == " " else "")
        return keys

    def declared(self):
        cat = {}
        shared = json.loads(text(os.path.join(self.ROOT, "_shared.json")))
        views = {k.split(".", 1)[1]: self.top_keys(v.split("=", 1)[1]) for k, v in shared["topics"].items()
                 if k.startswith("types.") and "=" in v}
        for name in os.listdir(self.ROOT):
            if not name.endswith(".json") or name.startswith("_"):
                continue
            with open(os.path.join(self.ROOT, name), encoding="utf-8") as f:
                d = json.load(f)
            whats = d["whats"].items() if isinstance(d["whats"], dict) else [(w.get("what"), w) for w in d["whats"]]
            for what, spec in whats:
                keys = set(d.get("reply_top") or {})
                for v in (spec.get("reply") or {}).get("variants", []):
                    shape = v.get("keys")
                    if isinstance(shape, dict):
                        keys |= set(shape)
                    elif isinstance(shape, str):
                        keys |= views.get(shape, set())
                    if v.get("job_view"):
                        keys |= views.get(v["job_view"], set())
                cat[(d["verb"], what)] = keys
        return cat

    def source(self):
        return "".join(text(os.path.join(HERE, n)) for n in self.SOURCES)

    def test_every_call_is_in_the_reads_table(self):
        called = set(re.findall(r'(?:do|game)\("(\w+)", "(\w+)"', self.source()))
        self.assertEqual(called - set(builder.READS), set())

    def test_every_read_is_declared_by_the_catalogue(self):
        cat = self.declared()
        wrong = {(call, key) for call, keys in builder.READS.items() for key in keys
                 if call not in cat or key not in cat[call]}
        self.assertEqual(wrong, set())

    def test_every_key_the_code_reads_is_in_the_table(self):
        read = set(re.findall(r'field\([^,]+, "(\w+)"\)', self.source()))
        listed = {k for keys in builder.READS.values() for k in keys}
        self.assertEqual(read - listed, set())

    def test_the_shape_parser(self):
        self.assertEqual(self.top_keys("{a: int, b: {c: str, d: int} | None, e: list[{f: int}]}"), {"a", "b", "e"})


class Hooks(unittest.TestCase):
    """The rig hooks (vm/*.ps1) and the build's end."""

    def test_no_cast_in_an_argument_list(self):
        # PowerShell reads `[bool]$x` in a command's arguments as the string "[bool]..." (always true).
        for name in os.listdir(os.path.join(HERE, "vm")):
            for n, line in enumerate(text(os.path.join(HERE, "vm", name)).splitlines(), 1):
                if "-ArgumentList" in line:
                    args = line.split("-ArgumentList", 1)[1].split("{", 1)[0]
                    self.assertNotRegex(args, r"\[\w+\]\$", f"{name}:{n}")

    def test_the_unzip_call_has_room_past_invoke_rigs_default(self):
        self.assertIn("Invoke-Rig -TimeoutSeconds 600", text(os.path.join(HERE, "vm", "prelaunch.ps1")))

    def test_arguments_that_do_not_parse_still_write_done_fail(self):
        tmp = tempfile.mkdtemp(prefix="tw-args-")
        try:
            out = os.path.join(tmp, "out")
            with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                build_world.main(["check", "--pipe", "SG-T", "--hub", "x", "--expect", "[int]@{}.expect",
                                  "--done", out])
            self.assertIn("arguments refused", text(os.path.join(out, "done-fail")))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class Layouts(unittest.TestCase):
    """The layouts against what the game is known to build and how it mounts small devices."""

    def test_every_layout_structure_is_known_buildable(self):
        layout = (set(builder.SILO_PREFABS.values()) | set(builder.GATE_PREFABS.values()) | set(builder.GATE_DOORS)
                  | {builder.HANGAR_DOOR, "StructureFrame"})
        self.assertEqual(layout - set(builder.KNOWN_BUILDABLE), set())

    def test_face_mount_devices_mount_on_the_floor_and_grid_pieces_do_not(self):
        g = FakeGame()
        builder.gate(quick(g))
        rows = [r for o in g.of("build", "structure") if o.get("confirm") for r in o["placements"]]
        mounted = {r["prefab"] for r in rows if r.get("orient") == {"mount": "floor"}}
        self.assertEqual(mounted, {"StructureLogicMemory", "StructureCircuitHousing", "StructureLogicSwitch",
                                   "StructureActiveVent"})

    def test_the_oxygen_tank_is_not_on_the_vent_pipe(self):
        g = FakeGame()
        f = builder.gate(quick(g))["fixtures"]
        route = g.of("plan", "route")[-1]
        self.assertEqual(route["start"], {"id": f["vent"]["id"]})
        self.assertNotEqual(route["end"]["id"], f["o2_source"]["id"])
        gas = g.of("move", "gas")[0]
        self.assertEqual(gas["to"], f["o2_source"]["id"])

    def test_power_is_a_trunk_each_further_device_drops_to_the_network(self):
        g = FakeGame()
        f = builder.gate(quick(g))["fixtures"]
        cables = [o for o in g.of("plan", "route") if o["kind"] == "cable"]
        self.assertEqual(cables[0]["start"], {"id": f["battery"]["id"], "port": 1})
        self.assertTrue(len(cables) > 2)
        self.assertTrue(all(c["end"] == {"network": "56"} for c in cables[1:]))


class Landed(unittest.TestCase):
    """P51: a stored world is landed; a builder sees it land, the store refuses anything else, a load refuses a seat."""

    def test_the_builder_waits_for_the_lander_before_it_reads_the_ground(self):
        g = FakeGame()
        g.lander_ys = [200.0, 150.0, 100.0, 60.0]
        calls = []
        real = g.__call__

        def spy(verb, what=None, **o):
            calls.append((verb, what, o))
            return real(verb, what, **o)
        builder.minimal(quick(spy))
        moves = [c for c in calls if c[:2] == ("move", "player")]
        self.assertEqual(moves[0][2]["at"], [100.0, 60.0, 200.0])          # beside where it settled, not where it fell
        self.assertTrue(moves[0][2]["safe_ground"])

    def test_a_player_already_out_is_left_where_they_are(self):
        g = FakeGame()
        g.seated = False
        builder.minimal(quick(g))
        self.assertEqual(g.of("move", "player"), [])

    def test_a_lander_that_never_settles_stops_the_build(self):
        g = FakeGame()
        g.lander_ys = [float(900 - n) for n in range(400)]
        with self.assertRaises(builder.Refused) as e:
            builder.minimal(quick(g))
        self.assertIn("never settled", str(e.exception))

    def test_a_refused_floor_goes_higher_and_changes_nothing_first(self):
        g = FakeGame()
        real = g.__call__
        tries = []

        def game(verb, what=None, **o):
            if (verb, what) == ("build", "structure") and not o.get("confirm"):
                tries.append(o["placements"][0]["at"][1])
                if len(tries) == 1:
                    return {"ready": False, "problems": [{"code": "cannot_place"}]}
            return real(verb, what, **o)
        builder.silo(quick(game))
        self.assertEqual(tries[1] - tries[0], 4)

    def test_manifests_carry_the_landed_record(self):
        for build in (builder.minimal, builder.silo, builder.gate):
            landed = build(quick(FakeGame()))["landed"]
            self.assertEqual(landed["lander_y"], 40.0)
            self.assertEqual(landed["player"]["facing"], "+z")

    def test_the_store_refuses_a_seated_player_a_mid_air_one_and_a_lander_in_the_air(self):
        tmp = tempfile.mkdtemp(prefix="tw-landed-")
        try:
            lib = os.path.join(tmp, "lib")
            os.makedirs(lib)
            for xml, word in ((landed_xml(parent=39), "seated"), (landed_xml(player_y=116.0), "mid-air"),
                              (landed_xml(lander_y=205.0), "lander is in the air")):
                saves = os.path.join(tmp, "saves", "sg-silo")
                write_save(saves, "sg-silo", xml)
                with self.assertRaises(SystemExit) as e:
                    twlib.store("sg-silo", saves, lib, {"landed": dict(LANDED)}, "")
                self.assertIn(word, str(e.exception))
                shutil.rmtree(saves)
            saves = os.path.join(tmp, "saves", "sg-silo")
            write_save(saves, "sg-silo")
            with self.assertRaises(SystemExit) as e:
                twlib.store("sg-silo", saves, lib, {}, "")
            self.assertIn("no landed record", str(e.exception))
            self.assertFalse(os.path.exists(os.path.join(lib, "sg-silo")))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_verify_fails_a_stored_world_that_is_not_landed(self):
        tmp = tempfile.mkdtemp(prefix="tw-verify-")
        try:
            lib = os.path.join(tmp, "lib")
            os.makedirs(lib)
            saves = os.path.join(tmp, "saves", "sg-silo")
            write_save(saves, "sg-silo")
            twlib.store("sg-silo", saves, lib, {"landed": dict(LANDED)}, "")
            self.assertEqual(twlib.verify_stored("sg-silo", lib), [])
            stored = json.loads(text(os.path.join(lib, "sg-silo", "fixtures.json")))
            self.assertEqual(stored["landed"]["player"]["owner"], "123")
            self.assertIn("owner id 123", text(os.path.join(lib, "sg-silo", "README.md")))
            write_save(os.path.join(lib, "sg-silo", "sg-silo"), "sg-silo", landed_xml(parent=39))
            self.assertIn("seated", twlib.verify_stored("sg-silo", lib)[0])
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_a_check_load_fails_if_the_player_arrives_seated_or_a_lander_descends(self):
        g = FakeGame()
        g.seated = False
        self.assertTrue(build_world.check(g, 25156, sleep=lambda s: None)["ok"])
        g = FakeGame()
        seen = build_world.check(g, 25156, sleep=lambda s: None)
        self.assertFalse(seen["ok"])
        self.assertIn("seated", seen["landing"][0])
        g = FakeGame()
        g.seated = False
        g.lander_ys = [205.0, 190.0]
        seen = build_world.check(g, 25156, sleep=lambda s: None)
        self.assertFalse(seen["ok"])
        self.assertIn("descended", seen["landing"][0])

    @staticmethod
    def _scripts():
        """Every .py and .ps1 under tests/gate and every tools folder or file except the builders (tools/test-worlds)."""
        tools = os.path.normpath(os.path.join(HERE, ".."))
        roots = [os.path.join(HERE, "..", "..", "tests", "gate")]
        roots += [os.path.join(tools, n) for n in sorted(os.listdir(tools)) if n != "test-worlds"]
        for root in roots:
            walk = [(root, [], [os.path.basename(root)])] if os.path.isfile(root) else os.walk(root)
            for folder, dirs, names in walk:
                dirs[:] = [d for d in dirs if d != "__pycache__"]
                for name in names:
                    if name.endswith((".py", ".ps1")):
                        yield os.path.join(os.path.dirname(root) if os.path.isfile(root) else folder, name)

    def test_no_test_starts_from_newgame(self):
        paths = list(self._scripts())
        self.assertTrue(any(p.endswith("silo_checks.py") for p in paths) and len(paths) > 10, paths[:5])
        for path in paths:
            self.assertNotRegex(text(path), r"-new(game)?" + chr(92) + "b", path)

    def test_world_build_is_passed_only_by_the_builders_call(self):
        """-WorldBuild (RigVM's new-game run) appears in no script outside tools/test-worlds, and there only as the one
        assignment in vm-build-world.ps1 (its help block and comments aside); a -Check run never sets it."""
        for path in self._scripts():
            self.assertNotIn("WorldBuild", text(path), path)
        builder_dir = HERE
        for name in sorted(os.listdir(builder_dir)) + ["vm/" + n for n in sorted(os.listdir(os.path.join(builder_dir, "vm")))]:
            path = os.path.join(builder_dir, name)
            if not name.endswith((".py", ".ps1")) or name == "test_test_worlds.py":
                continue
            code = re.sub(r"<#.*?#>", "", text(path), flags=re.S)
            code = "\n".join(re.sub(r"\s#.*$", "", line) for line in code.splitlines() if not line.lstrip().startswith("#"))
            hits = [line.strip() for line in code.splitlines() if "WorldBuild" in line]
            if name == "vm-build-world.ps1":
                self.assertEqual(hits, ["if (-not $Check) { $runArgs.WorldBuild = $true }"], path)
            else:
                self.assertEqual(hits, [], path)


if __name__ == "__main__":
    unittest.main()
