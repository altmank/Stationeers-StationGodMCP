#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// Whether a run of addresses fits a device's memory. The game's own reads and writes do not check (CODE): a
/// LogicSorter, a satellite dish or a fabricator indexes its LogicStack directly, and an address past it throws
/// StackOverflowException; a chip throws StackUnderflowException / StackOverflowException. So every address is checked
/// against IMemory.GetStackSize before the first one is touched, and a refused call reads and writes nothing.
/// </summary>
internal static class MemoryRange
{
    /// <summary>Why count values from start do not fit a memory of size values (null: size unknown); null when they do.</summary>
    internal static string? Problem(int start, int count, int? size)
    {
        long end = (long)start + count;
        if (end - 1L > int.MaxValue)
        {
            return "The requested memory range exceeds the valid address space.";
        }

        if (!(size is int known) || end <= known)
        {
            return null;
        }

        string holds = known > 0 ? $"{known} values, addresses 0 to {known - 1}" : "no values";
        return $"Addresses {start} to {end - 1} do not fit the device's memory: it holds {holds}.";
    }
}
