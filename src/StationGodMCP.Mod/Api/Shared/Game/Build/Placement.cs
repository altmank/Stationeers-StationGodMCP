#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.RoboticArm;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// The prefabs place_structure may build: loaded structures with build states that some kit builds (a Constructor's
/// BuildStructure or a MultiConstructor's Constructables), and the game's placement cursor for each
/// (InventoryManager._constructionCursors, one instance per structure prefab, made when the inventory manager sets
/// up). Always the registered prefab (Prefab.Find): a kit's entry is the source asset, whose GridBounds is empty.
/// </summary>
internal sealed class BuildCatalogue
{
    private readonly HashSet<int> _kitBuilt;
    private readonly HashSet<int> _mergeKitBuilt;
    private readonly Dictionary<int, Structure> _cursors;

    private BuildCatalogue(HashSet<int> kitBuilt, HashSet<int> mergeKitBuilt, Dictionary<int, Structure> cursors)
    {
        _kitBuilt = kitBuilt;
        _mergeKitBuilt = mergeKitBuilt;
        _cursors = cursors;
    }

    internal static BuildCatalogue Load()
    {
        HashSet<int> built = new HashSet<int>();
        HashSet<int> mergeBuilt = new HashSet<int>();
        foreach (Thing prefab in Prefab.AllPrefabs)
        {
            switch (prefab)
            {
                case MultiConstructor kit when kit.Constructables != null:
                    foreach (Structure constructable in kit.Constructables)
                    {
                        if (constructable != null)
                        {
                            built.Add(constructable.PrefabHash);
                            if (kit is MultiMergeConstructor)
                            {
                                mergeBuilt.Add(constructable.PrefabHash);
                            }
                        }
                    }

                    break;
                case Constructor single when single.BuildStructure != null:
                    built.Add(single.BuildStructure.PrefabHash);
                    break;
            }
        }

        Dictionary<int, Structure> cursors = new Dictionary<int, Structure>();
        if (GameMembers.ConstructionCursors.TryResolve() &&
            GameMembers.ConstructionCursors.GetValue(null) is Dictionary<string, Structure> byName)
        {
            foreach (Structure cursor in byName.Values)
            {
                if (cursor != null)
                {
                    cursors[cursor.PrefabHash] = cursor;
                }
            }
        }

        return new BuildCatalogue(built, mergeBuilt, cursors);
    }

    /// <summary>The registered structure prefab named or hashed, or why there is none to build.</summary>
    internal Structure? Find(PrefabRef reference, out string? issue)
    {
        Thing? found = reference switch
        {
            PrefabRef.Named named => ByName(named.Name),
            PrefabRef.Hashed hashed => Prefab.Find(hashed.Hash),
            _ => null
        };
        if (found == null)
        {
            issue = $"No loaded prefab is {reference}; prefab names are as find_things and looking_at report them " +
                    "(e.g. StructureWallLight).";
            return null;
        }

        if (!(Prefab.Find(found.PrefabHash) is Structure structure) || structure == null)
        {
            issue = $"{found.PrefabName} is a {found.GetType().Name}, not a structure; items are not placed.";
            return null;
        }

        issue = IssueOf(structure);
        return issue == null ? structure : null;
    }

    /// <summary>Whether some kit builds the prefab, so a player can place it at all.</summary>
    internal bool KitBuilds(Structure prefab) => _kitBuilt.Contains(prefab.PrefabHash);

    /// <summary>
    /// Whether a merge kit (MultiMergeConstructor) builds the prefab: only such a kit replaces a standing fuselage piece
    /// instead of building a second one into its cell (MultiMergeConstructor.Construct, MultiMergeConstructor.cs:39-88).
    /// </summary>
    internal bool MergeKitBuilds(Structure prefab) => _mergeKitBuilt.Contains(prefab.PrefabHash);

    internal Structure? CursorOf(Structure prefab) =>
        _cursors.TryGetValue(prefab.PrefabHash, out Structure cursor) && cursor != null ? cursor : null;

    private string? IssueOf(Structure prefab)
    {
        if (prefab.BuildStates == null || prefab.BuildStates.Count == 0)
        {
            return $"{prefab.PrefabName} has no build states.";
        }

        return _kitBuilt.Contains(prefab.PrefabHash)
            ? null
            : $"No loaded kit builds {prefab.PrefabName} (no Constructor or MultiConstructor lists it)." +
              KitBuiltNames.HintFor(prefab, _kitBuilt);
    }

    private static Thing? ByName(string name)
    {
        Thing exact = Prefab.Find(name);
        return exact != null ? exact : FindIgnoringCase(name);
    }

    private static Thing? FindIgnoringCase(string name)
    {
        foreach (Thing prefab in Prefab.AllPrefabs)
        {
            if (prefab != null && string.Equals(prefab.PrefabName, name, StringComparison.OrdinalIgnoreCase))
            {
                return prefab;
            }
        }

        return null;
    }
}

/// <summary>
/// Where the cursor would stand and whether the game's cursor lets the piece be built there, checked on the game's
/// own cursor for the prefab (InventoryManager.UpdatePlacement): the position snapped as the cursor snaps it
/// (Structure.GetWorldGrid; a face-placed piece then moves half a cell against its forward, onto the face), then
/// CanConstruct (every structure's own rules: blocked cells and faces, small-grid collisions, rocket cells and hulls,
/// the overrides of each class), CanMountOnWall for a face-mounted piece, and for a piece that fills its cells no loose
/// thing, creature or player inside it (BoundsIntersectWith, as the cursor checks DynamicThing.DynamicObjects). The
/// cursor is moved for the check and put back in the same call, so nothing is seen.
/// </summary>
internal static class CursorCheck
{
    internal static Vector3 Snap(Structure cursor, Vector3 at, Quaternion rotation)
    {
        Vector3 grid = cursor.GetWorldGrid(at);
        return cursor.PlacementType == PlacementSnap.Face
            ? grid - rotation * Vector3.forward * cursor.GridSize / 2f
            : grid;
    }

    /// <summary>Why the game would not build the prefab there; null when it would.</summary>
    internal static string? Refusal(Structure cursor, Vector3 position, Quaternion rotation, HashSet<long> ignore) =>
        At(cursor, position, rotation, () => GameRefusal(cursor) ??
                                             (cursor.StructureCollisionType == CollisionType.BlockGrid
                                                 ? DynamicInside(cursor, ignore)
                                                 : null));

    /// <summary>
    /// Runs a check with the cursor moved to the spot and turned, and puts it back in the same call (nothing is seen).
    /// </summary>
    internal static T At<T>(Structure cursor, Vector3 position, Quaternion rotation, Func<T> check)
    {
        Transform transform = cursor.ThingTransform;
        Vector3 oldPosition = transform.position;
        Quaternion oldRotation = transform.rotation;
        Vector3 oldField = cursor.Position;
        Quaternion oldFieldRotation = cursor.Rotation;
        try
        {
            transform.SetPositionAndRotation(position, rotation);
            cursor.Position = position;
            cursor.Rotation = rotation;
            return check();
        }
        finally
        {
            transform.SetPositionAndRotation(oldPosition, oldRotation);
            cursor.Position = oldField;
            cursor.Rotation = oldFieldRotation;
        }
    }

    /// <summary>
    /// The game's own verdict on the cursor where it stands (At): CanConstruct, then CanMountOnWall for a face-mounted
    /// piece; null when both allow it. Loose things inside are not looked at.
    /// </summary>
    internal static string? GameRefusal(Structure cursor) => ConstructRefusal(cursor) ?? MountRefusal(cursor);

    /// <summary>
    /// The cursor's CanConstruct where it stands (At): the class's own rules; null when it allows it. A fuselage piece
    /// that would replace another (StructureFuselage.CanMerge) is not asked: its CanConstruct then reads the local
    /// player's other hand and allows the placement outright when that holds the angle grinder (Fuselage.cs:30-41,
    /// EngineFuselage.cs:31-42, NoseCone.cs:28-39), and the tool builds as a player holding one (place_structure
    /// refuses such a replacement unless a merge kit builds the prefab).
    /// </summary>
    internal static string? ConstructRefusal(Structure cursor)
    {
        if (cursor is global::Objects.Rockets.StructureFuselage fuselage && fuselage.CanMerge())
        {
            return null;
        }

        CanConstructInfo info = cursor.CanConstruct();
        return info.CanConstruct ? null : Text(info.ErrorMessage, "the cell or face is taken");
    }

    /// <summary>
    /// The cursor's CanMountOnWall where it stands (At), which the cursor asks of every face-mounted piece; null when it
    /// allows it or the piece is not face-mounted.
    /// </summary>
    internal static string? MountRefusal(Structure cursor)
    {
        if (cursor.PlacementType != PlacementSnap.FaceMount)
        {
            return null;
        }

        CanMountResult mount = cursor.CanMountOnWall();
        return mount ? null : Text(mount.ResultMessage(), "nothing to mount it on");
    }

    // The cells a small-grid piece would take, as SmallGrid.CanConstruct reads them.
    internal static Grid3[] SmallCells(Structure prefab, Vector3 position, Quaternion rotation) =>
        prefab.GridBounds != null && prefab.GridBounds.IsValid()
            ? (Grid3[])prefab.GridBounds.GetLocalSmallGrid(position, rotation)
            : new Grid3[0];

    /// <summary>
    /// A piece of the same slot already in one of the small cells (SmallCell.Add keeps one cable, chute, pipe, device,
    /// rail and other per cell): the cursor lets a cable onto a cable of its type because a coil merges them, which a
    /// plain build would not; null when every slot is free.
    /// </summary>
    internal static string? SlotTaken(SmallGrid piece, Grid3[] cells, HashSet<long> ignore)
    {
        GridController world = GridController.World;
        foreach (Grid3 grid in cells)
        {
            SmallCell? cell = world.GetSmallCell(grid);
            SmallGrid? holder = cell == null ? null : SlotOf(cell, piece);
            if (holder != null && !holder.IsBeingDestroyed && !ignore.Contains(holder.ReferenceId))
            {
                return $"{Names.Of(holder)} ({holder.PrefabName} {holder.ReferenceId}) already takes that slot of " +
                       $"the cell at {GridText.Metres(grid.x, grid.y, grid.z)}";
            }
        }

        return null;
    }

    /// <summary>The slot of a small cell a piece takes, as SlotOf reads it: cable, chute, pipe, device, rail or other.</summary>
    internal static string SlotName(SmallGrid piece) => piece switch
    {
        Cable _ => "cable",
        Chute _ => "chute",
        Pipe _ => "pipe",
        Device device when device.SmallCollisionType != SmallGridBlock.Covers => "device",
        IRoboticArmRail _ => "rail",
        _ => "other"
    };

    private static SmallGrid? SlotOf(SmallCell cell, SmallGrid piece) => piece switch
    {
        Cable _ => cell.Cable,
        Chute _ => cell.Chute,
        Pipe _ => cell.Pipe,
        Device device when device.SmallCollisionType != SmallGridBlock.Covers => cell.Device,
        IRoboticArmRail _ => cell.Rail as SmallGrid,
        _ => cell.Other
    };

    private static string? DynamicInside(Structure cursor, HashSet<long> ignore)
    {
        foreach (DynamicThing thing in new List<DynamicThing>(DynamicThing.DynamicObjects))
        {
            if (thing != null && !thing.IsBeingDestroyed && thing.ParentSlot == null &&
                !ignore.Contains(thing.ReferenceId) && cursor.BoundsIntersectWith(thing))
            {
                return $"{Names.Of(thing)} ({thing.PrefabName} {thing.ReferenceId}) is inside it";
            }
        }

        return null;
    }

    internal static string Text(string? message, string fallback) =>
        string.IsNullOrEmpty(message) ? fallback : Shared.Text.Plain(message) ?? fallback;
}
