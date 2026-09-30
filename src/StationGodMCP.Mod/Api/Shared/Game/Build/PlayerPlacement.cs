#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Localization2;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects.Structures;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// The one rule every StationGod path that creates a structure asks: would a player's placement cursor build this
/// prefab here, turned so, as the world stands. The game's server builds whatever it is told
/// (Constructor.SpawnConstruct checks nothing); only the cursor refuses, through each class's CanConstruct (a frame
/// below for batteries, machines and dishes, a wall or floor behind for vents, lights and consoles, a straight pipe
/// or cable to mount on, terrain, outside, collisions) and CanMountOnWall for a face-mounted piece. Three forms:
/// <list type="bullet">
/// <item>Refusal: a new placement (place_structure, find_spot): the cursor check (a rocket's cells and hull
/// included: SmallGrid.CanConstruct, the fuselage, engine and umbilical classes' own), a small-grid slot already
/// taken.</item>
/// <item>PieceRefusal: a cable, pipe or chute piece with things treated as gone (place_cables, place_pipes,
/// place_chutes, their undo restores): PlacementCheck, the game's piece rules made here because the cursor cannot
/// ignore what a run replaces.</item>
/// <item>Replacing / AsItStands: a placement standing in for existing things (replace_walls, replace_frames;
/// check_replaceable and lint_layout's not_replaceable: could this thing be placed again where it stands). A refusal
/// that names only a replaced thing is theirs (PlayerPlacementRule), and the neighbours are checked again without
/// them.</item>
/// </list>
/// </summary>
internal static class PlayerPlacement
{
    /// <summary>Why a player could not place the prefab there as things stand; null when they could.</summary>
    internal static string? Refusal(Structure prefab, Structure cursor, Vector3 position, Quaternion rotation)
    {
        HashSet<long> none = new HashSet<long>();
        string? refusal = CursorCheck.Refusal(cursor, position, rotation, none);
        if (refusal != null || !(prefab is SmallGrid piece))
        {
            return refusal;
        }

        return CursorCheck.SlotTaken(piece, CursorCheck.SmallCells(prefab, position, rotation), none);
    }

    /// <summary>Why a player could not place the cable, pipe or chute piece there once the things in gone are gone.</summary>
    internal static string? PieceRefusal(Structure prefab, Vector3 position, Quaternion rotation,
        HashSet<long> gone) =>
        PlacementCheck.Refusal(prefab, position, rotation, gone);

    /// <summary>
    /// The verdict on a placement that stands in for the replaced things: whether a player could place the prefab
    /// there once they are gone, every other neighbour present. It must stand where the cursor snaps it, at a quarter
    /// turn the cursor gives the prefab (Stance); then the cursor's CanConstruct and, for a face-mounted piece,
    /// CanMountOnWall are both asked (PlayerPlacementRule.Judge). Not checked without a placement cursor.
    /// </summary>
    internal static PlacementVerdict Replacing(Structure prefab, Structure? cursor, Vector3 position,
        Quaternion rotation, IReadOnlyCollection<Structure> replaced)
    {
        if (cursor == null)
        {
            return PlacementVerdict.Unchecked($"the game has no placement cursor for {prefab.PrefabName} (it makes " +
                                              "one for each structure prefab when its inventory manager starts, a " +
                                              "dedicated server too; a prefab registered after that has none)");
        }

        PlacementVerdict? stance = Stance(prefab, cursor, position, rotation);
        if (stance != null)
        {
            return stance;
        }

        HashSet<long> ids = new HashSet<long>();
        foreach (Structure structure in replaced)
        {
            ids.Add(structure.ReferenceId);
        }

        List<string> texts = TextsNaming(replaced);
        return CursorCheck.At(cursor, position, rotation, () =>
        {
            GameCheck construct = Construct(prefab, cursor, position, rotation, replaced, ids, texts);
            string? mount = CursorCheck.MountRefusal(cursor);
            GameCheck mounting = new GameCheck(mount,
                mount != null && PlayerPlacementRule.BlamesReplaced(mount, texts));
            return PlayerPlacementRule.Judge(new[] { construct, mounting },
                () => Neighbours(prefab, cursor, position, rotation, ids));
        });
    }

    /// <summary>
    /// Whether a player could place the thing again where it stands, with its neighbours present: some kit builds it,
    /// and Replacing allows it with the thing gone.
    /// </summary>
    internal static PlacementVerdict AsItStands(Structure thing, BuildCatalogue catalogue)
    {
        if (!(Prefab.Find(thing.PrefabHash) is Structure prefab) || prefab == null || prefab.BuildStates == null ||
            prefab.BuildStates.Count == 0)
        {
            return PlacementVerdict.Unchecked($"{thing.PrefabName} is not a loaded structure prefab with build states");
        }

        if (!catalogue.KitBuilds(prefab))
        {
            return PlacementVerdict.Refuse($"no kit builds {prefab.PrefabName}, so no player can place one",
                PlacementRules.NoKit);
        }

        return Replacing(prefab, catalogue.CursorOf(prefab), thing.ThingTransformPosition,
            thing.ThingTransformRotation, new[] { thing });
    }

    // Where and how the cursor could hold the prefab: at a quarter turn, one its cursor gives it (PlacePlanner.
    // CursorAllows), where it snaps it (the cursor aims into a cell and snaps; a face-placed piece then sits half a
    // cell back on the face). Null when it could stand so.
    private static PlacementVerdict? Stance(Structure prefab, Structure cursor, Vector3 position, Quaternion rotation)
    {
        CubeRotation? turn = CubeRotation.FromQuaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        if (turn == null)
        {
            return PlacementVerdict.Refuse("it is not turned by quarter turns, which the cursor always is",
                PlacementRules.OffGrid);
        }

        if (!PlacePlanner.CursorAllows(prefab, turn))
        {
            return PlacementVerdict.Refuse($"{prefab.PrefabName}'s cursor never turns it to {turn}",
                PlacementRules.Rotation);
        }

        Vector3 aim = prefab.PlacementType == PlacementSnap.Face
            ? position + rotation * Vector3.forward * cursor.GridSize / 2f
            : position;
        Vector3 snapped = CursorCheck.Snap(cursor, aim, rotation);
        return PlayerPlacementRule.OnGrid((snapped - position).sqrMagnitude)
            ? null
            : PlacementVerdict.Refuse($"it stands at {PlacePlanner.Describe(position)}, where the cursor never " +
                                      $"puts it (it snaps it to {PlacePlanner.Describe(snapped)})",
                PlacementRules.OffGrid);
    }

    // The cursor's CanConstruct and whose refusal it is: a text naming a replaced thing; "requires a Frame below" where
    // a replaced thing blocks the cell (FrameRefusalIsReplaced); a door's unnamed "blocked" where the face structure
    // Door.IsSideBlocked finds first is a replaced thing (DoorSide says whether another one blocks).
    private static GameCheck Construct(Structure prefab, Structure cursor, Vector3 position, Quaternion rotation,
        IReadOnlyCollection<Structure> replaced, HashSet<long> ids, List<string> texts)
    {
        string? refusal = CursorCheck.ConstructRefusal(cursor);
        if (refusal == null)
        {
            return new GameCheck(null, false);
        }

        if ((prefab is Door || prefab is RoboticArmDoor) &&
            refusal == CursorCheck.Text(GameStrings.PlacementBlockedByStructure.DisplayString, string.Empty))
        {
            Structure? other = DoorSide(position, ids);
            return other == null
                ? new GameCheck(refusal, true)
                : new GameCheck($"{Names.Of(other)} ({other.PrefabName} {other.ReferenceId}) holds the door's face",
                    false);
        }

        return new GameCheck(refusal, PlayerPlacementRule.BlamesReplaced(refusal, texts) ||
                                      FrameRefusalIsReplaced(prefab, refusal, position, rotation, replaced, ids));
    }

    // Door.IsSideBlocked(allStructual: true) with the replaced things gone: a structure on the door's face grid that
    // fills a cell, or stands on the same grid (another plate or door).
    private static Structure? DoorSide(Vector3 position, HashSet<long> ids)
    {
        Grid3 grid = new Grid3(position);
        foreach (Structure face in new List<Structure>(GridController.World.GetFaceStructures(grid)))
        {
            if (face == null || face.IsBeingDestroyed || ids.Contains(face.ReferenceId))
            {
                continue;
            }

            if (face.StructureCollisionType == CollisionType.BlockGrid || new Grid3(face.ThingTransformPosition) == grid)
            {
                return face;
            }
        }

        return null;
    }

    // The texts the game names a thing in the way with (Structure.CanConstructCell, SmallGrid.CanConstruct,
    // Pipe.CanConstruct, CanMountResult InvalidBlocked), for each replaced thing, as CursorCheck words them.
    private static List<string> TextsNaming(IReadOnlyCollection<Structure> replaced)
    {
        List<string> texts = new List<string>();
        foreach (Structure structure in replaced)
        {
            string name = structure.DisplayName;
            texts.Add(CursorCheck.Text(GameStrings.PlacementBlockedBySmallGrid.AsString(name), string.Empty));
            texts.Add(CursorCheck.Text(GameStrings.PlacementBlockedByStructure.AsString(name), string.Empty));
            texts.Add(CursorCheck.Text(GameStrings.GridBlockedByStructure.AsString(name), string.Empty));
            texts.Add(CursorCheck.Text(GameStrings.FaceBlockedByStructure.AsString(name), string.Empty));
            texts.Add(CursorCheck.Text(GameStrings.CannotMergeWithSmallGrid.AsString(name), string.Empty));
            texts.Add(CursorCheck.Text(InterfaceStrings.TooltipPlacementSnapFaceMountBlocked(structure), string.Empty));
        }

        return texts;
    }

    // "Requires a Frame below" where a replaced thing that fills its 2 m cell blocks the cell the piece stands in
    // (SmallGrid.HasFrameBelow's IsBlockedGrid) while the frame the game looks for is there below.
    private static bool FrameRefusalIsReplaced(Structure prefab, string refusal, Vector3 position,
        Quaternion rotation, IReadOnlyCollection<Structure> replaced, HashSet<long> ids)
    {
        if (!(prefab is SmallGrid piece))
        {
            return false;
        }

        bool fills = false;
        foreach (Structure structure in replaced)
        {
            fills |= structure.StructureCollisionType == CollisionType.BlockGrid &&
                     (!(structure is SmallGrid small) || small.DualRegister);
        }

        Vector3 up = rotation * Vector3.up;
        GridCell below = LargeCells.Below(Bodies.V(position), Bodies.V(up), PlacePlanner.SupportDepth(piece));
        Structure? frame = GridController.World.Get<Structure>(
            new Vector3(below.X / 10f, below.Y / 10f, below.Z / 10f), StructureElement.Center);
        bool frameBelow = frame != null && frame.AllowMounting && !ids.Contains(frame.ReferenceId);
        return PlayerPlacementRule.FrameRefusalBlamesReplaced(PlacePlanner.RequiresFrameRefusal(refusal), fills,
            frameBelow);
    }

    // What the cursor check could not see past a replaced thing: the neighbours, the replaced things treated as gone.
    // A device mounted on a pipe or cable only needs its slot free (its CanConstruct looks at nothing else); a small-grid
    // piece every small-grid collision (PlacementCheck) and, when it registers in 2 m cells too, those; anything else
    // its 2 m cells or face as Structure.CanConstructCell reads them.
    private static string? Neighbours(Structure prefab, Structure cursor, Vector3 position, Quaternion rotation,
        HashSet<long> ids)
    {
        switch (prefab)
        {
            case DevicePipeMounted _:
            case DeviceCableMounted _:
                SmallGrid? device = GridController.World.GetSmallCell(position)?.Device;
                return device != null && !device.IsBeingDestroyed && !ids.Contains(device.ReferenceId)
                    ? $"{Names.Of(device)} ({device.PrefabName} {device.ReferenceId}) is in the way"
                    : null;
            case SmallGrid piece:
                return PlacementCheck.Refusal(prefab, position, rotation, ids) ??
                       (piece.DualRegister ? LargeRefusal(prefab, cursor, position, rotation, ids) : null);
            default:
                return LargeRefusal(prefab, cursor, position, rotation, ids);
        }
    }

    // Structure.CanConstructCell over the 2 m cells a grid-placed piece takes (or the cell of a face-placed one), the
    // things in ids treated as gone: a piece that fills its cell needs it empty; nothing may fill it; nothing may hold
    // the same face.
    private static string? LargeRefusal(Structure prefab, Structure cursor, Vector3 position, Quaternion rotation,
        HashSet<long> ids)
    {
        GridController world = GridController.World;
        List<Cell?> cells = new List<Cell?>();
        if (prefab.PlacementType == PlacementSnap.Grid)
        {
            foreach (GridCell cell in Bodies.LargeCells(prefab, position, rotation))
            {
                cells.Add(world.GetCell(new Vector3(cell.X / 10f, cell.Y / 10f, cell.Z / 10f)));
            }
        }
        else
        {
            cells.Add(world.GetCell(cursor.GetLocalGrid()));
        }

        foreach (Cell? cell in cells)
        {
            if (cell?.AllStructures == null)
            {
                continue;
            }

            foreach (Structure other in cell.AllStructures.ToArray())
            {
                if (other == null || other.IsBeingDestroyed || ids.Contains(other.ReferenceId))
                {
                    continue;
                }

                bool blocks = prefab.StructureCollisionType == CollisionType.BlockGrid ||
                              other.StructureCollisionType == CollisionType.BlockGrid ||
                              (other.StructureCollisionType == CollisionType.BlockFace &&
                               (other.GetGridPosition() - position).sqrMagnitude < 0.01f);
                if (blocks)
                {
                    return $"{Names.Of(other)} ({other.PrefabName} {other.ReferenceId}) is in the way";
                }
            }
        }

        return null;
    }
}
