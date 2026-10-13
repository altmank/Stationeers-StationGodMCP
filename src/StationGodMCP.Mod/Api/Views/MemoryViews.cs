#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>Which device's memory, from which address, and how big its stack is.</summary>
internal sealed class MemoryPlace
{
    internal MemoryPlace(string gatewayId, ThingId referenceId, int startAddress, int? stackSize)
    {
        GatewayId = gatewayId;
        ReferenceId = referenceId;
        StartAddress = startAddress;
        StackSize = stackSize;
    }

    internal string GatewayId { get; }

    internal ThingId ReferenceId { get; }

    internal int StartAddress { get; }

    internal int? StackSize { get; }
}

/// <summary>read_memory: consecutive values of a device's memory (a chip's stack, a logic memory).</summary>
internal sealed class MemoryReadView
{
    internal MemoryReadView(MemoryPlace place, List<double> values)
    {
        GatewayId = place.GatewayId;
        ReferenceId = place.ReferenceId;
        StartAddress = place.StartAddress;
        Values = values;
        Count = values.Count;
        StackSize = place.StackSize;
    }

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    public int StartAddress { get; }

    public List<double> Values { get; }

    public int Count { get; }

    /// <summary>IMemory.GetStackSize; null when the device has no stack size.</summary>
    public int? StackSize { get; }
}
