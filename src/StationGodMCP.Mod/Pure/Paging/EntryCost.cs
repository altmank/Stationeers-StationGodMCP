#nullable enable

using System;

namespace StationGodMCP.Pure.Paging;

/// <summary>
/// What serialising one entry of a method's paged reply costs the main thread, as a moving average of the replies
/// measured, so a page sized to the frame (PageBudget) leaves room for the serialising that follows its handler. The
/// handler says how many entries its reply holds (Expect); ApiHost begins each call and gives the serialise time it
/// measured (Observed), which teaches the cost the handler named. Main thread only; nothing is allocated.
/// </summary>
internal sealed class EntryCost
{
    /// <summary>find_things' entry as measured on the game (prepared save v2: 9.9 ms for 500 entries).</summary>
    internal const double InitialMsPerEntry = 0.02;

    // A reply's weight in the average; a sample is capped so one slow reply (a collection) does not swamp it.
    private const double Weight = 0.2;
    private const double MaximumSampleMs = 1.0;

    [ThreadStatic]
    private static EntryCost? _expected;

    [ThreadStatic]
    private static int _entries;

    /// <summary>find_things' walk pages.</summary>
    internal static EntryCost FindThings { get; } = new EntryCost();

    internal double MsPerEntry { get; private set; } = InitialMsPerEntry;

    /// <summary>A call starts: no reply expected yet.</summary>
    internal static void Begin() => _expected = null;

    /// <summary>The reply being built holds entries entries of this cost's kind.</summary>
    internal void Expect(int entries)
    {
        _expected = entries > 0 ? this : null;
        _entries = entries;
    }

    /// <summary>The reply was serialised in serialiseMs: the cost its handler expected learns from it.</summary>
    internal static void Observed(double serialiseMs)
    {
        EntryCost? expected = _expected;
        _expected = null;
        if (expected != null)
        {
            double sample = Math.Min(Math.Max(serialiseMs / _entries, 0.0), MaximumSampleMs);
            expected.MsPerEntry += Weight * (sample - expected.MsPerEntry);
        }
    }
}
