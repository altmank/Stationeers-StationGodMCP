#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Api.Views;

/// <summary>
/// The profiling method's reply: the switch's state (enabled, since_s, slow_frame_ms, the CSV), the methods timed and
/// any that could not be, and the session's report (ProfileSnapshot). After off it is the last session's report.
/// </summary>
internal sealed class ProfilingView
{
    internal ProfilingView(ProfileSnapshot? report, bool enabled, int timedMethods, List<string> untimedMethods,
        string? failure)
    {
        Enabled = enabled;
        Failure = failure;
        TimedMethods = timedMethods;
        UntimedMethods = untimedMethods;
        if (report == null)
        {
            return;
        }

        SinceS = Math.Round(report.SinceS, 1);
        SlowFrameMs = report.SlowFrameMs;
        CsvPath = report.Csv?.Path;
        Csv = report.Csv != null ? new CsvView(report.Csv) : null;
        Frames = new ProfileFramesView(report);
        Scopes = report.Slots.ConvertAll(static slot => new ProfileScopeView(slot));
        WorstFrame = report.Worst != null ? new WorstFrameView(report.Worst) : null;
        Calls = report.Calls.ConvertAll(static timing => new ProfileCallView(timing));
        TickHolds = new TickHoldsView(report.TickHolds);
    }

    public bool Enabled { get; }

    /// <summary>Seconds since profiling went on or was reset; null before the first session.</summary>
    public double? SinceS { get; }

    public double? SlowFrameMs { get; }

    /// <summary>The CSV file rows go to; null without a CSV.</summary>
    public string? CsvPath { get; }

    public CsvView? Csv { get; }

    /// <summary>The internal failure that turned profiling off, if one did.</summary>
    public string? Failure { get; }

    /// <summary>Handlers and game patch methods timed while profiling is on.</summary>
    public int TimedMethods { get; }

    /// <summary>Tagged methods that could not be wrapped for timing (each logged).</summary>
    public List<string> UntimedMethods { get; }

    public ProfileFramesView? Frames { get; }

    /// <summary>Every scope and timed method that ran, the most self time first.</summary>
    public List<ProfileScopeView>? Scopes { get; }

    public WorstFrameView? WorstFrame { get; }

    /// <summary>Per call method since on or reset, the most main-thread time first.</summary>
    public List<ProfileCallView>? Calls { get; }

    public TickHoldsView? TickHolds { get; }

    internal static double Ms(double ms) => Math.Round(ms, 3);
}

/// <summary>p50, p95 and the maximum; all null without samples.</summary>
internal sealed class SpreadView
{
    internal SpreadView(Spread spread, int decimals)
    {
        P50 = Rounded(spread.P50, decimals);
        P95 = Rounded(spread.P95, decimals);
        Max = Rounded(spread.Max, decimals);
    }

    public double? P50 { get; }

    public double? P95 { get; }

    public double? Max { get; }

    private static double? Rounded(double? value, int decimals) => value.HasValue ? Math.Round(value.Value, decimals) : null;
}

/// <summary>The CSV: its file, whether rows still go to it, rows written and dropped (queue full or writer stopped), and a write failure.</summary>
internal sealed class CsvView
{
    internal CsvView(CsvState state)
    {
        Path = state.Path;
        Writing = state.Writing;
        Rows = state.Written;
        Dropped = state.Dropped;
        Error = state.Failure;
    }

    public string Path { get; }

    /// <summary>Rows still go to the file; false once the CSV was stopped or a write failed.</summary>
    public bool Writing { get; }

    public long Rows { get; }

    public long Dropped { get; }

    public string? Error { get; }
}

/// <summary>
/// StationGod's main-thread ms per frame over the window, heap growth per frame (frames whose heap shrank left out and
/// counted), and the frames over slow_frame_ms.
/// </summary>
internal sealed class ProfileFramesView
{
    internal ProfileFramesView(ProfileSnapshot report)
    {
        Recorded = report.FramesRecorded;
        Window = report.WindowFrames;
        StationgodMs = new SpreadView(report.FrameMs, 3);
        HeapDeltaBytes = new SpreadView(report.HeapDeltaBytes, 0);
        HeapDeltasDropped = report.HeapDeltasDropped;
        Slow = report.SlowFrames;
    }

    /// <summary>Frames since on or reset.</summary>
    public long Recorded { get; }

    /// <summary>The frames the percentiles cover (the last 600 at most).</summary>
    public int Window { get; }

    public SpreadView StationgodMs { get; }

    public SpreadView HeapDeltaBytes { get; }

    public int HeapDeltasDropped { get; }

    /// <summary>Frames over slow_frame_ms since on or reset.</summary>
    public long Slow { get; }
}

/// <summary>One scope or timed method: totals since on or reset, and per frame over the window.</summary>
internal sealed class ProfileScopeView
{
    internal ProfileScopeView(SlotStat slot)
    {
        Name = slot.Name;
        Kind = slot.Kind;
        TotalMs = ProfilingView.Ms(slot.TotalMs);
        SelfMs = ProfilingView.Ms(slot.SelfMs);
        Count = slot.Count;
        MeanMs = ProfilingView.Ms(slot.MeanMs);
        P95FrameMs = slot.PerFrame.P95.HasValue ? ProfilingView.Ms(slot.PerFrame.P95.Value) : null;
        MaxFrameMs = slot.PerFrame.Max.HasValue ? ProfilingView.Ms(slot.PerFrame.Max.Value) : null;
    }

    public string Name { get; }

    /// <summary>scope or method.</summary>
    public string Kind { get; }

    public double TotalMs { get; }

    /// <summary>Total less the scopes inside it.</summary>
    public double SelfMs { get; }

    public long Count { get; }

    public double MeanMs { get; }

    /// <summary>Over the window's frames it ran in; null when it ran in none of them.</summary>
    public double? P95FrameMs { get; }

    public double? MaxFrameMs { get; }
}

/// <summary>A name and its milliseconds.</summary>
internal sealed class NamedMsView
{
    internal NamedMsView(NamedMs named)
    {
        Name = named.Name;
        Ms = ProfilingView.Ms(named.Ms);
    }

    public string Name { get; }

    public double Ms { get; }
}

/// <summary>
/// The costliest frame after on or reset: its Shown costliest pieces by self ms and its Shown slowest calls; the
/// not_listed counts say how many more there were.
/// </summary>
internal sealed class WorstFrameView
{
    internal const int Shown = 8;

    internal WorstFrameView(WorstFrame worst)
    {
        Frame = worst.Frame;
        Ms = ProfilingView.Ms(worst.Ms);
        HeapDeltaBytes = worst.HeapDeltaBytes;
        Scopes = First(worst.Scopes);
        ScopesNotListed = worst.Scopes.Count - Scopes.Count;
        Calls = First(worst.Calls);
        CallsNotListed = worst.CallsNotListed + worst.Calls.Count - Calls.Count;
    }

    public long Frame { get; }

    public double Ms { get; }

    /// <summary>Null when the heap shrank in that frame (a collection ran).</summary>
    public long? HeapDeltaBytes { get; }

    public List<NamedMsView> Scopes { get; }

    public int ScopesNotListed { get; }

    public List<NamedMsView> Calls { get; }

    public int CallsNotListed { get; }

    private static List<NamedMsView> First(List<NamedMs> all)
    {
        int shown = Math.Min(Shown, all.Count);
        List<NamedMsView> first = new List<NamedMsView>(shown);
        for (int index = 0; index < shown; index++)
        {
            first.Add(new NamedMsView(all[index]));
        }

        return first;
    }
}

/// <summary>One call method since on or reset: queue wait, execute, serialise and reply size.</summary>
internal sealed class ProfileCallView
{
    internal ProfileCallView(MethodTiming timing)
    {
        Method = timing.Method;
        Calls = timing.Calls;
        Errors = timing.Errors;
        QueueWaitMs = TallyView.Of(timing.QueueWait, 3);
        ExecuteMs = TallyView.Of(timing.Handler, 3);
        SerializeMs = TallyView.Of(timing.Serialize, 3);
        ReplyBytes = TallyView.Of(timing.ReplyBytes, 0);
    }

    public string Method { get; }

    public long Calls { get; }

    public long Errors { get; }

    public TallyView QueueWaitMs { get; }

    public TallyView ExecuteMs { get; }

    public TallyView SerializeMs { get; }

    public TallyView ReplyBytes { get; }
}

/// <summary>Jobs' game-tick holds since on or reset; recent newest first, a hold under way first with holding true.</summary>
internal sealed class TickHoldsView
{
    internal TickHoldsView(TickHoldSummary summary)
    {
        Count = summary.Count;
        TotalMs = ProfilingView.Ms(summary.TotalMs);
        MaxMs = ProfilingView.Ms(summary.MaxMs);
        Recent = summary.Recent.ConvertAll(static hold => new TickHoldView(hold));
    }

    public long Count { get; }

    public double TotalMs { get; }

    public double MaxMs { get; }

    public List<TickHoldView> Recent { get; }
}

/// <summary>One tick hold: the job, its tool, how long and over how many frames.</summary>
internal sealed class TickHoldView
{
    internal TickHoldView(TickHold hold)
    {
        Job = hold.Job;
        Tool = hold.Tool;
        Ms = ProfilingView.Ms(hold.Ms);
        Frames = hold.Frames;
        Holding = hold.Holding;
    }

    public string Job { get; }

    public string? Tool { get; }

    public double Ms { get; }

    public long Frames { get; }

    public bool Holding { get; }
}

/// <summary>
/// mod_info's runtime.profiling while profiling is on: since when, StationGod's ms per frame, the worst frame and the
/// five scopes or timed methods with the most self time.
/// </summary>
internal sealed class ProfilingSummaryView
{
    internal const int TopShown = 5;

    internal ProfilingSummaryView(ProfileSnapshot report)
    {
        SinceS = Math.Round(report.SinceS, 1);
        SlowFrameMs = report.SlowFrameMs;
        FrameMs = new SpreadView(report.FrameMs, 3);
        WorstFrameMs = report.Worst != null ? ProfilingView.Ms(report.Worst.Ms) : null;
        int shown = Math.Min(TopShown, report.Slots.Count);
        Top = new List<NamedMsView>(shown);
        for (int index = 0; index < shown; index++)
        {
            Top.Add(new NamedMsView(new NamedMs(report.Slots[index].Name, report.Slots[index].SelfMs)));
        }
    }

    public double SinceS { get; }

    public double SlowFrameMs { get; }

    public SpreadView FrameMs { get; }

    public double? WorstFrameMs { get; }

    /// <summary>Self ms since on or reset, the most first.</summary>
    public List<NamedMsView> Top { get; }
}
