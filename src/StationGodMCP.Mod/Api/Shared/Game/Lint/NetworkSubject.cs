#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Networks;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>
/// A cable, pipe or chute network of the lint model: members and devices from its own lists, cable ratings and the
/// power tick's loads (CableNetwork), a pipe network's gases, pressure, temperature and volume from its atmosphere
/// (the values of the last atmosphere tick), its weakest pipe's rating.
/// </summary>
internal sealed class NetworkSubject : LintSubject
{
    private readonly GameLintWorld _world;
    private readonly IReferencable _network;
    private readonly string _kind;
    private List<ThingSubject>? _members;
    private List<ThingSubject>? _devices;

    internal NetworkSubject(GameLintWorld world, IReferencable network, string kind)
        : base(LintModel.Network)
    {
        _world = world;
        _network = network;
        _kind = kind;
        Key = "network:" + network.ReferenceId.ToString(CultureInfo.InvariantCulture);
    }

    public override string Key { get; }

    public override long? ReferenceId => _network.ReferenceId;

    public override Vec3? Position => Members.Count > 0 ? Members[0].Position : null;

    public override string Describe => $"{_kind} network {_network.ReferenceId}";

    private UpgradeFamily Family =>
        _kind == "cable" ? new CableFamily() : _kind == "pipe" ? new PipeFamily() : (UpgradeFamily)new ChuteFamily();

    internal List<ThingSubject> Members => _members ??= Things(Read(() => Family.NetworkMembers(new ThingId(_network.ReferenceId))));

    internal List<ThingSubject> Devices => _devices ??= Things(Read(() => new List<SmallGrid>(Family.NetworkDevices(new ThingId(_network.ReferenceId)))));

    protected override LintValue Compute(string field)
    {
        Atmosphere? air = (_network as AtmosphericsNetwork)?.Atmosphere;
        CableNetwork? cables = _network as CableNetwork;
        switch (field)
        {
            case "id":
                return LintValue.Of(_network.ReferenceId);
            case "kind":
                return LintValue.Of(_kind);
            case "members":
                return LintValue.Objects(Members);
            case "devices":
                return LintValue.Objects(Devices);
            case "chips":
                return cables != null ? LintValue.Objects(LintChips.OnNetwork(_world, cables)) : LintValue.EmptyList;
            case "grades":
                return Grades();
            case "min_cable_power":
            case "max_cable_power":
                return CableRating(field == "max_cable_power");
            case "load":
                return cables != null ? LintValue.Of(cables.CurrentLoad) : LintValue.Null;
            case "required_load":
                return cables != null ? LintValue.Of(cables.RequiredLoad) : LintValue.Null;
            case "potential_load":
                return cables != null ? LintValue.Of(cables.PotentialLoad) : LintValue.Null;
            case "content":
                return LintValue.Of(_network is AtmosphericsNetwork pipes ? ContentOf(pipes) : null);
            case "gases":
                return air != null ? LintRecords.Gases(air.GasMixture) : LintValue.Of(new Dictionary<string, LintValue>());
            case "total_mol":
                return LintValue.Of(air != null ? air.GasMixture.GetTotalMolesGassesAndLiquids.ToDouble() : 0.0);
            case "pressure":
                return air != null ? LintValue.Of(air.PressureGassesAndLiquids.ToDouble()) : LintValue.Null;
            case "temperature":
                return air != null ? LintValue.Of(air.Temperature.ToDouble()) : LintValue.Null;
            case "max_pressure":
                return PipeRating();
            case "volume":
                return air != null ? LintValue.Of(air.Volume.ToDouble()) : LintValue.Null;
            case "outdoors":
                foreach (ThingSubject member in Members)
                {
                    if (member.RoomOf() == null)
                    {
                        return LintValue.True;
                    }
                }

                return LintValue.False;
            case "position":
                return LintValue.Of(Position ?? Vec3.Zero);
            default:
                throw NoField(field);
        }
    }

    private static List<SmallGrid> Read(Func<List<SmallGrid>> read)
    {
        try
        {
            return read();
        }
        catch (ApiException)
        {
            return new List<SmallGrid>();
        }
    }

    private List<ThingSubject> Things(List<SmallGrid> things)
    {
        List<ThingSubject> subjects = new List<ThingSubject>(things.Count);
        foreach (SmallGrid thing in things)
        {
            if (_world.Thing(thing) is ThingSubject subject)
            {
                subjects.Add(subject);
            }
        }

        return subjects;
    }

    private LintValue Grades()
    {
        List<(int Level, string Name)> grades = new List<(int, string)>();
        foreach (ThingSubject member in Members)
        {
            if (member.Source is Cable cable && !grades.Exists(grade => grade.Level == (int)cable.CableType))
            {
                grades.Add(((int)cable.CableType,
                    cable.CableType == Cable.Type.superHeavy ? "super_heavy" : cable.CableType.ToString().ToLowerInvariant()));
            }
        }

        grades.Sort(static (a, b) => a.Level.CompareTo(b.Level));
        List<string> names = new List<string>(grades.Count);
        foreach ((int _, string name) in grades)
        {
            names.Add(name);
        }

        return LintValue.Strings(names);
    }

    private LintValue CableRating(bool strongest)
    {
        double? rating = null;
        foreach (ThingSubject member in Members)
        {
            if (member.Source is Cable cable)
            {
                rating = rating.HasValue
                    ? strongest ? Math.Max(rating.Value, cable.MaxVoltage) : Math.Min(rating.Value, cable.MaxVoltage)
                    : cable.MaxVoltage;
            }
        }

        return LintValue.Of(rating);
    }

    private LintValue PipeRating()
    {
        double? rating = null;
        foreach (ThingSubject member in Members)
        {
            if (member.Source is Pipe pipe)
            {
                double each = pipe.MaxPressure.ToDouble();
                rating = rating.HasValue ? Math.Min(rating.Value, each) : each;
            }
        }

        return LintValue.Of(rating);
    }

    private static string? ContentOf(AtmosphericsNetwork network) =>
        network.NetworkContentType == Pipe.ContentType.Gas ? "gas"
        : network.NetworkContentType == Pipe.ContentType.Liquid ? "liquid"
        : null;
}

/// <summary>A room of the lint model: its id, size, the air at its first cell, and the region's things in it.</summary>
internal sealed class RoomSubject : LintSubject
{
    private readonly GameLintWorld _world;
    private readonly Room _room;

    internal RoomSubject(GameLintWorld world, Room room)
        : base(LintModel.Room)
    {
        _world = world;
        _room = room;
        Key = "room:" + room.RoomId.ToString(CultureInfo.InvariantCulture);
    }

    public override string Key { get; }

    public override long? ReferenceId => _room.RoomId;

    public override Vec3? Position => First is WorldGrid grid ? Bodies.V(grid.Value.ToVector3()) : (Vec3?)null;

    public override string Describe => $"room {_room.RoomId}";

    private WorldGrid? First
    {
        get
        {
            foreach (WorldGrid grid in _room.Grids)
            {
                return grid;
            }

            return null;
        }
    }

    protected override LintValue Compute(string field)
    {
        Atmosphere? air = First is WorldGrid grid ? AtmosphericsController.World?.SampleGlobalAtmosphere(grid) : null;
        switch (field)
        {
            case "id":
                return LintValue.Of(_room.RoomId);
            case "cell_count":
                return LintValue.Of(_room.Grids.Count);
            case "pressure":
                return air != null ? LintValue.Of(air.PressureGassesAndLiquids.ToDouble()) : LintValue.Null;
            case "temperature":
                return air != null ? LintValue.Of(air.Temperature.ToDouble()) : LintValue.Null;
            case "gases":
                return air != null ? LintRecords.Gases(air.GasMixture) : LintValue.Of(new Dictionary<string, LintValue>());
            case "devices":
                return LintValue.Objects(_world.ThingsIn(this));
            case "position":
                return LintValue.Of(Position ?? Vec3.Zero);
            default:
                throw NoField(field);
        }
    }
}
