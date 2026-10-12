#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Protocol;

/// <summary>
/// stream_costs on the wire (protocol.md, Protocol methods): a call run on the main thread that answers what the
/// caller's own streams have cost the main thread so far (StreamCosts), {streams: [{stream, calls, main_thread_ms,
/// over_budget_frames}]}. Without streams it lists every stream of the caller's owner that has counts, in stream order;
/// with streams, each one asked, in the order asked, with 0s for a stream that has none (no call yet, or ended). Another
/// owner's streams are never read. The call's own cost is counted after its reply is built, so it is not in it.
/// </summary>
internal sealed class StreamCostCalls
{
    internal const string StreamCostsMethod = "stream_costs";

    /// <summary>The most streams one call may ask for.</summary>
    internal const int MaximumStreams = 256;

    private readonly StreamCosts _costs;

    internal StreamCostCalls(StreamCosts costs)
    {
        _costs = costs ?? throw new ArgumentNullException(nameof(costs));
    }

    internal static bool Handles(string method) => method == StreamCostsMethod;

    /// <summary>stream_costs run on the main thread at this frame: the reply message.</summary>
    internal string Run(ProtocolCall call, double queueWaitMs, long frame)
    {
        Stopwatch watch = Stopwatch.StartNew();
        CallRequest request = call.Request;
        double queueMs = Math.Round(queueWaitMs, 2);
        ErrorView? error = null;
        StreamCostsView? result = null;
        try
        {
            if (!TryStreams(request.Params, out List<long>? streams, out string problem))
            {
                throw ApiErrors.InvalidArgument(problem);
            }

            result = Read(call.Context, streams);
        }
        catch (ApiException refused)
        {
            error = new ErrorView(refused.Code, refused.Message);
        }
        catch (Exception exception)
        {
            // The request boundary: a bug in the read answers this call, never the frame.
            error = new ErrorView("internal_error", exception.Message);
        }

        double elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 2);
        bool budgetHit = CurrentFrame.BudgetReached(StopwatchClock.Instance.NowMs);
        return ApiJson.WriteShared(error != null
            ? CallReplyView.Failed(request.Id, error, elapsedMs, queueMs, frame, budgetHit)
            : CallReplyView.Of(request.Id, result!, false, elapsedMs, queueMs, frame, budgetHit));
    }

    private StreamCostsView Read(CallContext caller, List<long>? asked)
    {
        List<StreamCost> found = new List<StreamCost>();
        if (caller.IsNone)
        {
            return StreamCostsView.Of(found);
        }

        if (asked == null)
        {
            _costs.Of(caller.Owner!, found);
            return StreamCostsView.Of(found);
        }

        foreach (long stream in asked)
        {
            found.Add(_costs.Of(new CallContext(caller.Owner!, stream)));
        }

        return StreamCostsView.Of(found);
    }

    // stream_costs' params: {streams?}, an array of 1 to 256 stream numbers; anything else is named in the message.
    private static bool TryStreams(JObject? parameters, out List<long>? streams, out string problem)
    {
        streams = null;
        problem = string.Empty;
        JObject given = parameters ?? new JObject();
        foreach (JProperty property in given.Properties())
        {
            if (property.Name != "streams")
            {
                problem = $"stream_costs has no argument '{property.Name}'; it takes streams.";
                return false;
            }
        }

        JToken? token = given["streams"];
        if (token == null || token.Type == JTokenType.Null)
        {
            return true;
        }

        if (!(token is JArray listed) || listed.Count == 0 || listed.Count > MaximumStreams)
        {
            problem = $"Argument 'streams' must be a list of 1 to {MaximumStreams} stream numbers, such as [3, 4].";
            return false;
        }

        streams = new List<long>(listed.Count);
        foreach (JToken item in listed)
        {
            if (item.Type != JTokenType.Integer || (long)item < 0 || (long)item > StreamEnd.MaximumStream)
            {
                problem = "Argument 'streams' must hold stream numbers, integers from 0 to 2^53.";
                return false;
            }

            streams.Add((long)item);
        }

        return true;
    }
}

/// <summary>stream_costs' result: one entry per stream.</summary>
internal sealed class StreamCostsView
{
    private StreamCostsView(List<StreamCostView> streams) => Streams = streams;

    public List<StreamCostView> Streams { get; }

    internal static StreamCostsView Of(List<StreamCost> costs)
    {
        List<StreamCostView> streams = new List<StreamCostView>(costs.Count);
        foreach (StreamCost cost in costs)
        {
            streams.Add(new StreamCostView(cost));
        }

        return new StreamCostsView(streams);
    }
}

/// <summary>One stream's counts: calls, their whole main-thread ms (serialising included), frames over budget.</summary>
internal sealed class StreamCostView
{
    internal StreamCostView(StreamCost cost)
    {
        Stream = cost.Stream;
        Calls = cost.Calls;
        MainThreadMs = Math.Round(cost.MainThreadMs, 2);
        OverBudgetFrames = cost.OverBudgetFrames;
    }

    public long Stream { get; }

    public long Calls { get; }

    public double MainThreadMs { get; }

    public long OverBudgetFrames { get; }
}
