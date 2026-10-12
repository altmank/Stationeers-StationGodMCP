#nullable enable

using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Pure.Paging;

/// <summary>
/// How far one frame's share of a page may go (FramedPage): it takes another entry while the clock is short of
/// untilMs, and always the first, so a page taken over frames always moves. The rest of the page waits for the next
/// frame; the reply is not cut. None (default) never stops. Nothing is allocated.
/// </summary>
internal readonly struct PageBudget
{
    private readonly IMonotonicClock? _clock;
    private readonly double _untilMs;

    internal PageBudget(IMonotonicClock clock, double untilMs)
    {
        _clock = clock;
        _untilMs = untilMs;
    }

    internal static PageBudget None => default;

    /// <summary>Whether a share that holds entries things may take one more.</summary>
    internal bool Allows(int entries) => _clock == null || entries == 0 || _clock.NowMs < _untilMs;
}
