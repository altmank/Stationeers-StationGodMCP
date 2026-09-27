#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A device's memory as a chip's get and put instructions see it: IMemoryReadable.ReadMemory and
/// IMemoryWritable.WriteMemory by address, and IMemory.GetStackSize.
/// </summary>
internal static class DeviceMemory
{
    internal const int MaximumValues = 512;

    internal static int StartAddress(Args args) => args.Int("start_address", 0, int.MaxValue);

    /// <summary>Refuses a range that would run past the last address.</summary>
    internal static void RequireRange(int startAddress, int count)
    {
        if ((long)startAddress + count - 1L > int.MaxValue)
        {
            throw ApiErrors.InvalidArgument("The requested memory range exceeds the valid address space.");
        }
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

    internal static List<double> Values(Args args)
    {
        JArray array = args.Array("values", MaximumValues);
        List<double> values = new List<double>(array.Count);
        for (int index = 0; index < array.Count; index++)
        {
            JToken token = array[index];
            double value = token.Type == JTokenType.Integer || token.Type == JTokenType.Float
                ? token.Value<double>()
                : double.NaN;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw ApiErrors.InvalidArgument($"Argument 'values[{index}]' must be a finite number.");
            }

            values.Add(value);
        }

        return values;
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
