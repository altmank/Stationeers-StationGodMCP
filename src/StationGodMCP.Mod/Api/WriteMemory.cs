#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// write_memory: consecutive values written to a device's memory (IMemoryWritable.WriteMemory), as a chip's put
/// instruction writes them, with the memory read before and after when it also reads (IMemoryReadable). Writes.
/// </summary>
internal static class WriteMemoryApi
{
    internal static MemoryWriteView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        ScopedTarget device = Devices.Require(scope, args.ThingId("reference_id"));
        if (!(device.Thing is IMemoryWritable memory))
        {
            throw ApiErrors.Refused("memory_not_writable",
                $"Device {device.ReferenceId} does not expose writable memory.");
        }

        int start = DeviceMemory.StartAddress(args);
        List<double> values = DeviceMemory.Values(args);
        DeviceMemory.RequireRange(start, values.Count);
        IMemoryReadable? readable = device.Thing as IMemoryReadable;
        List<double>? previous = readable != null ? DeviceMemory.Read(readable, start, values.Count) : null;
        for (int offset = 0; offset < values.Count; offset++)
        {
            memory.WriteMemory(start + offset, values[offset]);
        }

        List<double>? current = readable != null ? DeviceMemory.Read(readable, start, values.Count) : null;
        return new MemoryWriteView(DeviceMemory.Place(scope, device, start), values, previous, current);
    }
}
