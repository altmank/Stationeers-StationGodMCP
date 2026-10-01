#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>A thing a dry run plans: its prefab, where and how it would stand, and the cells and ends it would take.</summary>
internal sealed class PlannedThing
{
    internal PlannedThing(Structure prefab, Vector3 position, Quaternion rotation, IReadOnlyList<GridCell>? cells = null,
        IReadOnlyList<PieceEnd>? ends = null, int? index = null)
    {
        Prefab = prefab;
        Position = position;
        Rotation = rotation;
        Cells = cells;
        Ends = ends;
        Index = index;
    }

    internal Structure Prefab { get; }

    internal Vector3 Position { get; }

    internal Quaternion Rotation { get; }

    /// <summary>The small cells it takes, when the planner knows them; else read from the prefab at that pose.</summary>
    internal IReadOnlyList<GridCell>? Cells { get; }

    /// <summary>Its ends, when the planner knows them; else read from the prefab at that pose.</summary>
    internal IReadOnlyList<PieceEnd>? Ends { get; }

    /// <summary>The placement's index in its request, for the warning it reports under.</summary>
    internal int? Index { get; }
}

/// <summary>
/// One lint call's view of the game: the subjects of a room or box (audit), or of a plan and what stands around it
/// (dry run), and every thing, cell, network and room a rule reaches from them, each made once and its fields worked
/// out on first read. Main thread only.
/// </summary>
internal sealed class GameLintWorld : ILintWorld
{
    internal const int PortTypes = (int)(NetworkType.PowerAndData | NetworkType.Pipe | NetworkType.PipeLiquid |
                                         NetworkType.Chute);

    private static readonly CableFamily Cables = new CableFamily();
    private static readonly PipeFamily Pipes = new PipeFamily();
    private static readonly ChuteFamily Chutes = new ChuteFamily();

    private readonly Dictionary<long, ThingSubject> _things = new Dictionary<long, ThingSubject>();
    private readonly Dictionary<GridCell, CellSubject> _small = new Dictionary<GridCell, CellSubject>();
    private readonly Dictionary<GridCell, CellSubject> _large = new Dictionary<GridCell, CellSubject>();
    private readonly Dictionary<long, NetworkSubject> _networks = new Dictionary<long, NetworkSubject>();
    private readonly Dictionary<long, RoomSubject> _rooms = new Dictionary<long, RoomSubject>();
    private readonly Dictionary<string, List<ILintObject>> _sets = new Dictionary<string, List<ILintObject>>();
    private readonly HashSet<string> _planned = new HashSet<string>();
    private readonly List<Structure> _doors = new List<Structure>();
    private readonly HashSet<long> _gone;
    private readonly HashSet<long> _added = new HashSet<long>();
    private WorldSubject? _world;
    private BuildCatalogue? _catalogue;

    private GameLintWorld(HashSet<long> gone)
    {
        _gone = gone;
        Facts = new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());
    }

    internal GridFacts Facts { get; }

    internal BuildCatalogue Catalogue => _catalogue ??= BuildCatalogue.Load();

    internal int Cells { get; private set; }

    internal int Doors => _doors.Count;

    /// <summary>The things standing in the 2 m cells: the region an audit checks.</summary>
    internal static GameLintWorld Audit(List<GridCell> region)
    {
        GameLintWorld world = new GameLintWorld(new HashSet<long>());
        world.Gather(region);
        world.Cells = region.Count;
        world.Finish();
        return world;
    }

    /// <summary>
    /// A plan and what stands around it: the touched 2 m cells and their neighbours. gone: ids the plan removes or
    /// replaces, left out of the world.
    /// </summary>
    internal static GameLintWorld DryRun(List<PlannedThing> planned, HashSet<long> gone)
    {
        GameLintWorld world = new GameLintWorld(gone);
        List<ThingSubject> subjects = new List<ThingSubject>(planned.Count);
        HashSet<GridCell> touched = new HashSet<GridCell>();
        for (int index = 0; index < planned.Count; index++)
        {
            ThingSubject subject = ThingSubject.Planned(world, planned[index], index);
            subjects.Add(subject);
            world._planned.Add(subject.Key);
            foreach (GridCell cell in subject.SmallCells)
            {
                world._planned.Add(CellSubject.KeyOf(cell, false));
                touched.Add(SmallCellCode.LargeOf(cell));
            }

            touched.Add(SmallCellCode.LargeOf(new GridCell((int)System.Math.Round(planned[index].Position.x * 10),
                (int)System.Math.Round(planned[index].Position.y * 10), (int)System.Math.Round(planned[index].Position.z * 10))));
        }

        List<GridCell> area = new List<GridCell>(touched.Count * 27);
        HashSet<GridCell> seen = new HashSet<GridCell>();
        foreach (GridCell large in touched)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        GridCell near = new GridCell(large.X + dx * SmallCellCode.Large, large.Y + dy * SmallCellCode.Large,
                            large.Z + dz * SmallCellCode.Large);
                        if (seen.Add(near))
                        {
                            area.Add(near);
                        }
                    }
                }
            }
        }

        world.Gather(area);
        world.Cells = area.Count;
        foreach (ThingSubject subject in subjects)
        {
            world.AddSubject(subject);
            foreach (PortSubject port in subject.Ports)
            {
                world._planned.Add(port.Key);
            }
        }

        world.Finish();
        return world;
    }

    public ILintObject World => _world ??= new WorldSubject(this);

    public IReadOnlyList<ILintObject> Subjects(string set)
    {
        if (!_sets.TryGetValue(set, out List<ILintObject> subjects))
        {
            subjects = Derived(set);
            _sets[set] = subjects;
        }

        return subjects;
    }

    public ILintObject? CellAt(Vec3 point, double size) =>
        size >= 1.0
            ? LargeCell(SmallCellCode.LargeOf(new GridCell((int)System.Math.Round(point.X * 10), (int)System.Math.Round(point.Y * 10),
                (int)System.Math.Round(point.Z * 10))))
            : SmallCell(new GridCell((int)System.Math.Round(point.X * 2.0) * 5, (int)System.Math.Round(point.Y * 2.0) * 5,
                (int)System.Math.Round(point.Z * 2.0) * 5));

    public bool IsPlanned(ILintObject subject) => _planned.Contains(subject.Key);

    internal IReadOnlyList<Structure> DoorsNear => _doors;

    internal bool IsGone(Thing thing) => _gone.Contains(thing.ReferenceId);

    internal ThingSubject? Thing(Thing? thing)
    {
        if (thing == null || !(thing is Structure structure) || structure.IsBeingDestroyed || IsGone(structure))
        {
            return null;
        }

        if (!_things.TryGetValue(structure.ReferenceId, out ThingSubject subject))
        {
            subject = ThingSubject.Live(this, structure);
            _things[structure.ReferenceId] = subject;
        }

        return subject;
    }

    internal ThingSubject? ThingById(long id) =>
        GameLookup.TryFindThing(new ThingId(id), out Thing found) ? Thing(found) : null;

    internal CellSubject SmallCell(GridCell cell)
    {
        if (!_small.TryGetValue(cell, out CellSubject subject))
        {
            subject = new CellSubject(this, cell, false);
            _small[cell] = subject;
        }

        return subject;
    }

    internal CellSubject LargeCell(GridCell cell)
    {
        if (!_large.TryGetValue(cell, out CellSubject subject))
        {
            subject = new CellSubject(this, cell, true);
            _large[cell] = subject;
        }

        return subject;
    }

    internal NetworkSubject? Network(IReferencable? network, string kind)
    {
        if (network == null)
        {
            return null;
        }

        if (!_networks.TryGetValue(network.ReferenceId, out NetworkSubject subject))
        {
            subject = new NetworkSubject(this, network, kind);
            _networks[network.ReferenceId] = subject;
        }

        return subject;
    }

    /// <summary>The network of a cable, pipe or chute piece.</summary>
    internal NetworkSubject? NetworkOf(SmallGrid piece) =>
        Cables.IsMember(piece) ? Network(Cables.NetworkOf(piece), "cable")
        : Pipes.IsMember(piece) ? Network(Pipes.NetworkOf(piece), "pipe")
        : Chutes.IsMember(piece) ? Network(Chutes.NetworkOf(piece), "chute")
        : null;

    internal RoomSubject? Room(Room? room)
    {
        if (room == null)
        {
            return null;
        }

        if (!_rooms.TryGetValue(room.RoomId, out RoomSubject subject))
        {
            subject = new RoomSubject(this, room);
            _rooms[room.RoomId] = subject;
        }

        return subject;
    }

    /// <summary>The things of the region in a room: its devices field.</summary>
    internal List<ILintObject> ThingsIn(RoomSubject room)
    {
        List<ILintObject> inside = new List<ILintObject>();
        foreach (ILintObject thing in Subjects("things"))
        {
            if (thing is ThingSubject subject && !subject.IsPiece && subject.RoomOf()?.Key == room.Key)
            {
                inside.Add(thing);
            }
        }

        return inside;
    }

    // Every piece, device and 2 m structure in the cells, the doors on their faces, then the sets built from them.
    private void Gather(List<GridCell> region)
    {
        foreach (string set in new[] { "pieces", "devices", "structures", "things" })
        {
            _sets[set] = new List<ILintObject>();
        }

        HashSet<long> doors = new HashSet<long>();
        foreach (GridCell large in region)
        {
            Cell? standing = GridController.World.GetCell(new Vector3(large.X / 10f, large.Y / 10f, large.Z / 10f));
            if (standing?.AllStructures != null)
            {
                foreach (Structure structure in new List<Structure>(standing.AllStructures))
                {
                    if (structure != null && !(structure is SmallGrid))
                    {
                        AddThing(structure);
                    }
                }
            }

            foreach (GridStep face in GridStep.All)
            {
                foreach (Structure structure in Facts.FaceStructures(large, face))
                {
                    if (!(structure is SmallGrid))
                    {
                        AddThing(structure);
                    }

                    if (Openings.IsDoor(structure) && doors.Add(structure.ReferenceId))
                    {
                        _doors.Add(structure);
                    }
                }
            }

            for (int index = 0; index < SmallCellCode.PerCell; index++)
            {
                SmallCell? small = Facts.SmallAt(SmallCellCode.SmallAt(large, index));
                if (small == null)
                {
                    continue;
                }

                AddThing(small.Cable);
                AddThing(small.Pipe);
                AddThing(small.Chute);
                AddThing(small.Device);
                AddThing(small.Other);
            }
        }
    }

    private void AddThing(Structure? structure)
    {
        if (structure == null || structure.IsBeingDestroyed || IsGone(structure) || _added.Contains(structure.ReferenceId))
        {
            return;
        }

        ThingSubject? subject = Thing(structure);
        if (subject != null)
        {
            AddSubject(subject);
        }
    }

    private void AddSubject(ThingSubject subject)
    {
        if (subject.ReferenceId is long id)
        {
            _added.Add(id);
        }

        _sets[subject.IsPiece ? "pieces" : subject.IsSmallGrid ? "devices" : "structures"].Add(subject);
        _sets["things"].Add(subject);
    }

    // The thing sets sorted by reference id, planned things last.
    private void Finish()
    {
        foreach (string set in new[] { "pieces", "devices", "structures", "things" })
        {
            _sets[set].Sort(static (a, b) => Order(a).CompareTo(Order(b)));
        }
    }

    // ports, cells, networks and rooms: built from the things on first use.
    private List<ILintObject> Derived(string set)
    {
        List<ILintObject> subjects = new List<ILintObject>();
        HashSet<string> seen = new HashSet<string>();
        foreach (ILintObject item in Subjects("things"))
        {
            ThingSubject thing = (ThingSubject)item;
            switch (set)
            {
                case "ports" when !thing.IsPiece && thing.IsSmallGrid:
                    subjects.AddRange(thing.Ports);
                    break;
                case "cells" when thing.IsSmallGrid:
                    foreach (GridCell cell in thing.SmallCells)
                    {
                        CellSubject subject = SmallCell(cell);
                        if (seen.Add(subject.Key))
                        {
                            subjects.Add(subject);
                        }
                    }

                    break;
                case "networks":
                    foreach (NetworkSubject network in thing.NetworkList())
                    {
                        if (seen.Add(network.Key))
                        {
                            subjects.Add(network);
                        }
                    }

                    break;
                case "rooms":
                    RoomSubject? room = thing.RoomOf();
                    if (room != null && seen.Add(room.Key))
                    {
                        subjects.Add(room);
                    }

                    break;
            }
        }

        return subjects;
    }

    private static double Order(ILintObject subject) => subject.ReferenceId ?? double.MaxValue;
}
