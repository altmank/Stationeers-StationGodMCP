#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A device's memory as a chip's get and put instructions see it: IMemoryReadable.ReadMemory and
/// IMemoryWritable.WriteMemory by address, and IMemory.GetStackSize.
/// </summary>
internal static class DeviceMemory
{
    internal const int MaximumValues = 512;

    /// <summary>
    /// Refuses a call the device's memory cannot answer, before any address is touched: a circuit holder (an IC
    /// Housing, a suit) with no chip, whose ReadMemory and WriteMemory throw NullReferenceException, and a range past
    /// the memory's last address (Pure/MemoryRange).
    /// </summary>
    internal static void RequireRange(ScopedTarget device, int startAddress, int count)
    {
        if (device.Thing is ICircuitHolder && IcTarget.FirstOccupant<ProgrammableChip>(device.Slots) == null)
        {
            throw ApiErrors.Refused("no_programmable_chip",
                $"Device {device.ReferenceId} ({device.PrefabName}) keeps its memory on a programmable chip and has " +
                "none installed.");
        }

        DeviceMemoryRange.Require(startAddress, count, StackSize(device));
    }

    internal static List<double> Read(IMemoryReadable memory, int startAddress, int count)
    {
        List<double> values = new List<double>(count);
        for (int offset = 0; offset < count; offset++)
        {
            values.Add(memory.ReadMemory(startAddress + offset));
        }

        return values;
    }

    /// <summary>
    /// A memory write's checks, which write nothing: the device in scope with writable memory, a chip where the memory
    /// is the chip's, the range inside the memory, and the memory there now when it also reads.
    /// </summary>
    internal static MemoryWritePlan Check(DeviceScope scope, ThingId id, int start, IReadOnlyList<double> values)
    {
        ScopedTarget device = Devices.Require(scope, id);
        if (!(device.Thing is IMemoryWritable memory))
        {
            throw ApiErrors.Refused("memory_not_writable",
                $"Device {device.ReferenceId} ({device.DisplayName}) has no memory a chip's put writes.");
        }

        RequireRange(device, start, values.Count);
        IMemoryReadable? readable = device.Thing as IMemoryReadable;
        List<double>? previous = readable != null ? Read(readable, start, values.Count) : null;
        return new MemoryWritePlan(Place(scope, device, start), device.DisplayName, memory, readable, values, previous);
    }

    /// <summary>A checked write, as a chip's put writes each value (IMemoryWritable.WriteMemory); the memory after.</summary>
    internal static List<double>? Write(MemoryWritePlan plan)
    {
        int start = plan.Place.StartAddress;
        for (int offset = 0; offset < plan.Values.Count; offset++)
        {
            plan.Memory.WriteMemory(start + offset, plan.Values[offset]);
        }

        return plan.Readable != null ? Read(plan.Readable, start, plan.Values.Count) : null;
    }

    internal static MemoryPlace Place(DeviceScope scope, ScopedTarget device, int startAddress) =>
        new MemoryPlace(scope.Id, new ThingId(device.ReferenceId), startAddress, StackSize(device));

    private static int? StackSize(ScopedTarget device)
    {
        try
        {
            return device.Thing is IMemory memory ? memory.GetStackSize() : null;
        }
        catch (Exception)
        {
            // IMemory.GetStackSize on a device whose memory is not set up yet.
            return null;
        }
    }
}

/// <summary>A checked memory write: where, the device's name, the values, and the memory there before.</summary>
internal sealed class MemoryWritePlan
{
    internal MemoryWritePlan(MemoryPlace place, string? name, IMemoryWritable memory, IMemoryReadable? readable,
        IReadOnlyList<double> values, List<double>? previous)
    {
        Place = place;
        Name = name;
        Memory = memory;
        Readable = readable;
        Values = values;
        Previous = previous;
    }

    public MemoryPlace Place { get; }

    public string? Name { get; }

    public IMemoryWritable Memory { get; }

    public IMemoryReadable? Readable { get; }

    public IReadOnlyList<double> Values { get; }

    public List<double>? Previous { get; }
}
