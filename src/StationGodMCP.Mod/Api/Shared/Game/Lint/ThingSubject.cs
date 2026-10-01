#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects.Structures;
using Objects.Structures;
using Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>
/// A thing of the lint model: a structure standing in the world, or one a dry run plans (its prefab at a pose). Each
/// field is read from the game on first use (LintModel.Thing documents them).
/// </summary>
internal sealed class ThingSubject : LintSubject
{
    private static readonly CableFamily Cables = new CableFamily();
    private static readonly PipeFamily Pipes = new PipeFamily();
    private static readonly ChuteFamily Chutes = new ChuteFamily();
    private static readonly Dictionary<Type, LintValue> TypeChains = new Dictionary<Type, LintValue>();

    private readonly GameLintWorld _world;
    private readonly PlannedThing? _plan;
    private List<GridCell>? _small;
    private List<GridCell>? _large;
    private List<PortSubject>? _ports;
    private List<NetworkSubject>? _networks;
    private bool _roomRead;
    private RoomSubject? _room;

    private ThingSubject(GameLintWorld world, Structure source, Vector3 position, Quaternion rotation,
        PlannedThing? plan, string key)
        : base(LintModel.Thing)
    {
        _world = world;
        Source = source;
        Pose = position;
        Turn = rotation;
        _plan = plan;
        Key = key;
    }

    /// <summary>The structure standing, or the prefab a plan places.</summary>
    internal Structure Source { get; }

    internal Vector3 Pose { get; }

    internal Quaternion Turn { get; }

    internal bool IsPlanned => _plan != null;

    internal PlannedThing? Plan => _plan;

    public override string Key { get; }

    public override long? ReferenceId => IsPlanned ? (long?)null : Source.ReferenceId;

    public override Vec3? Position => Bodies.V(Pose);

    public override string Describe => IsPlanned
        ? $"planned {Source.PrefabName}{(_plan!.Index.HasValue ? $" (placement {_plan.Index})" : string.Empty)} at {Bodies.V(Pose)}"
        : $"{Names.Of(Source)} ({Source.PrefabName} {Source.ReferenceId})";

    internal bool IsSmallGrid => Source is SmallGrid;

    /// <summary>A cable, pipe or chute piece, or a thing in a pipe's slot (in-line tank, passive vent, connector).</summary>
    internal bool IsPiece => Cables.IsMember(Source) || Pipes.IsMember(Source) || Chutes.IsMember(Source);

    internal static ThingSubject Live(GameLintWorld world, Structure structure) =>
        new ThingSubject(world, structure, structure.ThingTransformPosition, structure.ThingTransformRotation, null,
            "thing:" + structure.ReferenceId.ToString(CultureInfo.InvariantCulture));

    internal static ThingSubject Planned(GameLintWorld world, PlannedThing plan, int index) =>
        new ThingSubject(world, plan.Prefab, plan.Position, plan.Rotation, plan,
            "planned:" + index.ToString(CultureInfo.InvariantCulture));

    internal CubeRotation? Cube => CubeRotation.FromQuaternion(Turn.x, Turn.y, Turn.z, Turn.w);

    /// <summary>The small cells it registers in (none for a 2 m structure).</summary>
    internal List<GridCell> SmallCells => _small ??= ReadSmallCells();

    /// <summary>The 2 m cells a 2 m structure registers in.</summary>
    internal List<GridCell> LargeCells => _large ??= ReadLargeCells();

    internal List<PortSubject> Ports => _ports ??= ReadPorts();

    internal Box3 RenderBox => IsPlanned ? Bodies.RenderBox(Source, Pose, Turn) : Bodies.RenderBox(Source);

    /// <summary>The mount rectangle over its small cells; null without small cells or a quarter turn.</summary>
    internal MountRect? Mount =>
        SmallCells.Count > 0 && Cube is CubeRotation turn
            ? PlacementLayout.MountOf(Source, SmallCells, turn, RenderBox)
            : null;

    internal bool FaceMounted => Source.PlacementType == PlacementSnap.FaceMount;

    internal RoomSubject? RoomOf()
    {
        if (!_roomRead)
        {
            _roomRead = true;
            RoomController? rooms = RoomController.World;
            _room = !IsPlanned && Source.WorldGrid != WorldGrid.INVALID
                ? _world.Room(rooms?.GetRoom(Source.WorldGrid.Value))
                : _world.Room(_world.Facts.RoomAt(LargeAtPose()));
        }

        return _room;
    }

    internal List<NetworkSubject> NetworkList()
    {
        if (_networks != null)
        {
            return _networks;
        }

        _networks = new List<NetworkSubject>();
        if (IsPlanned)
        {
            return _networks;
        }

        if (Source is SmallGrid piece && IsPiece)
        {
            AddNetwork(_world.NetworkOf(piece));
        }

        if (Source is Device device)
        {
            foreach (long id in Cables.DeviceNetworks(device))
            {
                AddNetwork(NetworkById(device.ConnectedCableNetworks, id, "cable"));
            }

            foreach (PortSubject port in Ports)
            {
                AddNetwork(port.NetworkOf());
            }
        }

        return _networks;
    }

    private void AddNetwork(NetworkSubject? network)
    {
        if (network != null && !_networks!.Exists(known => known.Key == network.Key))
        {
            _networks.Add(network);
        }
    }

    private NetworkSubject? NetworkById(IEnumerable<CableNetwork>? networks, long id, string kind)
    {
        if (networks == null)
        {
            return null;
        }

        foreach (CableNetwork network in networks)
        {
            if (network != null && network.ReferenceId == id)
            {
                return _world.Network(network, kind);
            }
        }

        return null;
    }

    protected override LintValue Compute(string field)
    {
        switch (field)
        {
            case "reference_id":
                return LintValue.Of(IsPlanned ? 0.0 : Source.ReferenceId);
            case "prefab":
                return LintValue.Of(Source.PrefabName ?? string.Empty);
            case "prefab_hash":
                return LintValue.Of(Source.PrefabHash);
            case "display_name":
                return LintValue.Of(Names.Of(Source));
            case "label":
                return LintValue.Of(IsPlanned ? null : Labels.CustomNameOf(Source));
            case "name_hash":
                return LintValue.Of(IsPlanned ? LintChipPrograms.HashOf(Source.DisplayName ?? string.Empty) : Source.GetNameHash());
            case "weather_damage_scale":
                return LintValue.Of(Source.WeatherDamageScale);
            case "kind":
                return LintValue.Of(KindOf(Source));
            case "runtime_type":
                return LintValue.Of(Source.GetType().Name);
            case "runtime_types":
                return TypeChain(Source.GetType());
            case "grid":
                return LintValue.Of(IsSmallGrid ? "small" : "large");
            case "build_state":
                return LintValue.Of(IsPlanned ? Last(Source) : Source.CurrentBuildStateIndex);
            case "finished":
                return LintValue.Of(IsPlanned || Source.CurrentBuildStateIndex >= Last(Source));
            case "broken":
                return LintValue.Of(!IsPlanned && Source.IsBroken);
            case "planned":
                return LintValue.Of(IsPlanned);
            case "position":
                return LintValue.Of(Bodies.V(Pose));
            case "rotation":
                return Cube is CubeRotation turn ? LintValue.Of(LintRecords.Rotation(turn)) : LintValue.Null;
            case "mesh_box":
                return LintValue.Of(LintRecords.Box(RenderBox, Key));
            case "cells":
                return Cells();
            case "room":
                return LintValue.Of(RoomOf());
            case "outdoors":
                return LintValue.Of(RoomOf() == null);
            case "slots":
                return IsPlanned ? LintValue.EmptyList : LintRecords.Slots(Source, Key);
            case "mounted":
                return Mounted();
            case "network":
                return LintValue.Of(!IsPlanned && Source is SmallGrid piece && IsPiece ? _world.NetworkOf(piece) : null);
            case "networks":
                return LintValue.Objects(NetworkList());
            case "ports":
                return LintValue.Objects(Ports);
            case "flow":
                return LintValue.Of(FlowOf(Source));
            case "grade":
                return LintValue.Of(GradeOf(Source));
            case "max_power":
                return Source is Cable cable ? LintValue.Of(cable.MaxVoltage) : LintValue.Null;
            case "max_pressure":
                return Source is Pipe pipe ? LintValue.Of(pipe.MaxPressure.ToDouble()) : LintValue.Null;
            case "insulated":
                return LintValue.Of(Source is Pipe insulated &&
                                    (insulated.PipeType == Piping.Type.Insulated || insulated.PipeType == Piping.Type.InsulatedLowVolume));
            case "content":
                return LintValue.Of(Source is Pipe content ? ContentOf(content) : null);
            case "chip":
                return IsPlanned ? LintValue.Null : LintValue.Of(LintChips.ChipOf(_world, Source));
            default:
                throw NoField(field);
        }
    }

    private LintValue Cells()
    {
        if (IsSmallGrid)
        {
            List<LintValue> cells = new List<LintValue>(SmallCells.Count);
            foreach (GridCell cell in SmallCells)
            {
                cells.Add(LintValue.Of(_world.SmallCell(cell)));
            }

            return LintValue.Of(cells);
        }

        List<LintValue> large = new List<LintValue>(LargeCells.Count);
        foreach (GridCell cell in LargeCells)
        {
            large.Add(LintValue.Of(_world.LargeCell(cell)));
        }

        return LintValue.Of(large);
    }

    private LintValue Mounted()
    {
        MountRect? mount = FaceMounted ? Mount : null;
        if (mount == null)
        {
            return LintValue.Null;
        }

        Vec3 centre = Box3.OfSmallCells(SmallCells).Centre.With(mount.Plane.Axis, mount.Plane.Metres);
        Vec3 step = Vec3.Of(mount.Outward);
        Dictionary<string, LintValue> values = new Dictionary<string, LintValue>(StringComparer.Ordinal)
        {
            ["plane"] = LintValue.Of(mount.Plane.ToString()),
            ["outward"] = LintValue.Of(mount.Outward.Name),
            ["back"] = LintValue.Of(_world.CellAt(centre - step * 0.6, 2.0)),
            ["front"] = LintValue.Of(_world.CellAt(centre + step * 0.6, 2.0)),
            ["sections"] = LintValue.Of(mount.Faces().Count),
            ["fits_one_section"] = LintValue.Of(mount.FitsOneSection)
        };
        return LintValue.Of(new LintRecord(LintModel.Mount, Key + "/mount", values));
    }

    private List<GridCell> ReadSmallCells()
    {
        if (!IsSmallGrid)
        {
            return new List<GridCell>();
        }

        if (_plan?.Cells != null)
        {
            return new List<GridCell>(_plan.Cells);
        }

        if (!IsPlanned && Source is SmallGrid piece && IsPiece)
        {
            return new List<GridCell>(PieceShapes.Live(piece).Cells);
        }

        return IsPlanned ? Bodies.SmallCells(Source, Pose, Turn) : Bodies.SmallCells(Source);
    }

    private List<GridCell> ReadLargeCells()
    {
        List<GridCell> cells = IsSmallGrid ? new List<GridCell>() : Bodies.LargeCells(Source, Pose, Turn);
        if (cells.Count == 0 && !IsSmallGrid)
        {
            cells.Add(LargeAtPose());
        }

        return cells;
    }

    private GridCell LargeAtPose() =>
        SmallCellCode.LargeOf(new GridCell((int)Math.Round(Pose.x * 10.0), (int)Math.Round(Pose.y * 10.0),
            (int)Math.Round(Pose.z * 10.0)));

    private List<PortSubject> ReadPorts()
    {
        List<PortSubject> ports = new List<PortSubject>();
        if (!(Source is SmallGrid grid) || Cables.IsPiece(Source) || Pipes.IsPiece(Source) || Chutes.IsPiece(Source))
        {
            return ports;
        }

        if (!IsPlanned)
        {
            if (grid.OpenEnds == null)
            {
                return ports;
            }

            for (int index = 0; index < grid.OpenEnds.Count; index++)
            {
                Connection end = grid.OpenEnds[index];
                if (end?.Transform != null && ((int)end.ConnectionType & GameLintWorld.PortTypes) != 0)
                {
                    ports.Add(PortSubject.Live(_world, this, index, end));
                }
            }

            return ports;
        }

        IReadOnlyList<PieceEnd>? ends = _plan!.Ends ?? PieceShapes.Placed(Source, Pose, Turn, 0)?.Ends;
        if (ends != null)
        {
            foreach (PortCell port in PortCells.Of(ends, GameLintWorld.PortTypes))
            {
                ports.Add(PortSubject.Planned(_world, this, port));
            }
        }

        return ports;
    }

    private static int Last(Structure structure) => structure.BuildStates != null ? structure.BuildStates.Count - 1 : 0;

    /// <summary>LintModel.Thing's kind for a structure.</summary>
    internal static string KindOf(Structure structure)
    {
        if (structure is Frame)
        {
            return "frame";
        }

        if (structure is WindowShutter || structure is WallTransparent)
        {
            return Openings.KindOf(structure) == OpeningKind.Window ? "window" : "wall";
        }

        if (structure.IsDoor || structure is RoboticArmDoor)
        {
            return "door";
        }

        if (structure is Wall)
        {
            return "wall";
        }

        if (structure is Cable)
        {
            return "cable";
        }

        if (structure is Chute)
        {
            return "chute";
        }

        if (structure is Piping)
        {
            return "pipe";
        }

        if (structure is PassiveVent)
        {
            return "passive_vent";
        }

        if (structure is StructureInLineTank || structure is InLineTank)
        {
            return "in_line_tank";
        }

        return structure is SmallGrid ? "device" : "structure";
    }

    /// <summary>What a device does to a pipe flow from its inputs to its outputs, by its game class.</summary>
    internal static string? FlowOf(Structure structure) => structure switch
    {
        FiltrationMachineBase _ => "filter",
        VolumePump _ => "pump",
        PressureRegulator _ => "regulator",
        VolumeRegulator _ => "regulator",
        Mixer _ => "mixer",
        OneWayValve _ => "valve",
        Valve _ => "valve",
        _ => IsNamed(structure, "Valve") ? "valve" : IsNamed(structure, "Pump") && structure is DeviceInputOutput ? "pump" : null
    };

    private static bool IsNamed(Structure structure, string word)
    {
        for (Type? type = structure.GetType(); type != null && type != typeof(Device); type = type.BaseType)
        {
            if (type.Name.IndexOf(word, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string? GradeOf(Structure structure) => structure switch
    {
        Cable cable => cable.CableType == Cable.Type.superHeavy ? "super_heavy" : cable.CableType.ToString().ToLowerInvariant(),
        Pipe piping => piping.PipeType switch
        {
            Piping.Type.Insulated => "insulated",
            Piping.Type.NormalLowVolume => "normal_low_volume",
            Piping.Type.InsulatedLowVolume => "insulated_low_volume",
            Piping.Type.Duct => "duct",
            _ => "normal"
        },
        _ => null
    };

    private static string? ContentOf(Pipe pipe) =>
        pipe.PipeContentType == Pipe.ContentType.Gas ? "gas"
        : pipe.PipeContentType == Pipe.ContentType.Liquid ? "liquid"
        : null;

    private static LintValue TypeChain(Type type)
    {
        lock (TypeChains)
        {
            if (!TypeChains.TryGetValue(type, out LintValue chain))
            {
                List<string> names = new List<string>();
                for (Type? each = type; each != null && each != typeof(UnityEngine.Object); each = each.BaseType)
                {
                    names.Add(each.Name);
                }

                chain = LintValue.Strings(names);
                TypeChains[type] = chain;
            }

            return chain;
        }
    }
}
