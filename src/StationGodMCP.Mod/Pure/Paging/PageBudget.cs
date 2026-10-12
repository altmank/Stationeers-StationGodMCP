#nullable enable

using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Pure.Paging;

/// <summary>
/// How far a walk's page may grow in the frame it runs in (protocol.md, Walks): it takes another entry while the clock,
/// plus the serialising its entries will cost once the handler returns (msPerEntry each, EntryCost), stays short of
/// untilMs. The first entry is always taken, so a walk always moves. None (default) never stops a page: outside a
/// frame, or with an unlimited budget. Nothing is allocated.
/// </summary>
internal readonly struct PageBudget
{
    private readonly IMonotonicClock? _clock;
    private readonly double _untilMs;
    private readonly double _msPerEntry;

    internal PageBudget(IMonotonicClock clock, double untilMs, double msPerEntry)
    {
        _clock = clock;
        _untilMs = untilMs;
        _msPerEntry = msPerEntry;
    }

    internal static PageBudget None => default;

    /// <summary>The running frame's budget (CurrentFrame.EndMs) on its clock; None outside a frame or without a budget.</summary>
    internal static PageBudget OfFrame(IMonotonicClock clock, double msPerEntry)
    {
        double untilMs = CurrentFrame.EndMs;
        return double.IsPositiveInfinity(untilMs) ? None : new PageBudget(clock, untilMs, msPerEntry);
    }

    /// <summary>Whether a page that holds entries things may take one more.</summary>
    internal bool Allows(int entries) =>
        _clock == null || entries == 0 || _clock.NowMs + (entries + 1) * _msPerEntry < _untilMs;
}
