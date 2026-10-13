#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// write_memory: consecutive values written to a device's memory (IMemoryWritable.WriteMemory), as a chip's put
/// instruction writes them, with the memory read before and after when it also reads (IMemoryReadable). Writes;
/// dry_run reports the write from the same checks and writes nothing (ReportOnly).
/// </summary>
internal static class WriteMemoryApi
{
    internal static object Handle(Args args)
    {
        bool report = ReportOnly.Asked(args);
        ThingId id = args.ThingId("reference_id");
        int start = args.Int("start_address", 0, int.MaxValue);
        List<double> values = DeviceMemory.Values(args);
        return WriteCheck<MemoryWritePlan>.Run(() => Check(args, id, start, values)).Answer(report, Write, Describe);
    }

    private static MemoryWritePlan Check(Args args, ThingId id, int start, List<double> values)
    {
        DeviceScope scope = Devices.Scope(args);
        ScopedTarget device = Devices.Require(scope, id);
        if (!(device.Thing is IMemoryWritable memory))
        {
            throw ApiErrors.Refused("memory_not_writable",
                $"Device {device.ReferenceId} does not expose writable memory.");
        }

        DeviceMemory.RequireRange(device, start, values.Count);
        IMemoryReadable? readable = device.Thing as IMemoryReadable;
        List<double>? previous = readable != null ? DeviceMemory.Read(readable, start, values.Count) : null;
        return new MemoryWritePlan(DeviceMemory.Place(scope, device, start), memory, readable, values, previous);
    }

    private static object Write(MemoryWritePlan plan)
    {
        int start = plan.Place.StartAddress;
        for (int offset = 0; offset < plan.Values.Count; offset++)
        {
            plan.Memory.WriteMemory(start + offset, plan.Values[offset]);
        }

        List<double>? current = plan.Readable != null
            ? DeviceMemory.Read(plan.Readable, start, plan.Values.Count)
            : null;
        return new MemoryWriteView(plan.Place, plan.Values, plan.Previous, current);
    }

    private static object Describe(MemoryWritePlan plan) =>
        new MemoryWriteReportView(plan.Place, plan.Values, plan.Previous);

    /// <summary>A checked write: where, the values, and the memory there before.</summary>
    private sealed class MemoryWritePlan
    {
        internal MemoryWritePlan(MemoryPlace place, IMemoryWritable memory, IMemoryReadable? readable,
            List<double> values, List<double>? previous)
        {
            Place = place;
            Memory = memory;
            Readable = readable;
            Values = values;
            Previous = previous;
        }

        internal MemoryPlace Place { get; }

        internal IMemoryWritable Memory { get; }

        internal IMemoryReadable? Readable { get; }

        internal List<double> Values { get; }

        internal List<double>? Previous { get; }
    }
}
