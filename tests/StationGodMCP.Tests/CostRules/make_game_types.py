"""Writes GameTypes.txt: the names of the game's types, which no reply view may hold (cost design A5, the cost gate's
fourth source rule: a view holds copied values only, so the connection's writer thread can write it). Rerun on a game
update:

    py -3.12 -I tests/StationGodMCP.Tests/CostRules/make_game_types.py G:/Programming/Git/StationeersWorkspace/decompile

Every class, struct, interface and enum the decompile declares, and the engine types the game's code passes around
(UnityEngine's, which the decompile does not hold). One name per line, sorted.
"""
import os
import re
import sys

DECL = re.compile(r"^\s*(?:(?:public|internal|private|protected|sealed|abstract|static|partial|readonly|unsafe|new)\s+)*"
                  r"(?:class|struct|interface|enum)\s+(\w+)")
ENGINE = ["Vector2", "Vector3", "Vector4", "Quaternion", "Matrix4x4", "Transform", "GameObject", "Component",
          "MonoBehaviour", "Object", "Ray", "RaycastHit", "Bounds", "Collider", "Rigidbody", "Camera", "Color",
          "Color32", "Material", "Mesh", "Renderer", "Texture", "Texture2D", "Sprite"]


def main():
    root = sys.argv[1]
    names = set(ENGINE)
    for folder, _, files in os.walk(root):
        for name in files:
            if not name.endswith(".cs"):
                continue
            with open(os.path.join(folder, name), encoding="utf-8", errors="replace") as source:
                for line in source:
                    match = DECL.match(line)
                    if match:
                        names.add(match.group(1))
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, "GameTypes.txt"), "w", encoding="utf-8", newline="\n") as out:
        out.write("# The game's type names (make_game_types.py over the decompile, and engine types).\n")
        for name in sorted(names):
            out.write(name + "\n")
    print(len(names), "types")


if __name__ == "__main__":
    main()
