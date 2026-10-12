#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Pure.Controllers;

/// <summary>
/// A thing on a data network that can write devices on it. Enter names every device or prefab it writes to a sink
/// (an index of the network, or the match for one device); Describe makes the controller one of those stands for,
/// when it matches.
/// </summary>
internal interface IControllerSource
{
    void Enter(IControllerSink sink);

    /// <summary>The controller one of its writes stands for (op and via as it entered them).</summary>
    Controller Describe(ChipOperation? op, string? via);
}

/// <summary>Takes the writes a source names (IControllerSource.Enter).</summary>
internal interface IControllerSink
{
    /// <summary>The source writes the device with this id.</summary>
    void Targets(long referenceId, IControllerSource source, ChipOperation? op, string? via);

    /// <summary>The source batch writes every device of this prefab (with this name hash, when given).</summary>
    void Batches(int prefabHash, int? nameHash, IControllerSource source, ChipOperation? op, string? via);
}

/// <summary>
/// A program chip on the network: its writes through db (the device holding it), its pins d0..d5, an id (sd,
/// write_id) and batches (sb, sbn, sbs, batch_write...). A batch whose prefab hash is not a constant (a register) is
/// not entered.
/// </summary>
internal sealed class ProgramSource : IControllerSource
{
    private readonly long _ownerId;
    private readonly string? _ownerPrefab;
    private readonly long[] _pins;
    private readonly List<ChipOperation> _operations;

    /// <param name="ownerId">The device on the network holding the program (an IC Housing, a Console): what db names.</param>
    /// <param name="pins">The device on each pin d0..dN, 0 for none.</param>
    internal ProgramSource(long ownerId, string? ownerPrefab, long[] pins, List<ChipOperation> operations)
    {
        _ownerId = ownerId;
        _ownerPrefab = ownerPrefab;
        _pins = pins;
        _operations = operations;
    }

    public void Enter(IControllerSink sink)
    {
        foreach (ChipOperation op in _operations)
        {
            if (!op.Writes)
            {
                continue;
            }

            if (op.Pin == -1)
            {
                sink.Targets(_ownerId, this, op, "db");
            }
            else if (op.Pin is int pin && pin >= 0 && pin < _pins.Length && _pins[pin] != 0)
            {
                sink.Targets(_pins[pin], this, op, PinNames.Of(pin));
            }
            else if (op.ReferenceId is long id)
            {
                sink.Targets(id, this, op, "id");
            }
            else if (op.IsBatch && op.PrefabHash is int prefabHash)
            {
                sink.Batches(prefabHash, op.NameHash, this, op, op.Op);
            }
        }
    }

    public Controller Describe(ChipOperation? op, string? via) =>
        new Controller(ControllerKinds.Chip, _ownerId, _ownerPrefab, op!.Logic, via!, op.Line, op.IsSlot);
}

/// <summary>The names d0..d5 without a string built per write.</summary>
internal static class PinNames
{
    private static readonly string[] Names = { "d0", "d1", "d2", "d3", "d4", "d5", "d6", "d7" };

    internal static string Of(int pin) => pin >= 0 && pin < Names.Length ? Names[pin] : "d" + pin;
}

/// <summary>
/// A logic writer (or writer switch) whose output is one device, or a mirror that passes writes on to one device.
/// </summary>
internal sealed class WiredSource : IControllerSource
{
    private readonly long _targetId;
    private readonly Controller _controller;

    internal WiredSource(string kind, long referenceId, string? prefabName, long targetId, string? logicType)
    {
        _targetId = targetId;
        _controller = new Controller(kind, referenceId, prefabName, logicType,
            kind == ControllerKinds.Mirror ? "mirror" : "output", null, false);
    }

    public void Enter(IControllerSink sink)
    {
        if (_targetId != 0)
        {
            sink.Targets(_targetId, this, null, null);
        }
    }

    public Controller Describe(ChipOperation? op, string? via) => _controller;
}

/// <summary>A logic batch writer: every device of one prefab on its output network.</summary>
internal sealed class BatchWriterSource : IControllerSource
{
    private readonly int _prefabHash;
    private readonly Controller _controller;

    internal BatchWriterSource(long referenceId, string? prefabName, int prefabHash, string? logicType)
    {
        _prefabHash = prefabHash;
        _controller = new Controller(ControllerKinds.BatchWriter, referenceId, prefabName, logicType, "prefab_hash",
            null, false);
    }

    public void Enter(IControllerSink sink)
    {
        if (_prefabHash != 0)
        {
            sink.Batches(_prefabHash, null, this, null, null);
        }
    }

    public Controller Describe(ChipOperation? op, string? via) => _controller;
}
