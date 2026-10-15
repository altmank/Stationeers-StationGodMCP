"""Writes GameCollections.txt: the game's global collections, which only the mod's World module may read (cost design A1,
the cost gate's first source rule). Rerun on a game update:

    py -3.12 -I tests/StationGodMCP.Tests/CostRules/make_game_collections.py G:/Programming/Git/StationeersWorkspace/decompile

A global collection is a static field or property of the game, named All* or of a DensePool type, whose type is a
collection (a list, set, dictionary, pool, queue or array), plus the room lists reached through the RoomController.World
singleton. One line per entry: Owner.Member, then the decompile path and line it is declared at.
"""
import os
import re
import sys

COLLECTION = r"(?:List|HashSet|Dictionary|ConcurrentDictionary|SortedDictionary|DensePool|ConcurrentDensePool|Queue|" \
             r"IEnumerable|IReadOnlyList|ReadOnlyCollection|ICollection|IList|LinkedList|ThingPool)<[^=;{]*?>|\w+\[\]"
DECL = re.compile(r"^\s*public static (?:readonly )?(" + COLLECTION + r")\s+(\w+)\s*(?:=|;|\{)")
SINGLETON = [("RoomController.World.Rooms", "Assets.Scripts/RoomController.cs", "public List<Room> Rooms"),
             ("RoomController.World.RoomLookup", "Assets.Scripts/RoomController.cs", "public ConcurrentDictionary<Grid3, Room> RoomLookup")]


def main(root):
    found = {}
    for folder, _, files in os.walk(root):
        for name in files:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(folder, name)
            owner = name[:-3]
            with open(path, encoding="utf-8", errors="replace") as f:
                for number, line in enumerate(f, 1):
                    m = DECL.match(line)
                    if not m:
                        continue
                    kind, member = m.group(1), m.group(2)
                    if member.startswith("All") or "DensePool<" in kind:
                        rel = os.path.relpath(path, root).replace("\\", "/")
                        found.setdefault(owner + "." + member, "%s:%d" % (rel, number))
    for key, rel, text in SINGLETON:
        with open(os.path.join(root, rel), encoding="utf-8") as f:
            number = next(i for i, line in enumerate(f, 1) if text in line)
        found[key] = "%s:%d" % (rel, number)
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, "GameCollections.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("# The game's global collections (make_game_collections.py; decompile of Update 0.2.6428.27798).\n")
        f.write("# Only src/StationGodMCP.Mod/World/ may read them (CostSourceRulesTests).\n")
        for key in sorted(found):
            f.write("%s\t%s\n" % (key, found[key]))
    print(len(found), "collections")


if __name__ == "__main__":
    main(sys.argv[1])
