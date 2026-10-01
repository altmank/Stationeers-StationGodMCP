#nullable enable

using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>
/// A cable, pipe or chute port of a device: its end as it stands (Connection), or as a planned device's prefab would
/// have it (PortCell). The piece of the port's kind in its joining cell is its occupant; the occupant's network is the
/// port's when the occupant joins it.
/// </summary>
internal sealed class PortSubject : LintSubject
{
    private readonly GameLintWorld _world;
    private readonly ThingSubject _device;
    private readonly Connection? _end;
    private readonly int _index;
    private readonly GridCell _cell;
    private readonly GridStep? _toward;
    private readonly int _type;
    private readonly int _role;
    private bool _occupantRead;
    private SmallGrid? _occupant;

    private PortSubject(GameLintWorld world, ThingSubject device, int index, Connection? end, GridCell cell,
        GridStep? toward, int type, int role)
        : base(LintModel.Port)
    {
        _world = world;
        _device = device;
        _index = index;
        _end = end;
        _cell = cell;
        _toward = toward;
        _type = type;
        _role = role;
        Key = $"port:{device.Key}/{index.ToString(CultureInfo.InvariantCulture)}";
    }

    internal static PortSubject Live(GameLintWorld world, ThingSubject device, int index, Connection end) =>
        new PortSubject(world, device, index, end, PieceShapes.Cell(end.GetLocalGrid()), null, (int)end.ConnectionType,
            (int)end.ConnectionRole);

    internal static PortSubject Planned(GameLintWorld world, ThingSubject device, PortCell port) =>
        new PortSubject(world, device, port.Index, null, port.Cell, port.Toward, port.Type, port.Role);

    public override string Key { get; }

    public override long? ReferenceId => _device.ReferenceId;

    public override Vec3? Position => Bodies.V(PieceShapes.CentreOf(_cell));

    public override string Describe => $"{_device.Describe} port {_index}";

    protected override LintValue Compute(string field)
    {
        switch (field)
        {
            case "device":
                return LintValue.Of(_device);
            case "index":
                return LintValue.Of(_index);
            case "type":
                return LintValue.Of(((NetworkType)_type).ToString());
            case "role":
                return LintValue.Of(((ConnectionRole)_role).ToString());
            case "flow":
                return LintValue.Of(Flow());
            case "joining_cell":
                return LintValue.Of(_world.SmallCell(_cell));
            case "network":
                return LintValue.Of(NetworkOf());
            case "occupant":
                return LintValue.Of(_world.Thing(Occupant()));
            case "joined":
                return LintValue.Of(Joined());
            case "position":
                return LintValue.Of(Bodies.V(PieceShapes.CentreOf(_cell)));
            default:
                throw NoField(field);
        }
    }

    internal NetworkSubject? NetworkOf()
    {
        SmallGrid? occupant = Occupant();
        return occupant != null && Joined() ? _world.NetworkOf(occupant) : null;
    }

    private SmallGrid? Occupant()
    {
        if (!_occupantRead)
        {
            _occupantRead = true;
            SmallCell? cell = GridController.World.GetSmallCell(PieceShapes.Grid(_cell));
            SmallGrid? piece = cell == null ? null
                : (_type & (int)(NetworkType.Pipe | NetworkType.PipeLiquid)) != 0 ? cell.Pipe
                : (_type & (int)NetworkType.Chute) != 0 ? cell.Chute
                : (SmallGrid?)cell.Cable;
            _occupant = piece != null && !piece.IsBeingDestroyed && !_world.IsGone(piece) ? piece : null;
        }

        return _occupant;
    }

    private bool Joined()
    {
        SmallGrid? piece = Occupant();
        if (piece == null)
        {
            return false;
        }

        if (_end != null)
        {
            return piece.IsConnected(_end);
        }

        return _toward.HasValue && EndSet.AtCell(PieceShapes.Live(piece), _cell).Contains(_toward.Value);
    }

    // in or out: the device's own input and output fields first (the game moves gas by them), then the role's name.
    private string? Flow()
    {
        if (_end != null && _device.Source is DeviceInputOutput io)
        {
            if (Same(io.InputConnection) || Same(io.InputConnection2))
            {
                return "in";
            }

            if (Same(io.OutputConnection) || Same(io.OutputConnection2))
            {
                return "out";
            }
        }

        string role = ((ConnectionRole)_role).ToString();
        return TwoWayChutePorts.FlowOf(_device.Source, _type, role);
    }

    private bool Same(Connection? other) =>
        other != null && (ReferenceEquals(other, _end) || (other.Transform != null && other.Transform == _end!.Transform));
}
