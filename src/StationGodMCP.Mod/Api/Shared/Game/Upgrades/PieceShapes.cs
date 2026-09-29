#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// Things on the small grid as the connectivity model sees them: the cells they occupy and their ends. A placed thing
/// is read as registered (GridBounds.GetLocalSmallGrid at RegisteredPosition and RegisteredRotation, as
/// GridController.AddSmallGridStructure registers it; each end's Connection.GetLocalGrid and GetFacingGrid). A prefab
/// is read as it would stand at a position and rotation: its ends' transforms relative to its root, turned and moved
/// there, and put on the grid the way Connection.SetGrids does (the end's cell, and the cell half a metre along its
/// forward, both WorldToLocalGrid with SmallGridSize and SmallGridOffset).
/// </summary>
internal static class PieceShapes
{
    private static readonly Dictionary<int, List<PrefabEnd>?> PrefabEnds = new Dictionary<int, List<PrefabEnd>?>();

    /// <summary>The 24 rotations that keep the grid's axes on axes.</summary>
    internal static readonly Quaternion[] AxisRotations = BuildAxisRotations();

    internal static PieceModel Live(SmallGrid thing)
    {
        List<GridCell> cells = RegisteredCells(thing);
        List<PieceEnd> ends = new List<PieceEnd>(thing.OpenEnds?.Count ?? 0);
        if (thing.OpenEnds != null)
        {
            foreach (Connection end in thing.OpenEnds)
            {
                if (end != null && end.Transform != null)
                {
                    ends.Add(new PieceEnd(Cell(end.GetLocalGrid()), Cell(end.GetFacingGrid()), (int)end.ConnectionType,
                        (int)end.ConnectionRole));
                }
            }
        }

        return new PieceModel(thing.ReferenceId, cells, ends, ContentOf(thing));
    }

    /// <summary>The cells a placed thing fills, as registered.</summary>
    internal static List<GridCell> RegisteredCells(SmallGrid thing) =>
        CellsOf(thing.GridBounds, thing.RegisteredPosition, thing.RegisteredRotation);

    /// <summary>Whether Placed can model the prefab: its ends and its grid bounds can be read.</summary>
    internal static bool CanModel(Structure prefab) =>
        EndsOf(prefab) != null && prefab.GridBounds != null && prefab.GridBounds.IsValid();

    /// <summary>The prefab standing there, under the id; null when its ends or bounds cannot be read.</summary>
    internal static PieceModel? Placed(Structure prefab, Vector3 position, Quaternion rotation, long id)
    {
        List<PrefabEnd>? local = EndsOf(prefab);
        if (local == null || prefab.GridBounds == null || !prefab.GridBounds.IsValid())
        {
            return null;
        }

        GridController world = GridController.World;
        List<PieceEnd> ends = new List<PieceEnd>(local.Count);
        foreach (PrefabEnd end in local)
        {
            Vector3 at = position + rotation * end.Position;
            Vector3 forward = rotation * end.Forward;
            Grid3 cell = world.WorldToLocalGrid(at, SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
            Grid3 facing = world.WorldToLocalGrid(at + forward * SmallGrid.SmallGridSize, SmallGrid.SmallGridSize,
                SmallGrid.SmallGridOffset);
            ends.Add(new PieceEnd(Cell(cell), Cell(facing), end.Type, end.Role));
        }

        return new PieceModel(id, CellsOf(prefab.GridBounds, position, rotation), ends, ContentOf(prefab));
    }

    /// <summary>
    /// Where the prefab's end transforms would be, standing there (SmallGrid.IsPipeEndCollision compares these);
    /// null when its ends cannot be read.
    /// </summary>
    internal static List<Vector3>? EndPositions(Structure prefab, Vector3 position, Quaternion rotation)
    {
        List<PrefabEnd>? local = EndsOf(prefab);
        if (local == null)
        {
            return null;
        }

        List<Vector3> positions = new List<Vector3>(local.Count);
        foreach (PrefabEnd end in local)
        {
            positions.Add(position + rotation * end.Position);
        }

        return positions;
    }

    /// <summary>Which way each of the prefab's ends would point, standing there; null when its ends cannot be read.</summary>
    internal static List<Vector3>? EndForwards(Structure prefab, Quaternion rotation)
    {
        List<PrefabEnd>? local = EndsOf(prefab);
        return local?.ConvertAll(end => rotation * end.Forward);
    }

    /// <summary>
    /// Where a replacement made from the old piece stands: CreateStructureInstance keeps the old piece's grid
    /// (GridController.WorldToLocal of ThingTransformPosition) and Structure.OnAssignedReference moves the new thing
    /// to that grid's world position.
    /// </summary>
    internal static Vector3 PlacementOf(Structure old)
    {
        GridController world = GridController.World;
        return world.LocalToWorld(world.WorldToLocal(old.ThingTransformPosition));
    }

    /// <summary>
    /// The world position of a small-grid cell's centre: a cell is Grid3 of the centre in tenths of a metre
    /// (WorldToLocalGrid with SmallGridSize and SmallGridOffset), and LocalToWorld is its inverse.
    /// </summary>
    internal static Vector3 CentreOf(GridCell cell) => GridController.World.LocalToWorld(Grid(cell));

    internal static GridCell Cell(Grid3 grid) => new GridCell(grid.x, grid.y, grid.z);

    internal static Grid3 Grid(GridCell cell) => new Grid3(cell.X, cell.Y, cell.Z);

    private static PipeContent? ContentOf(Thing thing) =>
        thing is Pipe pipe
            ? new PipeContent((int)pipe.PipeContentType, pipe.PipeContentType == Pipe.ContentType.All)
            : null;

    private static List<GridCell> CellsOf(GridBounds? bounds, Vector3 position, Quaternion rotation)
    {
        List<GridCell> cells = new List<GridCell>();
        if (bounds == null || !bounds.IsValid())
        {
            return cells;
        }

        foreach (Grid3 grid in (Grid3[])bounds.GetLocalSmallGrid(position, rotation))
        {
            cells.Add(Cell(grid));
        }

        return cells;
    }

    // Each end relative to the prefab's root, cached per prefab: prefab transforms never move.
    private static List<PrefabEnd>? EndsOf(Structure prefab)
    {
        if (PrefabEnds.TryGetValue(prefab.PrefabHash, out List<PrefabEnd>? cached))
        {
            return cached;
        }

        List<PrefabEnd>? ends = ReadEnds(prefab);
        PrefabEnds[prefab.PrefabHash] = ends;
        return ends;
    }

    private static List<PrefabEnd>? ReadEnds(Structure prefab)
    {
        SmallGrid? grid = prefab as SmallGrid;
        if (grid == null || grid.OpenEnds == null)
        {
            return null;
        }

        Transform root = prefab.transform;
        Vector3 scale = root.localScale;
        List<PrefabEnd> ends = new List<PrefabEnd>(grid.OpenEnds.Count);
        foreach (Connection end in grid.OpenEnds)
        {
            if (end == null || end.Transform == null)
            {
                return null;
            }

            Vector3 position = Vector3.Scale(root.InverseTransformPoint(end.Transform.position), scale);
            Vector3 forward = root.InverseTransformDirection(end.Transform.forward);
            ends.Add(new PrefabEnd(position, forward, (int)end.ConnectionType, (int)end.ConnectionRole));
        }

        return ends;
    }

    private static Quaternion[] BuildAxisRotations()
    {
        Vector3[] axes = { Vector3.forward, Vector3.back, Vector3.up, Vector3.down, Vector3.left, Vector3.right };
        List<Quaternion> rotations = new List<Quaternion>(24);
        foreach (Vector3 forward in axes)
        {
            foreach (Vector3 up in axes)
            {
                if (Mathf.Approximately(Vector3.Dot(forward, up), 0f))
                {
                    rotations.Add(Quaternion.LookRotation(forward, up));
                }
            }
        }

        return rotations.ToArray();
    }

    private sealed class PrefabEnd
    {
        internal PrefabEnd(Vector3 position, Vector3 forward, int type, int role)
        {
            Position = position;
            Forward = forward;
            Type = type;
            Role = role;
        }

        internal Vector3 Position { get; }

        internal Vector3 Forward { get; }

        internal int Type { get; }

        internal int Role { get; }
    }
}

/// <summary>
/// A kit the family builds with (a cable coil or pipe kit, MultiMergeConstructor; a chute kit, MultiConstructor) whose
/// pieces are all of one grade (constructables of no grade, such as special connectors or chute valves, are left out),
/// and those pieces in the kit's own order, each as its registered prefab (Prefab.Find by hash). A piece costs its
/// first build state's Tool.EntryQuantity of the kit (MultiConstructor.Construct).
/// </summary>
internal sealed class Kit
{
    internal Kit(MultiConstructor item, Grade grade, List<Structure> pieces)
    {
        Item = item;
        Grade = grade;
        Pieces = pieces;
        Unmodelled = pieces.FindAll(static piece => !PieceShapes.CanModel(piece));
    }

    internal MultiConstructor Item { get; }

    internal Grade Grade { get; }

    internal List<Structure> Pieces { get; }

    /// <summary>Pieces whose ends or grid bounds cannot be read, so they can never be a twin.</summary>
    internal List<Structure> Unmodelled { get; }

    internal bool Places(int prefabHash)
    {
        foreach (Structure piece in Pieces)
        {
            if (piece.PrefabHash == prefabHash)
            {
                return true;
            }
        }

        return false;
    }

    internal static int CostOf(Structure piece) =>
        piece.BuildStates != null && piece.BuildStates.Count > 0 && piece.BuildStates[0].Tool != null
            ? piece.BuildStates[0].Tool.EntryQuantity
            : 0;
}

/// <summary>
/// Every kit of one family, found from Prefab.AllPrefabs by what the kits place, never by name. When two kits place
/// pieces of one grade, the one with the most pieces is that grade's kit; a tie is ambiguous and that grade has none.
/// </summary>
internal sealed class KitCatalogue
{
    private readonly List<Kit> _kits;
    private readonly List<Grade> _ambiguous;

    private KitCatalogue(List<Kit> kits, List<Grade> ambiguous)
    {
        _kits = kits;
        _ambiguous = ambiguous;
    }

    internal static KitCatalogue Of(UpgradeFamily family)
    {
        List<Kit> kits = new List<Kit>();
        List<Grade> ambiguous = new List<Grade>();
        foreach (Thing prefab in Prefab.AllPrefabs)
        {
            Kit? kit = prefab is MultiConstructor item && family.BuildsWith(item) ? Classify(family, item) : null;
            if (kit != null)
            {
                Keep(kits, ambiguous, kit);
            }
        }

        return new KitCatalogue(kits, ambiguous);
    }

    /// <summary>The grade's kit; null when there is none or two kits tie for it.</summary>
    internal Kit? For(Grade grade)
    {
        foreach (Grade tie in _ambiguous)
        {
            if (tie.SameAs(grade))
            {
                return null;
            }
        }

        foreach (Kit kit in _kits)
        {
            if (kit.Grade.SameAs(grade))
            {
                return kit;
            }
        }

        return null;
    }

    internal bool IsAmbiguous(Grade grade)
    {
        foreach (Grade tie in _ambiguous)
        {
            if (tie.SameAs(grade))
            {
                return true;
            }
        }

        return false;
    }

    private static Kit? Classify(UpgradeFamily family, MultiConstructor item)
    {
        if (item.Constructables == null || item.Constructables.Count == 0)
        {
            return null;
        }

        Grade? grade = null;
        List<Structure> pieces = new List<Structure>(item.Constructables.Count);
        foreach (Structure constructable in item.Constructables)
        {
            Structure? piece = constructable != null ? Registered(constructable) : null;
            Grade? own = piece != null ? family.GradeOf(piece) : null;
            if (own == null)
            {
                continue;
            }

            if (grade != null && !grade.SameAs(own))
            {
                return null;
            }

            grade ??= own;
            pieces.Add(piece!);
        }

        return grade != null ? new Kit(item, grade, pieces) : null;
    }

    // A Constructables entry is the source asset. Prefab.Register instantiates each source asset and runs OnPrefabLoad
    // (CachePrefabBounds, which fills GridBounds) on that copy only, and Thing.Create builds from
    // Prefab.Find(prefab.PrefabHash), never from the asset. The asset's GridBounds stays empty.
    private static Structure? Registered(Structure constructable) =>
        Prefab.Find(constructable.PrefabHash) as Structure;

    private static void Keep(List<Kit> kits, List<Grade> ambiguous, Kit kit)
    {
        for (int index = 0; index < kits.Count; index++)
        {
            if (!kits[index].Grade.SameAs(kit.Grade))
            {
                continue;
            }

            if (kit.Pieces.Count > kits[index].Pieces.Count)
            {
                kits[index] = kit;
                ambiguous.RemoveAll(tie => tie.SameAs(kit.Grade));
            }
            else if (kit.Pieces.Count == kits[index].Pieces.Count)
            {
                ambiguous.Add(kit.Grade);
            }

            return;
        }

        kits.Add(kit);
    }
}

/// <summary>A piece's replacement: the target prefab, where and how turned it stands, and its model there.</summary>
internal sealed class Twin
{
    internal Twin(Structure prefab, Vector3 position, Quaternion rotation, PieceModel model)
    {
        Prefab = prefab;
        Position = position;
        Rotation = rotation;
        Model = model;
    }

    internal Structure Prefab { get; }

    /// <summary>The world position it is built at (its grid's position).</summary>
    internal Vector3 Position { get; }

    internal Quaternion Rotation { get; }

    internal PieceModel Model { get; }

    /// <summary>Whether it stands at exactly the old rotation (a mounted device's axis test then agrees).</summary>
    internal bool KeepsRotation(Quaternion old) =>
        Rotation.x == old.x && Rotation.y == old.y && Rotation.z == old.z && Rotation.w == old.w;
}

/// <summary>
/// Finds each piece's replacement in the target kit: the first piece in the kit's order that, at the old piece's grid
/// and at the old rotation or one of the 24 axis rotations, has the same cells and the same ends (type, role, cell and
/// facing cell) as the old piece has now. The kit's own merge picks the first matching constructable the same way
/// (MultiMergeConstructor.Construct). What a source prefab mapped to is remembered as a rotation relative to the old
/// one and tried first for the next piece of that prefab.
/// </summary>
internal sealed class TwinFinder
{
    private readonly Dictionary<int, KnownTwin> _known = new Dictionary<int, KnownTwin>();

    internal Twin? Find(SmallGrid old, PieceModel live, Kit target) =>
        FindAt(old.PrefabHash, PieceShapes.PlacementOf(old), old.ThingTransformRotation, live, target);

    /// <summary>
    /// As Find, for a piece to stand at the position (a long straight's part stands in one of its cells), starting
    /// from the rotation; what was found is remembered under the key.
    /// </summary>
    internal Twin? FindAt(int key, Vector3 position, Quaternion rotation, PieceModel live, Kit target)
    {
        if (_known.TryGetValue(key, out KnownTwin known))
        {
            Twin? again = Try(known.Prefab, position, rotation * known.Relative, live);
            if (again != null)
            {
                return again;
            }
        }

        foreach (Structure prefab in target.Pieces)
        {
            Twin? found = Try(prefab, position, rotation, live) ?? TryAxes(prefab, position, live);
            if (found != null)
            {
                _known[key] = new KnownTwin(prefab, Quaternion.Inverse(rotation) * found.Rotation);
                return found;
            }
        }

        return null;
    }

    private static Twin? TryAxes(Structure prefab, Vector3 position, PieceModel live)
    {
        foreach (Quaternion rotation in PieceShapes.AxisRotations)
        {
            Twin? found = Try(prefab, position, rotation, live);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static Twin? Try(Structure prefab, Vector3 position, Quaternion rotation, PieceModel live)
    {
        PieceModel? model = PieceShapes.Placed(prefab, position, rotation, live.Id);
        return model != null && Connectivity.SameShape(model, live)
            ? new Twin(prefab, position, rotation, model)
            : null;
    }

    private readonly struct KnownTwin
    {
        internal KnownTwin(Structure prefab, Quaternion relative)
        {
            Prefab = prefab;
            Relative = relative;
        }

        internal Structure Prefab { get; }

        internal Quaternion Relative { get; }
    }
}
