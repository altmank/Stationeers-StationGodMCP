#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;

namespace StationGodMCP.Api.Views;

/// <summary>
/// mod_info's runtime section: what the mod costs the game since it loaded. Per method (only those called), the
/// main-thread time of the tool and of serialising its reply, the wait in the queue for the main thread, and the reply
/// size; per frame, the requests served and the time spent on them; the Mono garbage collector's counts and heap; and
/// the argument names handlers read that the catalogue does not declare (catalogue_drift, which should stay empty).
/// </summary>
internal sealed class RuntimeView
{
    internal RuntimeView(double uptimeS, long worldEpoch, FrameBudget budget, DispatchSnapshot frames,
        MemoryView memory, List<MethodTiming> methods, List<DriftCount> drift, List<ConnectionView>? connections = null)
    {
        Connections = connections;
        UptimeS = Math.Round(uptimeS, 1);
        WorldEpoch = worldEpoch;
        RequestBudgetMs = budget.Unlimited ? null : budget.LimitMs;
        Frames = new FrameStatsView(frames);
        Memory = memory;
        Methods = methods.ConvertAll(static timing => new MethodRuntimeView(timing));
        MethodCount = Methods.Count;
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

    /// <summary>Methods called at least once, the most main-thread time (handler + serialise) first.</summary>
    public List<MethodRuntimeView> Methods { get; }

    public int MethodCount { get; }

    /// <summary>Per method and argument name, reads of a name the method's catalogue entry does not declare.</summary>
    public List<CatalogueDriftView> CatalogueDrift { get; }

    /// <summary>The overlapped pipe's open connections; absent on the synchronous pipe.</summary>
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public List<ConnectionView>? Connections { get; }
}

/// <summary>
/// One open connection: the server's id for it, the key's name (anonymous without a key), the name the client gave
/// itself, transport, protocol (null before its first line), level, calls in flight and answered, bytes sent.
/// </summary>
internal sealed class ConnectionView
{
    internal ConnectionView(string clientId, string? client, string? label, string transport, int? protocol,
        int inFlight, long served, long bytesSent)
    {
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
