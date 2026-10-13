#nullable enable

using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>The memory range refusal of read memory and write memory (Pure/MemoryRange), as the call's error.</summary>
internal static class DeviceMemoryRange
{
    /// <summary>Refuses count values from start that do not fit a memory of size values (null: size unknown).</summary>
    internal static void Require(int start, int count, int? size)
    {
        string? problem = MemoryRange.Problem(start, count, size);
        if (problem != null)
        {
            throw ApiErrors.InvalidArgument(problem);
        }
    }
}
