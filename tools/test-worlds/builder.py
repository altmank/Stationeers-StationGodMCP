"""Builds each test world's fixtures through StationGod's own verbs, on a fresh world a test server just made.

Every builder takes a Game (one call: verb, what, options -> the reply as plain JSON, {"error": {...}} for a
refusal) and returns a manifest: {"fixtures": {role: {id, prefab, position, note}}, "site": text, "built": {...}}.

What the world must be when a builder starts (build_world.py checks it):
- new and creative (`-new Lunar Creative`): StationGod's cheats (free building, build item, move gas, move player)
  are allowed only in a creative world unless someone types at the game's console, which a headless server has
  not; the builder turns the difficulty to the world's own (sg-silo: Normal, so food rots) as its last step;
- with one player character (a Steam-off client joined once): a dedicated server makes none of its own.
"""
import json
import math
import sys
import time

# The reply keys the builders read, per call, at the top level of the reply as StationGod's catalogue declares them
# (catalogue/verbs/<verb>.json: a what's reply variants, the verb's reply_top, and the job views in _shared.json's
# types topics). test_test_worlds.Catalogue holds every call and every read to this table and the table to the
# catalogue, so a reply shape the builder gets wrong fails offline, not on a rig.
READS = {
    ("build", "structure"): ["ready", "placed", "status", "job_id", "result"],
    ("build", "cable"): ["ready", "placed", "status", "job_id", "result"],
    ("build", "pipe"): ["ready", "placed", "status", "job_id", "result"],
    ("build", "item"): ["placed"],
    ("inspect", "job"): ["status", "job_id", "result"],
    ("inspect", "prefab"): ["render_box", "prefabs", "buildable"],
    ("inspect", "connections"): ["ends"],
    ("inspect", "contents"): ["silo"],
    ("find", "thing"): ["things", "total", "has_more"],
    ("plan", "route"): ["found", "failure", "build_arguments"],
    ("server", "command"): ["completes_asynchronously"],
    ("server", "info"): ["mod_version"],
    ("read", "clock"): ["paused"],
    ("read", "logic"): ["value"],
    ("move", "item"): [],
    ("move", "player"): [],
    ("move", "gas"): [],
    ("write", "battery"): [],
    ("write", "logic"): ["results"],
    ("write", "chip"): [],
}

FINAL = {"applied", "applied_with_differences", "applied_unchecked", "stopped", "gas_lost", "refused"}


def field(reply, key):
    """A reply's key where StationGod's catalogue declares it (catalogue/verbs/*.json: the variant's top-level keys).
    One documented exception: a call given a list of ids answers {results: [{index, ok, reference_id, payload}]}; a
    single result's payload is read there, and said so on stderr (test_test_worlds checks every read against the
    catalogue)."""
    if not isinstance(reply, dict):
        return None
    if key in reply:
        return reply[key]
    rows = reply.get("results")
    if isinstance(rows, list) and len(rows) == 1 and isinstance(rows[0], dict) and key in rows[0]:
        print(f"NOTE {key} read from results[0] (a list reply)", file=sys.stderr, flush=True)
        return rows[0][key]
    return None


def trace(verb, what, options, reply, limit=2500):
    """One line per call on stderr (the rig's build.log): what was asked and the start of the reply."""
    try:
        asked = json.dumps(options, default=str)[:300]
        said = json.dumps(reply, default=str)[:limit]
        print(f"CALL {verb} {what or ''} {asked}", file=sys.stderr)
        print(f"  -> {said}", file=sys.stderr, flush=True)
    except Exception:  # noqa: BLE001 - tracing never stops a build
        pass


class Refused(Exception):
    def __init__(self, what, reply):
        super().__init__(f"{what}: {reply}")
        self.reply = reply


class Builder:
    """The verbs a world build uses, each checked: a refusal or a report that is not ready stops the build."""

    def __init__(self, game, sleep=time.sleep, poll_s=1.0, job_timeout_s=300.0, trace_calls=False):
        self.game = game
        self.sleep = sleep
        self.poll_s = poll_s
        self.job_timeout_s = job_timeout_s
        self.log = []
        self.trace = trace_calls

    def do(self, verb, what=None, **options):
        reply = self.game(verb, what, **options)
        self.log.append((verb, what, options, reply))
        if self.trace:
            trace(verb, what, options, reply)
        if isinstance(reply, dict) and reply.get("error"):
            raise Refused(f"{verb} {what or ''}".strip(), reply["error"])
        return reply

    # ---- Jobs ----

    def finish(self, reply, what):
        """Waits for a held-tick job to end; it must end applied."""
        end = time.monotonic() + self.job_timeout_s
        while reply.get("status") not in FINAL:
            job = reply.get("job_id")
            if job is None:
                raise Refused(what, {"message": "no job id and no final status", "reply": reply})
            if time.monotonic() > end:
                raise Refused(what, {"message": f"job {job} not done after {self.job_timeout_s} s", "reply": reply})
            self.sleep(self.poll_s)
            reply = self.do("inspect", "job", id=job)
        if reply["status"] != "applied":
            raise Refused(what, {"message": f"job ended {reply['status']}", "reply": reply})
        return reply

    @staticmethod
    def placed_ids(job):
        """The new things a job made, [(id, prefab)], from wherever the reply carries them."""
        for holder in (job, job.get("result") or {}, job.get("log") or {}):
            rows = holder.get("placed")
            if isinstance(rows, list):
                return [(str(r["reference_id"]), r.get("prefab_name")) if isinstance(r, dict) else (str(r), None)
                        for r in rows]
        return []

    def build_structures(self, placements):
        """Places structures for free, 64 to a job; every job is reported before any is built, so a refusal changes
        nothing. Returns [(id, prefab)] in placement order."""
        parts = [placements[i:i + 64] for i in range(0, len(placements), 64)]
        for part in parts:
            report = self.do("build", "structure", placements=part, free=True)
            if not field(report, "ready"):
                raise Refused("build structure", {"problems": report.get("problems"), "first": part[0]})
        made = []
        for part in parts:
            job = self.finish(self.do("build", "structure", placements=part, free=True, confirm=True),
                              "build structure")
            made += self.placed_ids(job)
        return made

    def cable(self, start, end, grade="heavy"):
        """A cable from one device's port to another's, routed by plan route and built for free."""
        plan = self.do("plan", "route", kind="cable", grade=grade, start=start, end=end, summarize=True)
        if not field(plan, "found"):
            raise Refused("plan route", {"failure": field(plan, "failure"), "start": start, "end": end})
        args = dict(field(plan, "build_arguments") or {})
        args.pop("what", None)
        args.pop("materials", None)
        report = self.do("build", "cable", free=True, **args)
        if not field(report, "ready"):
            raise Refused("build cable", {"problems": report.get("problems")})
        return self.finish(self.do("build", "cable", free=True, confirm=True, **args), "build cable")

    def items(self, prefab, quantity=1, to=None, at=None, slot=None):
        """Makes items (a cheat, allowed in a creative world); returns the new ids."""
        options = {"prefab": prefab, "quantity": quantity, "confirm": True}
        if to is not None:
            options["to"] = str(to)
        else:
            options["at"] = at
        if slot is not None:
            options["slot"] = slot
        reply = self.do("build", "item", **options)
        return [str(r["reference_id"]) if isinstance(r, dict) else str(r) for r in field(reply, "placed") or []]

    # ---- Reads ----

    def prefab_row(self, prefab):
        """inspect prefab's row for one prefab (cached)."""
        cache = self.__dict__.setdefault("_prefab_rows", {})
        if prefab not in cache:
            reply = self.do("inspect", "prefab", prefab=prefab)
            cache[prefab] = (field(reply, "prefabs") or [reply])[0]
        return cache[prefab]

    def prefab_box(self, prefab):
        return self.prefab_row(prefab).get("render_box")

    def place(self, prefab, at, label):
        """A placement row. A face-mount device (inspect prefab's placement face_mount: a logic memory, a circuit
        housing, a logic switch, an active vent) needs support behind it, so it is mounted on the floor and its back
        rests on the frame below. The game's rule: SmallGrid.CanMountOnWall takes the support from the grid cell
        behind the device (rearGrid = position - forward * GridSize / 2, SmallGrid.GetSupport) and refuses
        InvalidMissingSupport without one; mounted on the floor, forward is up and that cell is the floor's frame.
        (The 11-02 18:08 sg-gate run was refused 'Placement requires support' for each one standing in the air.)
        Grid pieces (the battery, silos, locker, tray: small_grid but placement grid) stand as they are."""
        row = {"prefab": prefab, "at": at, "label": label}
        if self.prefab_row(prefab).get("placement") == "face_mount":
            row["orient"] = {"mount": "floor"}
        return row

    def missing_prefabs(self, names, structures=()):
        """The prefab names this game does not know, and the structures it knows but no kit builds, so a build stops
        before it changes anything (StructureTankSmall exists but only its in-line variants build)."""
        missing = []
        for name in names:
            reply = self.game("inspect", "prefab", prefab=name)
            if reply.get("error"):
                missing.append(name)
            elif name in structures and field(reply, "buildable") is False:
                missing.append(name + " (not buildable)")
        return missing

    def ports(self, thing_id):
        reply = self.do("inspect", "connections", id=str(thing_id))
        return field(reply, "ends") or []

    def network_of(self, thing_id, port):
        """The network id on one of a device's ports, read now (joining pieces can change it)."""
        row = next((e for e in self.ports(thing_id) if e.get("index") == port), None)
        network = (row or {}).get("network") or {}
        if not network.get("id"):
            raise Refused("network", {"id": thing_id, "port": port, "message": "no network on this port"})
        return str(network["id"])

    def has_power_port(self, thing_id):
        return any("power" in str(e.get("type_name") or e.get("type")).lower() for e in self.ports(thing_id))

    def power_port(self, thing_id, prefer="output"):
        """The index of a device's power port, the one whose role names `prefer` when there are several."""
        seen = self.ports(thing_id)
        ends = [e for e in seen if "power" in str(e.get("type_name") or e.get("type")).lower()]
        if not ends:
            raise Refused("power port", {"id": thing_id, "message": "no power port", "ends": seen})
        for e in ends:
            if prefer and prefer in str(e.get("role_name") or e.get("role") or "").lower():
                return e["index"]
        return ends[0]["index"]

    def switch_on(self, thing_ids, required=False):
        """Writes On 1 to each device. A device with no On is skipped with a note unless required; returns the ids
        switched on."""
        reply = self.do("write", "logic", id=[str(i) for i in thing_ids], logic="On", value=1, confirm=True)
        rows = field(reply, "results") or []
        on, skipped = [], []
        for i, row in zip(thing_ids, rows):
            (on if row.get("ok") else skipped).append((str(i), row.get("error")))
        if skipped and required:
            raise Refused("switch on", {"failed": skipped})
        for i, err in skipped:
            print(f"NOTE {i} has no On: {err}", file=sys.stderr, flush=True)
        return [i for i, _e in on]

    def wait_powered(self, thing_id, timeout_s=60.0):
        """Waits until a device reads Power 1 (its network powers it); refuses with its ends when it never does."""
        end = time.monotonic() + timeout_s
        while True:
            if field(self.do("read", "logic", id=str(thing_id), logic="Power"), "value") == 1:
                return
            if time.monotonic() > end:
                raise Refused("unpowered", {"id": thing_id, "seconds": timeout_s, "ends": self.ports(thing_id)})
            self.sleep(2.0)

    def players(self):
        reply = self.do("find", "thing", runtime_type="Human", limit=8)
        return field(reply, "things") or []

    def label_ids(self, label):
        return [str(t["reference_id"]) for t in self.labelled(label)]

    def labelled(self, label):
        """Things whose name is exactly `label` (find thing's name matches any part of a name, any case)."""
        # name matches any part of a name: "TW door" also finds door0..door6, door_switch and door_chip_housing.
        rows = field(self.do("find", "thing", name=label, limit=64), "things") or []
        return [t for t in rows if label in (t.get("custom_name"), t.get("display_name"))]

    def role_ids(self, prefab_of):
        """{role: id} for structures placed with label "TW <role>": each found by its label and checked to be the
        prefab it was placed as. build's `placed` is a list of ids with no prefab, so its order is not relied on."""
        ids = {}
        for role, prefab in prefab_of.items():
            rows = self.labelled(f"TW {role}")
            if len(rows) != 1 or rows[0].get("prefab_name") != prefab:
                raise Refused("role", {"role": role, "label": f"TW {role}", "prefab": prefab,
                                       "found": [(t.get("reference_id"), t.get("prefab_name")) for t in rows]})
            ids[role] = str(rows[0]["reference_id"])
        return ids

    def console(self, command):
        return self.do("server", "command", command=command, confirm=True)


def pos(row):
    p = row.get("position") or {}
    return [p.get("x"), p.get("y"), p.get("z")] if isinstance(p, dict) else list(p)


def silo_count(b, silo_id):
    """A silo's entry count (inspect contents), or None when the reply does not carry one."""
    reply = b.do("inspect", "contents", id=silo_id, limit=1)
    return (field(reply, "silo") or {}).get("count")


def player_name(row):
    return row.get("custom_name") or row.get("name") or row.get("display_name")


def one_player(b):
    people = b.players()
    if not people:
        raise Refused("player", {"message": "no player character in the world: build on a client that started "
                                            "the world (-newgame), or join the rig's Steam-off client once (a "
                                            "dedicated server makes none)"})
    return people[0]


# ---- Landing ---------------------------------------------------------------------------------------------------------
# P51: no test plays the landing, so a world is stored only once it has finished: the lander down, the player out of
# the seat and standing. World builders alone start from -newgame; everything else loads a stored world.

LANDER = "LanderCapsuleSmall"
POLL_S = 2.0
STILL_M = 0.05                  # a position this close to the last read has not moved
STILL_POLLS = 8                 # that many reads in a row (16 s)
LAND_POLLS = 210                # 7 minutes for the lander to come down
STAND_POLLS = 60                # 2 minutes for a player or a lander already down
GROUND_M = 2.0                  # the player's feet within this of the ground, in the world and in the save
LIFT_STEPS = (0, 4, 8, 12)      # a floor the terrain refuses goes this much higher, not through a looser rule


def lander_at(b):
    rows = field(b.do("find", "thing", prefab=LANDER, limit=1), "things") or []
    if not rows:
        raise Refused("lander", {"message": f"no {LANDER} in this world"})
    return pos(rows[0])


def wait_still(b, read, what, polls=LAND_POLLS):
    """Reads a position until it has held for STILL_POLLS reads; returns it. Refuses when it never does."""
    last, still = None, 0
    for _ in range(polls):
        now = read()
        same = last is not None and all(abs(a - c) <= STILL_M for a, c in zip(now, last))
        still = still + 1 if same else 0
        if still >= STILL_POLLS:
            return now
        last = now
        b.sleep(POLL_S)
    raise Refused("settle", {"message": f"{what} never settled in {polls * POLL_S:.0f} s", "last": last})


def seated(row):
    """In a seat, bed or capsule: find thing's held_in names the holder."""
    return bool(row.get("held_in"))


def out_on_the_ground(b):
    """Waits for the lander, takes the player out of the seat onto clear ground and waits for them to stand. Reads
    the player's state first, so it also works when they are already out. Returns (lander, person, feet)."""
    lander = wait_still(b, lambda: lander_at(b), "the lander")
    person = one_player(b)
    if seated(person):
        lx, ly, lz = lander
        b.do("move", "player", player=player_name(person), at=[lx + 10, ly, lz + 10], safe_ground=True, confirm=True)
    feet = wait_still(b, lambda: pos(one_player(b)), "the player", STAND_POLLS)
    person = one_player(b)
    if seated(person):
        raise Refused("seat", {"message": "the player is still in a seat", "held_in": person.get("held_in")})
    return lander, person, feet


def floor_site(b, make_site, lift, site=None):
    """Lays the frame floor `lift` metres up; if the report refuses it (terrain in the way) one step higher."""
    if site is not None:
        b.build_structures([{"prefab": "StructureFrame", "at": c} for c in site.frames()])
        return site
    for n, step_up in enumerate(LIFT_STEPS):
        site = make_site(lift + step_up)
        try:
            b.build_structures([{"prefab": "StructureFrame", "at": c} for c in site.frames()])
            return site
        except Refused as refused:
            if n == len(LIFT_STEPS) - 1 or "problems" not in refused.reply:
                raise
            print(f"NOTE the floor at +{lift + step_up} m was refused; going higher", file=sys.stderr, flush=True)
    return site


def landed_record(b, lander, ground_y, ground):
    """The last read before saving: the lander where it settled, the player out of the seat, standing at ground_y.
    Refuses anything else, so a mid-air or seated world is never saved as done. The store gate reads it back."""
    still = wait_still(b, lambda: lander_at(b), "the lander", STAND_POLLS)
    if abs(still[1] - lander[1]) > STILL_M * 2:
        raise Refused("lander", {"message": "the lander moved after settling", "was": lander, "now": still})
    feet = wait_still(b, lambda: pos(one_player(b)), "the player", STAND_POLLS)
    person = one_player(b)
    if seated(person):
        raise Refused("seat", {"message": "the player is in a seat", "held_in": person.get("held_in")})
    if abs(feet[1] - ground_y) > GROUND_M:
        raise Refused("ground", {"message": f"the player's feet are not within {GROUND_M} m of the ground",
                                 "feet": feet, "ground_y": ground_y, "ground": ground})
    return {"lander_y": still[1], "lander": still, "ground_y": ground_y, "ground": ground,
            "player": {"id": str(person["reference_id"]), "position": feet,
                       "facing": (person.get("rotation") or {}).get("facing")}}


# ---- Worlds --------------------------------------------------------------------------------------------------------

def minimal(b, difficulty="Normal"):
    """sg-minimal: a landed world and its one player on the ground beside the lander, nothing built."""
    lander, person, feet = out_on_the_ground(b)
    landed = landed_record(b, lander, feet[1], "terrain")
    b.console(f"difficulty {difficulty}")
    return {"fixtures": {"player": {"id": str(person["reference_id"]), "prefab": person.get("prefab_name"),
                                    "position": feet, "note": "out of the seat, on the ground"}},
            "landed": landed,
            "site": "none: the landed world, the player on the ground clear of the lander",
            "built": {"difficulty": difficulty}}


SILO_PREFABS = {
    "battery": "StructureBatteryLarge",
    "silo_ore": "StructureSDBSilo",
    "silo_room": "StructureSDBSilo",
    "silo_full": "StructureSDBSilo",
    "locker": "StructureStorageLocker",
    "tray": "StructureHydroponicsTray",
}
SILO_ITEMS = ["ItemIronOre", "ItemHardMiningBackPack", "ItemHardSuit", "ItemCookedPumpkin", "ItemCannedMushroom",
              "SeedBag_Pumpkin"]
ORE_STACKS, ORE_STACK = 10, 100         # 10 pooled stacks of iron ore (it stacks to 100): 10 silo entries
FOOD_STACKS, FOOD_QUANTITY = 2, 4       # fresh food for the food-rot check: 2 loose stacks of 4 ItemCookedPumpkin
SILO_CAPACITY = 600                     # Silo.cs: at most 600 entries (IsFull); one stack is one entry
FILL_CHUNK = 50                         # stacks made and deposited per call while filling silo_full


def silo(b, lift=4.0, difficulty="Normal", site=None):
    """sg-silo: three powered SDB Silos (one holding 10 pooled stacks of ore, one empty, one full: 600 stacks of
    ore, so the next deposit is refused silo_full), an empty locker, a mining
    backpack holding ore and two empty ones, canned and fresh food loose on the floor, a pumpkin planted in a tray, a
    hard suit carried by the player, and the player on clear floor beside them. All on a floor of frames `lift`
    metres above the ground, laid after the lander is down."""
    from twlib import Site, bay_cells
    missing = b.missing_prefabs(sorted(set(SILO_PREFABS.values()) | set(SILO_ITEMS) | {"StructureFrame"}))
    if missing:
        raise Refused("prefabs", {"message": "this game does not know these prefabs", "missing": missing})
    lander, person, (px, py, pz) = out_on_the_ground(b)
    bays = [(role, bay_cells(b.prefab_box(prefab))) for role, prefab in SILO_PREFABS.items()]
    site = floor_site(b, lambda up: Site(px + 4, py + up, pz + 4, bays), lift, site)
    spots = site.spots()
    silos = ("silo_ore", "silo_room", "silo_full")
    placements = [b.place(SILO_PREFABS[role], spots[role], f"TW {role}") for role, _cells in bays]
    b.build_structures(placements)
    ids = b.role_ids({role: SILO_PREFABS[role] for role, _c in bays})

    # Power: the battery full and switched on (Battery.GetGeneratedPower gives nothing while OnOff is off, and a
    # placed battery starts off: the 11-02 02:08 rig run's silos never powered), a heavy cable from its output to each
    # silo's power port.
    b.do("write", "battery", id=[ids["battery"]], ratio=1.0, confirm=True)
    b.switch_on([ids["battery"]], required=True)
    out = b.power_port(ids["battery"], prefer="output")
    for role in silos:
        b.cable({"id": ids["battery"], "port": out}, {"id": ids[role], "port": b.power_port(ids[role], prefer="")})
    b.switch_on([ids[role] for role in silos], required=True)
    # A new cable network powers its devices on the game's next power ticks, not at once: a deposit straight after
    # the cables was refused silo_unpowered (sg-silo rig run, 11-02 02:03).
    for role in silos:
        b.wait_powered(ids[role])

    # The ore silo: pooled stacks made loose beside it, then deposited by id (a deposit by id acts at once).
    drop = site.clear(i=0)
    ore = b.items("ItemIronOre", ORE_STACKS * ORE_STACK, at=drop)
    if len(ore) != ORE_STACKS:
        raise Refused("ore stacks", {"message": f"{ORE_STACKS * ORE_STACK} iron ore came as {len(ore)} stacks, not "
                                                f"{ORE_STACKS}: the stack size is not {ORE_STACK}"})
    b.do("move", "item", to=ids["silo_ore"], id=ore)

    # The full silo: 600 stacks of ore, made and deposited a chunk at a time, then counted.
    for _ in range(SILO_CAPACITY // FILL_CHUNK):
        chunk = b.items("ItemIronOre", FILL_CHUNK * ORE_STACK, at=drop)
        b.do("move", "item", to=ids["silo_full"], id=chunk)
    held = silo_count(b, ids["silo_full"])
    if held is not None and held != SILO_CAPACITY:
        raise Refused("silo_full", {"message": f"the full silo holds {held} entries, not {SILO_CAPACITY}"})

    # A backpack with contents, two spare empty ones, canned and fresh food: loose on the floor's far clear row.
    far = site.depth - 1
    pack = b.items("ItemHardMiningBackPack", 1, at=site.clear(i=site.width - 1, k=far))[0]
    b.items("ItemIronOre", 2 * ORE_STACK, to=pack)
    spare = b.items("ItemHardMiningBackPack", 2, at=site.clear(i=site.width - 2, k=far))
    canned = b.items("ItemCannedMushroom", 2, at=site.clear(i=site.width - 3, k=far))
    fresh = []
    for n in range(FOOD_STACKS):
        fresh += b.items("ItemCookedPumpkin", FOOD_QUANTITY, at=site.clear(i=site.width - 4 - n, k=far))

    # A pumpkin planted in the tray: a seed bag moved into its plant slot plants it, as by hand.
    seed = b.items("SeedBag_Pumpkin", 1, at=site.clear(i=site.width - 1, k=far - 1))[0]
    b.do("move", "item", id=seed, quantity=1, to=ids["tray"], slot="auto")

    # The player: a hard suit carried (no ore slot: the no_room check), then on the clear floor.
    suit = b.items("ItemHardSuit", 1, to=person["reference_id"])
    stand = site.clear(i=1)
    b.do("move", "player", player=player_name(person), at=stand, confirm=True)
    landed = landed_record(b, lander, site.floor_y + 1, "the frame floor's top")

    b.console(f"difficulty {difficulty}")
    fixtures = {role: {"id": ids[role], "prefab": SILO_PREFABS[role], "position": spots[role]} for role, _c in bays}
    fixtures["silo_ore"]["note"] = f"{ORE_STACKS} stacks of {ORE_STACK} ItemIronOre, one entry each"
    fixtures["silo_room"]["note"] = "empty, powered and on"
    fixtures["silo_full"]["note"] = (f"full: {SILO_CAPACITY} stacks of {ORE_STACK} ItemIronOre, one entry each; the "
                                     "next deposit is refused silo_full")
    fixtures["locker"]["note"] = "empty"
    fixtures["battery"]["note"] = "charged full; heavy cable to each silo"
    fixtures["tray"]["note"] = "a pumpkin planted (SeedBag_Pumpkin moved into its plant slot); no water or light"
    fixtures.update({
        "player": {"id": str(person["reference_id"]), "prefab": person.get("prefab_name"), "position": stand,
                   "note": "on clear floor; carries an ItemHardSuit"},
        "suit": {"id": suit[0] if suit else None, "prefab": "ItemHardSuit", "note": "carried by the player"},
        "backpack_full": {"id": pack, "prefab": "ItemHardMiningBackPack", "note": f"holds {2 * ORE_STACK} iron ore"},
        "backpacks_spare": {"id": spare, "prefab": "ItemHardMiningBackPack", "note": "empty, loose"},
        "food_canned": {"id": canned, "prefab": "ItemCannedMushroom", "note": "loose"},
        "food_fresh": {"id": fresh, "prefab": "ItemCookedPumpkin",
                       "note": f"{FOOD_STACKS} stacks of {FOOD_QUANTITY}, loose, rots"},
        "ground": {"id": None, "prefab": None, "position": site.clear(i=3),
                   "note": "clear floor rows for ground drops (z beyond the bays)"},
    })
    return {"fixtures": fixtures, "site": site.describe(), "landed": landed,
            "built": {"difficulty": f"built creative, then `difficulty {difficulty}`",
                      "power": "StructureBatteryLarge charged full, heavy cable to each of the three silos; no atmosphere (the "
                               "silo needs none: on and powered is enough)"}}


GATE_PREFABS = {
    "battery": "StructureBatteryLarge",
    "mem1": "StructureLogicMemory",              # a free Setting: stores any number as written
    "mem2": "StructureLogicMemory",
    "door_chip_housing": "StructureCircuitHousing",
    "door_switch": "StructureLogicSwitch",
    "vent": "StructureActiveVent",
    # The small tank builds only as its in-line variants, and an in-line tank is a Pipe (InLineTank : Pipe): it joins
    # the network it lies on. So two: vent_tank ends the vent's pipe (vent_net), o2_tank stands alone holding the
    # oxygen, a network of its own, so plant_oxygen's move gas o2_source -> vent_net moves gas between two networks.
    "vent_tank": "StructureTankSmallInLine",
    "o2_tank": "StructureTankSmallInLine",
}
# The Open cases' door. On Lunar both sides are vacuum, so its Open never moves air and never draws a lint.
GATE_DOORS = ("StructureGlassDoor",)
# Structure prefabs a rig's build structure report accepted as buildable (no invalid_prefab), with where it said so.
# test_test_worlds holds every structure prefab in the layouts to this list, so a prefab no kit builds fails offline.
KNOWN_BUILDABLE = {
    "StructureFrame": "sg-silo rig run 11-02 02:10 (built)",
    "StructureBatteryLarge": "sg-silo rig run 11-02 02:10 (built)",
    "StructureSDBSilo": "sg-silo rig run 11-02 02:10 (built)",
    "StructureStorageLocker": "sg-silo rig run 11-02 02:10 (built)",
    "StructureHydroponicsTray": "sg-silo rig run 11-02 02:10 (built)",
    "StructureLogicMemory": "sg-gate report 11-02 18:08 (placement problem only)",
    "StructureCircuitHousing": "sg-gate report 11-02 18:08 (placement problem only)",
    "StructureLogicSwitch": "sg-gate report 11-02 18:08 (placement problem only)",
    "StructureActiveVent": "sg-gate report 11-02 18:08 (placement problem only)",
    "StructureGlassDoor": "sg-gate report 11-02 18:08 (accepted)",
    "StructureLargeHangerDoor": "sg-gate report 11-02 18:08 (accepted)",
    "StructureTankSmallInLine": "sg-gate report 11-02 18:08 (named as a buildable variant of StructureTankSmall)",
}
HANGAR_DOOR = "StructureLargeHangerDoor"
HANGAR_DOORS = 7
DOOR_CHIP = """# sg-gate: the hangar doors follow the switch (tests/gate/cases.py: driven_by)
alias Switch d0
define Doors HASH("StructureLargeHangerDoor")
loop:
l r0 Switch Setting
sb Doors Open r0
yield
j loop
"""


def gate(b, lift=4.0, difficulty="Creative", site=None):
    """sg-gate: the gate suite's fixtures (tests/gate/cases.py FIX), vanilla only (StationGod's gate passes on a game
    with StationGod alone): a powered door (Open), two logic memories (Setting), seven hangar doors a chip drives from
    a switch, and an outdoor vent on a pipe network with a tank to take oxidiser from. Stays creative: the suite moves
    gas, a cheat."""
    from twlib import Site, bay_cells
    door_prefab = next((d for d in GATE_DOORS if not b.missing_prefabs([d])), None)
    names = sorted(set(GATE_PREFABS.values()) | {HANGAR_DOOR, "StructureFrame", "ItemIntegratedCircuit10"})
    structures = set(GATE_PREFABS.values()) | {HANGAR_DOOR, "StructureFrame"}
    missing = b.missing_prefabs(names, structures=structures) + ([] if door_prefab else list(GATE_DOORS))
    if missing:
        raise Refused("prefabs", {"message": "this game does not know these prefabs", "missing": missing})
    lander, person, (px, py, pz) = out_on_the_ground(b)
    prefab_of = dict(GATE_PREFABS, door=door_prefab, **{f"door{n}": HANGAR_DOOR for n in range(HANGAR_DOORS)})
    roles = list(prefab_of)
    boxes = {p: bay_cells(b.prefab_box(p)) for p in set(prefab_of.values())}
    bays = [(r, boxes[prefab_of[r]]) for r in roles]
    site = floor_site(b, lambda up: Site(px + 4, py + up, pz + 4, bays), lift, site)
    spots = site.spots()
    b.build_structures([b.place(prefab_of[r], spots[r], f"TW {r}") for r, _c in bays])
    ids = b.role_ids({r: prefab_of[r] for r, _c in bays})

    # The chip: an IC10 in the housing drives the hangar doors from the switch.
    chip = b.items("ItemIntegratedCircuit10", 1, to=ids["door_chip_housing"])
    b.do("write", "chip", id=ids["door_chip_housing"], code=DOOR_CHIP, pins={"d0": ids["door_switch"]})
    # Setting on both memories; the door shut; the vent off, on a pipe to the tank, with oxygen in the tank.
    b.do("write", "logic", id=[ids["mem1"], ids["mem2"]], logic="Setting", value=1000, confirm=True)
    b.do("write", "logic", id=ids["vent"], logic="On", value=0, confirm=True)
    # Power: the battery full and on, a heavy cable from its output to every device with a power port, each switched
    # on where it has an On.
    b.do("write", "battery", id=[ids["battery"]], ratio=1.0, confirm=True)
    b.switch_on([ids["battery"]], required=True)
    out = b.power_port(ids["battery"], prefer="output")
    # A trunk, not a star: the first device's cable leaves the battery's one output cell, and each further device
    # drops to the nearest piece of that network. One run per device from that cell ran out of ways out of it (the
    # 11-02 18:2x run: route costs 36, 70, 93, 265, then no_route to the third hangar door).
    powered = []
    for r in ["door", "mem1", "mem2", "door_chip_housing"] + [f"door{n}" for n in range(HANGAR_DOORS)]:
        if not b.has_power_port(ids[r]):
            print(f"NOTE {r} ({prefab_of[r]}) has no power port: not cabled", file=sys.stderr, flush=True)
            continue
        port = {"id": ids[r], "port": b.power_port(ids[r], prefer="")}
        if powered:
            b.cable(port, {"network": b.network_of(ids["battery"], out)})
        else:
            b.cable({"id": ids["battery"], "port": out}, port)
        powered.append(r)
    b.switch_on([ids[r] for r in powered])
    b.do("write", "logic", id=ids["door"], logic="Open", value=0, confirm=True)
    pipe = b.do("plan", "route", kind="pipe", grade="gas", start={"id": ids["vent"]}, end={"id": ids["vent_tank"]},
                summarize=True)
    if not field(pipe, "found"):
        raise Refused("plan route", {"failure": field(pipe, "failure")})
    args = dict(field(pipe, "build_arguments") or {})
    args.pop("what", None)
    args.pop("materials", None)
    b.finish(b.do("build", "pipe", free=True, confirm=True, **args), "build pipe")
    b.do("move", "gas", source="create", to=ids["o2_tank"], gases=["Oxygen"], amount_mol=10, temperature_k=293.15,
         confirm=True)
    net = b.do("inspect", "connections", id=ids["vent"])
    vent_net = next((e.get("network") for e in field(net, "ends") or [] if e.get("network")), None)
    if isinstance(vent_net, dict):
        vent_net = vent_net.get("id")
    b.do("move", "player", player=player_name(person), at=site.clear(i=1), confirm=True)
    landed = landed_record(b, lander, site.floor_y + 1, "the frame floor's top")
    b.console(f"difficulty {difficulty}")

    fixtures = {r: {"id": ids[r], "prefab": prefab_of[r], "position": spots.get(r)} for r, _c in bays}
    fixtures["door"]["note"] = "a powered vanilla door in the open (vacuum both sides), Open 0"
    fixtures["mem1"]["note"] = fixtures["mem2"]["note"] = "a logic memory, Setting 1000"
    fixtures["doors"] = {"id": [ids[f"door{n}"] for n in range(HANGAR_DOORS)], "prefab": HANGAR_DOOR,
                         "note": "driven by door_chip from door_switch"}
    # driven_by names a chip by the device holding its program, so the suite's door_chip is the housing.
    fixtures["door_chip"] = {"id": ids["door_chip_housing"], "prefab": GATE_PREFABS["door_chip_housing"],
                             "note": "the IC housing running the door program (driven_by names it)"}
    fixtures["door_ic"] = {"id": chip[0] if chip else None, "prefab": "ItemIntegratedCircuit10",
                           "note": "the chip in door_chip's housing"}
    fixtures["vent_net"] = {"id": str(vent_net) if vent_net else None, "prefab": None, "note": "the vent's pipe network"}
    fixtures["o2_source"] = {"id": ids["o2_tank"], "prefab": GATE_PREFABS["o2_tank"],
                             "note": "10 mol oxygen in an in-line tank standing alone (its own network)"}
    run_at = site.clear(i=1)
    fixtures["pipe_run"] = {"id": "-", "prefab": None, "note": "two clear floor cells a pipe report may name",
                            "position": [[run_at[0], site.floor_y + 2, run_at[2]],
                                         [run_at[0] + 2, site.floor_y + 2, run_at[2]]]}
    return {"fixtures": fixtures, "site": site.describe(), "landed": landed,
            "built": {"difficulty": difficulty, "mods": "none (StationGod only)", "cabled": ", ".join(powered)}}


WORLDS = {"sg-minimal": minimal, "sg-silo": silo, "sg-gate": gate}

SPECS = {
    "sg-minimal": "A fresh Lunar world with one player and nothing else: the release load check, with and without "
                  "Lattice.",
    "sg-silo": "The SDB Silo checks (tools/silo-rig/silo_checks.py and the food-rot check): three powered silos: "
               "one with pooled ore stacks, one with room and one full (600 entries, silo_full), an empty locker, a backpack with contents, canned and "
               "fresh food, a pumpkin growing in a tray, and a player with clear floor for ground drops.",
    "sg-gate": "The gate suite's fixtures (tests/gate/cases.py FIX), vanilla only (no mod but StationGod): a powered "
               "door (Open), two logic memories (Setting), hangar doors driven by a chip, and an outdoor vent with "
               "oxidiser to hand.",
}
