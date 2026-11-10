r"""The cost gate's larger world (sg-cost-large): a test world from the library plus a block of synthetic things.

    py -3.12 make_large_world.py [--world sg-silo] [--library <library>] [--out sg-cost-large]

The retarget of the scoreboard's instance/make_large_save.py, which read the pristine Vulcan2_4 copy (P36: no test
uses LU's base). The library is --library, else STATIONGOD_TEST_WORLDS. It reads <library>\<world>\<world>.save and writes <library>\<out>\<out>.save, with
copies of three ordinary things appended to world.xml's AllThings, each with a fresh ReferenceId above the save's
highest, on the 2 m grid's cell centres far from the world's own things (200 m past the largest x and z, 200 m above
the highest y, in the air):
  - FRAMES StructureFrame (a solid block of frames: no room, no network);
  - LIGHTS StructureWallLight (unpowered devices: Device.AllDevices grows);
  - ITEMS  ItemIronSheets stacks, each resting on one frame of a one-layer platform (loose items).
Nothing of the source world changes, so every question asked of it has the same answer in both; only the size grows.
The counts stay as the Vulcan-based world had them added (24,859 things), checked against the game's caps.
Then `build_world.py store`-style files: world.txt, README.md, fixtures.json (the source's fixtures, unchanged).
"""
import argparse
import json
import os
import re
import shutil
import sys
import tempfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import twlib  # noqa: E402

FRAMES = 16000          # block 40 x 40 x 10
LIGHTS = 2809           # 53 x 53
ITEMS = 3025            # 55 x 55 on a platform of as many frames

ROTATION = """<x>0</x>
        <y>0</y>
        <z>0</z>
        <w>1</w>
        <eulerAngles>
          <x>-0</x>
          <y>0</y>
          <z>0</z>
        </eulerAngles>"""
DAMAGE = "".join(f"<{n}>0</{n}>" for n in ("Brute", "Burn", "Oxygen", "Hydration", "Starvation", "Toxic",
                                           "Radiation", "Stun", "Decay"))
LIGHT_STATES = ("<States>" + "".join(
    f"<State><StateName>{n}</StateName><State>0</State></State>" for n in ("OnOff", "Lock", "Powered", "Access"))
    + "</States>")


def xyz(pos):
    x, y, z = pos
    return f"<x>{x}</x><y>{y}</y><z>{z}</z>"


def structure(ref, prefab, pos, states=""):
    return f"""    <ThingSaveData xsi:type="StructureSaveData">
      <ReferenceId>{ref}</ReferenceId>
      <PrefabName>{prefab}</PrefabName>
      <CustomName />
      <WorldPosition>{xyz(pos)}</WorldPosition>
      <WorldRotation>{ROTATION}</WorldRotation>
      {states}
      <IsCustomName>false</IsCustomName>
      <CustomColorIndex>-1</CustomColorIndex>
      <OwnerSteamId>0</OwnerSteamId>
      <Reagents />
      <Indestructable>false</Indestructable>
      <DamageState>{DAMAGE}</DamageState>
      <CurrentBuildState>{3 if prefab == "StructureFrame" else 0}</CurrentBuildState>
      <MothershipReferenceId>0</MothershipReferenceId>
      <HasSpawnedWreckage>false</HasSpawnedWreckage>
      <RegisteredWorldPosition>{xyz(pos)}</RegisteredWorldPosition>
      <RegisteredWorldRotation>{ROTATION}</RegisteredWorldRotation>
    </ThingSaveData>
"""


def item(ref, pos):
    zero = "<x>0</x><y>0</y><z>0</z>"
    return f"""    <ThingSaveData xsi:type="StackableSaveData">
      <ReferenceId>{ref}</ReferenceId>
      <PrefabName>ItemIronSheets</PrefabName>
      <CustomName />
      <WorldPosition>{xyz(pos)}</WorldPosition>
      <WorldRotation>{ROTATION}</WorldRotation>
      <States />
      <IsCustomName>false</IsCustomName>
      <CustomColorIndex>-1</CustomColorIndex>
      <OwnerSteamId>0</OwnerSteamId>
      <Reagents />
      <Indestructable>false</Indestructable>
      <DamageState>{DAMAGE}</DamageState>
      <ParentReferenceId>0</ParentReferenceId>
      <ParentSlotId>0</ParentSlotId>
      <Dragged>false</Dragged>
      <DragOffset>{zero}</DragOffset>
      <Velocity>{zero}</Velocity>
      <AngularVelocity>{zero}</AngularVelocity>
      <HealthCurrent>0</HealthCurrent>
      <Quantity>10</Quantity>
    </ThingSaveData>
"""


def origin(world):
    """Odd cell centres 200 m past the world's largest x and z, 200 m above its highest y."""
    pts = [tuple(float(v) for v in m) for m in re.findall(
        r"<WorldPosition>\s*<x>([^<]+)</x>\s*<y>([^<]+)</y>\s*<z>([^<]+)</z>", world)]
    if not pts:
        raise SystemExit("no positions in this world")
    return tuple(twlib.odd(max(p[i] for p in pts) + 200) for i in range(3))


def grow(world):
    """world.xml with the block added; returns (xml, facts)."""
    nl = "\r\n" if "\r\n" in world[:2000] else "\n"
    next_id = max(int(x) for x in re.findall(r"<ReferenceId>(\d+)</ReferenceId>", world)) + 1
    first_id = next_id
    ox, oy, oz = origin(world)
    out = []
    for layer in range(10):
        for i in range(40):
            for k in range(40):
                out.append(structure(next_id, "StructureFrame", (ox + 2 * i, oy + 2 * layer, oz + 2 * k)))
                next_id += 1
    lx = ox + 200
    for i in range(53):
        for k in range(53):
            out.append(structure(next_id, "StructureWallLight", (lx + 2 * i - 0.5, oy - 0.5, oz + 2 * k - 1),
                                 LIGHT_STATES))
            next_id += 1
    px = ox + 400
    for i in range(55):
        for k in range(55):
            out.append(structure(next_id, "StructureFrame", (px + 2 * i, oy, oz + 2 * k)))
            out.append(item(next_id + 1, (px + 2 * i, oy + 1.25, oz + 2 * k)))
            next_id += 2
    marker = "  </AllThings>"
    at = world.index(marker)
    grown = world[:at] + "".join(out).replace("\n", nl) + world[at:]
    size = twlib.counts(grown)
    if size["things"] > twlib.CAP_THINGS - 3000 or size["structures"] > twlib.CAP_STRUCTURES - 3000:
        raise SystemExit(f"too big for the game's caps (leave room for things it spawns): {size}")
    return grown, {"frames": FRAMES + ITEMS, "lights": LIGHTS, "items": ITEMS, "ids": [first_id, next_id - 1],
                   "origin": [ox, oy, oz]}


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--world", default="sg-silo")
    ap.add_argument("--library", help="the world library (default: STATIONGOD_TEST_WORLDS)")
    ap.add_argument("--out", default="sg-cost-large")
    a = ap.parse_args(argv)
    a.library = twlib.library_root(a.library)
    src = twlib.save_path(a.library, a.world)
    twlib.check_write(os.path.join(a.library, a.out), a.library)
    with zipfile.ZipFile(src) as z:
        parts = {i.filename: z.read(i.filename) for i in z.infolist()}
    world, added = grow(parts["world.xml"].decode("utf-8"))
    parts["world.xml"] = world.encode("utf-8")
    stage = tempfile.mkdtemp(prefix="sg-cost-large-")      # store() copies it into the library
    dst = os.path.join(stage, a.out)
    os.makedirs(dst)
    with zipfile.ZipFile(os.path.join(dst, a.out + ".save"), "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in parts.items():
            z.writestr(name, data)
    with open(os.path.join(a.library, a.world, "fixtures.json"), encoding="utf-8") as f:
        source = json.load(f)
    fixtures = source.get("fixtures", {})
    # The source's landed record: the block is far off, so the player and lander stand as stored (P51), and store's
    # gate holds this save to it too.
    manifest = {"fixtures": fixtures, "landed": source.get("landed"), "site": f"{a.world}'s site, unchanged; the block from {added['origin']}",
                "built": {"from": f"{a.world} (library)", "added": json.dumps(added),
                          "builder": "tools/test-worlds/make_large_world.py"}}
    spec = (f"The cost gate's larger world: {a.world} plus {FRAMES + ITEMS} frames, {LIGHTS} unpowered wall lights "
            f"and {ITEMS} loose iron sheet stacks far from its fixtures. Load it as {a.out}.")
    try:
        size = twlib.store(a.out, dst, a.library, manifest, spec)
    finally:
        shutil.rmtree(stage, ignore_errors=True)
    print(json.dumps(dict(added, **{"things": size["things"], "structures": size["structures"]})))
    return 0


if __name__ == "__main__":
    sys.exit(main())
