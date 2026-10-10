#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;

using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Views;

/// <summary>
/// mod_info's runtime section: what the mod costs the game since it loaded. Per method (only those called), the
/// main-thread time of the tool and of serialising its reply, the wait in the queue for the main thread, and the reply
/// size; per frame, the requests served and the time spent on them; the Mono garbage collector's counts and heap; and
/// the argument names handlers read that the catalogue does not declare (catalogue_drift, which should stay empty).
/// </summary>
internal sealed class RuntimeView : ITruncatingView
{
    internal RuntimeView(double uptimeS, long worldEpoch, FrameBudget budget, DispatchSnapshot frames,
        MemoryView memory, List<MethodTiming> methods, List<DriftCount> drift, List<ConnectionView>? connections = null,
        ProfilingSummaryView? profiling = null, JobSettlesView? jobSettles = null, PrefabIndexView? prefabIndex = null,
        GameCountersView? counters = null)
    {
        PrefabIndex = prefabIndex ?? new PrefabIndexView(false, false, 0, 0, 0, 0);
        JobHolds = counters?.JobHolds;
        PrintLog = counters?.PrintLog;
        LintChipPrograms = counters?.LintChipPrograms;
        BatchConsole = counters?.BatchConsole;
        Connections = connections;
        Profiling = profiling;
        JobSettles = jobSettles ?? new JobSettlesView(0, 0, 0);
        UptimeS = Math.Round(uptimeS, 1);
        WorldEpoch = worldEpoch;
        RequestBudgetMs = budget.Unlimited ? null : budget.LimitMs;
        Frames = new FrameStatsView(frames);
        Memory = memory;
        int listed = Math.Min(methods.Count, ReplyDefaults.RuntimeMethods);
        Methods = new List<MethodRuntimeView>(listed);
        for (int index = 0; index < listed; index++)
        {
            Methods.Add(new MethodRuntimeView(methods[index]));
        }

        MethodCount = methods.Count;
        CatalogueDrift = drift.ConvertAll(static count => new CatalogueDriftView(count));
    }

    /// <summary>Seconds since the mod loaded (real time).</summary>
    public double UptimeS { get; }

    /// <summary>Worlds left since the mod loaded; each one cleared the per-world state (IC pauses, jobs, logs).</summary>
    public long WorldEpoch { get; }

    /// <summary>Main-thread ms per frame for requests ([Performance] RequestBudgetMs); null = unlimited.</summary>
    public double? RequestBudgetMs { get; }

    public FrameStatsView Frames { get; }

    public MemoryView Memory { get; }

    /// <summary>
    /// The methods called at least once that cost the most main-thread time (handler + serialise), most first, up to
    /// ReplyDefaults.RuntimeMethods; method_count counts them all.
    /// </summary>
    public List<MethodRuntimeView> Methods { get; }

    public int MethodCount { get; }

    /// <summary>Per method and argument name, reads of a name the method's catalogue entry does not declare.</summary>
    public List<CatalogueDriftView> CatalogueDrift { get; }

    /// <summary>The overlapped pipe's open connections; absent on the synchronous pipe.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public List<ConnectionView>? Connections { get; }

    public JobSettlesView JobSettles { get; }

    public PrefabIndexView PrefabIndex { get; }

    /// <summary>The game counters (GameCountersView), only with include_counters.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public JobHoldsView? JobHolds { get; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public PrintLogView? PrintLog { get; }

    /// <summary>Chip programs the lint cache holds parsed (LintChipPrograms).</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public int? LintChipPrograms { get; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public BatchConsoleView? BatchConsole { get; }

    /// <summary>The profiler's summary while profiling is on; absent otherwise.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public ProfilingSummaryView? Profiling { get; }

    public void NoteTruncations(string path) =>
        Truncations.Note(path + "methods", Methods.Count, MethodCount,
            "no argument lists more: the costliest methods are listed first");
}

/// <summary>
/// One open connection: the server's id for it, the key's name (anonymous without a key), the name the client gave
/// itself, transport, protocol (null before its first line), level, calls in flight and answered, bytes sent, the
/// subscriptions it holds, and its most-called methods (at most MethodsShown; method_count counts them all).
/// </summary>
internal sealed class ConnectionView
{
    internal const int MethodsShown = 3;

    internal ConnectionView(string clientId, string? client, string? label, string transport, int? protocol,
        int inFlight, long served, long bytesSent, ConnectionCalls? calls = null, int subscriptions = 0)
    {
        Methods = (calls?.Top(MethodsShown) ?? new List<MethodCallCount>()).ConvertAll(
            static count => new ConnectionMethodView(count));
        MethodCount = calls?.MethodCount ?? 0;
        Subscriptions = subscriptions;
        ClientId = clientId;
        Client = client;
        Label = label;
        Transport = transport;
        Protocol = protocol;
        InFlight = inFlight;
        Served = served;
        BytesSent = bytesSent;
    }

    public string ClientId { get; }

    public string? Client { get; }

    public string? Label { get; }

    public string Transport { get; }

    public int? Protocol { get; }

    public int InFlight { get; }

    public long Served { get; }

    public long BytesSent { get; }

    /// <summary>Subscriptions the connection holds.</summary>
    public int Subscriptions { get; }

    /// <summary>The connection's most-called methods, most first.</summary>
    public List<ConnectionMethodView> Methods { get; }

    /// <summary>Methods the connection called at least once.</summary>
    public int MethodCount { get; }
}

/// <summary>
/// Pipe jobs' settles after each piece since the mod loaded: run (a hop to a pool thread to apply the game's queued
/// gas changes), skipped (nothing was queued), and unchecked (run because the game's queues could not be read).
/// </summary>
internal sealed class JobSettlesView
{
    internal JobSettlesView(long run, long skipped, long notChecked)
    {
        Run = run;
        Skipped = skipped;
        Unchecked = notChecked;
    }

    public long Run { get; }

    public long Skipped { get; }

    public long Unchecked { get; }
}

/// <summary>One method a connection called: calls, tool errors and main-thread ms in total.</summary>
internal sealed class ConnectionMethodView
{
    internal ConnectionMethodView(MethodCallCount count)
    {
        Method = count.Method;
        Calls = count.Calls;
        Errors = count.Errors;
        TotalMs = Math.Round(count.TotalMs, 2);
    }

    public string Method { get; }

    public long Calls { get; }

    public long Errors { get; }

    public double TotalMs { get; }
}

/// <summary>One undeclared argument name a method's handler read since the mod loaded, and how often.</summary>
internal sealed class CatalogueDriftView
{
    internal CatalogueDriftView(DriftCount count)
    {
        Method = count.Method;
        Argument = count.Argument;
        Reads = count.Reads;
    }

    public string Method { get; }

    public string Argument { get; }

    public long Reads { get; }
}

/// <summary>A total, mean and maximum, in milliseconds (0.01) or bytes (whole).</summary>
internal sealed class TallyView
{
    private TallyView(double total, double? mean, double max)
    {
        Total = total;
        Mean = mean;
        Max = max;
    }

    public double Total { get; }

    /// <summary>Null before the first sample.</summary>
    public double? Mean { get; }

    public double Max { get; }

    internal static TallyView Of(Tally tally, int decimals) =>
        new TallyView(Math.Round(tally.Total, decimals),
            tally.Mean.HasValue ? Math.Round(tally.Mean.Value, decimals) : null, Math.Round(tally.Maximum, decimals));
}

/// <summary>One method's runtime counters.</summary>
internal sealed class MethodRuntimeView
{
    private const int MsDecimals = 2;

    internal MethodRuntimeView(MethodTiming timing)
    {
        Method = timing.Method;
        Calls = timing.Calls;
        Errors = timing.Errors;
        MainThreadMs = Math.Round(timing.MainThreadMs, MsDecimals);
        HandlerMs = TallyView.Of(timing.Handler, MsDecimals);
        SerializeMs = TallyView.Of(timing.Serialize, MsDecimals);
        QueueWaitMs = TallyView.Of(timing.QueueWait, MsDecimals);
        ReplyBytes = TallyView.Of(timing.ReplyBytes, 0);
    }

    public string Method { get; }

    public long Calls { get; }

    public long Errors { get; }

    /// <summary>Handler plus serialisation, in total: what the method cost the main thread.</summary>
    public double MainThreadMs { get; }

    /// <summary>Parsing, the argument check and the tool, up to the reply object (the reply's elapsed_ms).</summary>
    public TallyView HandlerMs { get; }

    /// <summary>The reply object to JSON text, on the main thread.</summary>
    public TallyView SerializeMs { get; }

    /// <summary>From the request being queued to the main thread starting it: mostly waiting for the next frame.</summary>
    public TallyView QueueWaitMs { get; }

    /// <summary>The reply line's UTF-8 size.</summary>
    public TallyView ReplyBytes { get; }
}

/// <summary>The dispatcher's frames: only frames that served at least one request count.</summary>
internal sealed class FrameStatsView
{
    private const int Decimals = 2;

    internal FrameStatsView(DispatchSnapshot snapshot)
    {
        BusyFrames = snapshot.BusyFrames;
        RequestsServed = snapshot.Served;
        RequestsPerFrameMean = snapshot.MeanPerFrame.HasValue ? Math.Round(snapshot.MeanPerFrame.Value, Decimals) : null;
        RequestsPerFrameMax = snapshot.MaxPerFrame;
        FrameMs = TallyView.Of(snapshot.FrameMs, Decimals);
        BudgetStops = snapshot.BudgetStops;
        Expired = snapshot.Expired;
    }

    public long BusyFrames { get; }

    public long RequestsServed { get; }

    public double? RequestsPerFrameMean { get; }

    public int RequestsPerFrameMax { get; }

    /// <summary>Main-thread ms spent on requests in one frame.</summary>
    public TallyView FrameMs { get; }

    /// <summary>Frames the request budget ended with requests still queued (they ran the next frame).</summary>
    public long BudgetStops { get; }

    /// <summary>Requests the main thread did not reach before their client gave up (game_timeout).</summary>
    public long Expired { get; }
}

/// <summary>
/// The garbage collector and the Mono heap. Unity's Mono uses the Boehm collector: one generation, and a heap that
/// does not shrink. The two Profiler sizes are null where this build of the game does not report them (0).
/// </summary>
internal sealed class MemoryView
{
    internal MemoryView(int gcCollections, int gcMaxGeneration, long gcTotalMemoryBytes, long? monoUsedBytes,
        long? monoHeapBytes)
    {
        GcCollections = gcCollections;
        GcMaxGeneration = gcMaxGeneration;
        GcTotalMemoryBytes = gcTotalMemoryBytes;
        MonoUsedBytes = monoUsedBytes;
        MonoHeapBytes = monoHeapBytes;
    }

    /// <summary>GC.CollectionCount(0): collections since the game started.</summary>
    public int GcCollections { get; }

    public int GcMaxGeneration { get; }

    /// <summary>GC.GetTotalMemory(false): bytes the collector thinks are allocated.</summary>
    public long GcTotalMemoryBytes { get; }

    /// <summary>Profiler.GetMonoUsedSizeLong.</summary>
    public long? MonoUsedBytes { get; }

    /// <summary>Profiler.GetMonoHeapSizeLong: the reserved heap, which never shrinks.</summary>
    public long? MonoHeapBytes { get; }
}

/// <summary>
/// The prefab index (ThingIndex): ready (lists filtered by prefab read it; false: they walk every thing), verify
/// ([Performance] VerifyPrefabIndex), things and names filed, with verify the queries checked against a full walk
/// and those that differed, and with include_counters the things the game's master lists reported taking in (arrived)
/// and letting go (left) since the mod loaded.
/// </summary>
internal sealed class PrefabIndexView
{
    internal PrefabIndexView(bool ready, bool verify, int things, int names, long verified, long differences,
        long? arrived = null, long? left = null)
    {
        Ready = ready;
        Verify = verify;
        Things = things;
        Names = names;
        Verified = verified;
        Differences = differences;
        Arrived = arrived;
        Left = left;
    }

    public bool Ready { get; }

    public bool Verify { get; }

    public int Things { get; }

    public int Names { get; }

    public long Verified { get; }

    public long Differences { get; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public long? Arrived { get; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public long? Left { get; }
}

/// <summary>
/// The counters mod_info's runtime lists beside prefab_index with include_counters: job tick holds, the print log, the
/// lint chip-program cache and a dedicated server's console lines.
/// </summary>
internal sealed class GameCountersView
{
    internal GameCountersView(JobHoldsView jobHolds, PrintLogView printLog, int lintChipPrograms,
        BatchConsoleView batchConsole)
    {
        JobHolds = jobHolds;
        PrintLog = printLog;
        LintChipPrograms = lintChipPrograms;
        BatchConsole = batchConsole;
    }

    internal JobHoldsView JobHolds { get; }

    internal PrintLogView PrintLog { get; }

    internal int LintChipPrograms { get; }

    internal BatchConsoleView BatchConsole { get; }
}

/// <summary>
/// Jobs' holds of the game tick since the mod loaded, profiling on or off, by length (HoldLengths): each stretch from
/// a job asking for the hold to letting the tick go counts once. Each bucket includes its lower edge.
/// </summary>
internal sealed class JobHoldsView
{
    internal JobHoldsView(long[] counts)
    {
        Under100Ms = counts[0];
        Under1S = counts[1];
        Under10S = counts[2];
        Under60S = counts[3];
        Over60S = counts[4];
    }

    [Newtonsoft.Json.JsonProperty("under_100ms")]
    public long Under100Ms { get; }

    [Newtonsoft.Json.JsonProperty("under_1s")]
    public long Under1S { get; }

    [Newtonsoft.Json.JsonProperty("under_10s")]
    public long Under10S { get; }

    [Newtonsoft.Json.JsonProperty("under_60s")]
    public long Under60S { get; }

    /// <summary>60 s or longer.</summary>
    [Newtonsoft.Json.JsonProperty("over_60s")]
    public long Over60S { get; }
}

/// <summary>Print provenance's log (Prints.Log): records held, and the most it keeps before dropping the oldest.</summary>
internal sealed class PrintLogView
{
    internal PrintLogView(int records, int capacity)
    {
        Records = records;
        Capacity = capacity;
    }

    public int Records { get; }

    public int Capacity { get; }
}

/// <summary>
/// The lines a dedicated (batch-mode) server printed to its console since the mod loaded, and their rate per minute
/// over the last full minute (EventRate). Both stay 0 on a game with a window.
/// </summary>
internal sealed class BatchConsoleView
{
    internal BatchConsoleView(long lines, double linesPerMinute)
    {
        Lines = lines;
        LinesPerMinute = Math.Round(linesPerMinute, 1);
    }

    public long Lines { get; }

    public double LinesPerMinute { get; }
}
