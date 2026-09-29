#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// read_memory: consecutive values of a device's memory (IMemoryReadable.ReadMemory: a chip's stack, a Logic
/// Sorter's, a satellite dish's), as a chip's get instruction reads them. Read only.
/// </summary>
internal static class ReadMemoryApi
{
    internal static MemoryReadView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        ScopedTarget device = Devices.Require(scope, args.ThingId("reference_id"));
        if (!(device.Thing is IMemoryReadable memory))
        {
            throw ApiErrors.Refused("memory_not_readable",
                $"Device {device.ReferenceId} does not expose readable memory.");
        }

        int start = DeviceMemory.StartAddress(args);
        int count = args.Int("count", 1, DeviceMemory.MaximumValues);
        DeviceMemory.RequireRange(device, start, count);
        List<double> values = DeviceMemory.Read(memory, start, count);
        return new MemoryReadView(DeviceMemory.Place(scope, device, start), values);
    }
}
