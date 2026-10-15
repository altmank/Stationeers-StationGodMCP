#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Verbs;

namespace StationGodMCP.Api;

/// <summary>
/// One call in and one reply out, on the main thread, one at a time (StationGodRequestDispatcher.RunCall). A call that
/// reaches this host is neither a verb (VerbCalls) nor one of the protocol methods' own lanes (subscribe, drop_job,
/// hold_restore, stream_costs), so it is refused method_not_found. A verb document that will not load is
/// internal_error, logged.
/// </summary>
internal static class ApiHost
{
    private const int ElapsedDecimals = 2;

    /// <summary>
    /// One call: its reply message as JSON text (type reply, elapsed_ms, queue_ms, frame, budget_hit when the frame's
    /// request budget was reached as the refusal was built).
    /// </summary>
    internal static HandledRequest HandleCall(CallRequest call, double queueWaitMs, long frame)
    {
        Stopwatch watch = Stopwatch.StartNew();
        return Reply(Run(call, watch), queueWaitMs, frame);
    }

    private static HandledRequest Reply(Answer answer, double queueWaitMs, long frame)
    {
        bool budgetHit = CurrentFrame.BudgetReached(StopwatchClock.Instance.NowMs);
        double queueMs = Math.Round(queueWaitMs, ElapsedDecimals);
        long serializeStarted = Stopwatch.GetTimestamp();
        string json;
        try
        {
            json = SerializeCall(ref answer, queueMs, frame, budgetHit);
        }
        catch (Exception exception)
        {
            // A reply that will not serialise is answered internal_error, like any other failure of the answer.
            StationGodMod.LogWarning($"API reply could not be serialised: {exception}");
            answer = answer.Failed(new ErrorView("internal_error", exception.Message));
            json = SerializeCall(ref answer, queueMs, frame, budgetHit);
        }

        double serializeMs = MillisecondsSince(serializeStarted);
        MethodStats.Record(answer.Method, answer.Ok, answer.HandlerMs, serializeMs, queueWaitMs);
        return new HandledRequest(json, MethodStats.Counted(answer.Method), answer.Ok);
    }

    /// <summary>The reply message: every call that reaches this host is refused.</summary>
    private static string SerializeCall(ref Answer answer, double queueMs, long frame, bool budgetHit)
    {
        using ProfScope serializing = Prof.Scope(ProfId.CallSerialize);
        return Serialize(CallReplyView.Failed(answer.RequestId, answer.Error!, answer.HandlerMs, queueMs, frame,
            budgetHit));
    }

    /// <summary>The largest reply sent (limits.max_reply_bytes).</summary>
    internal const int MaxReplyBytes = 16777216;

    // The call's answer: one the mod does not answer is a refusal.
    private static Answer Run(CallRequest call, Stopwatch watch)
    {
        using ProfScope executing = Prof.Scope(ProfId.CallExecute);
        string? requestId = call.Id;
        string? method = call.Method;
        try
        {
            if (Document.Value.Problem is { } problem)
            {
                throw ApiErrors.Refused("internal_error", problem);
            }

            return Answer.Failure(requestId, method, UnknownMethod(method), Elapsed(watch));
        }
        catch (ApiException exception)
        {
            return Answer.Failure(requestId, method, new ErrorView(exception.Code, exception.Message, exception.Details), Elapsed(watch));
        }
        catch (GameChangedException exception)
        {
            return Answer.Failure(requestId, method, new ErrorView(ApiErrors.GameChangedCode, exception.Message), Elapsed(watch));
        }
        catch (Exception exception)
        {
            // The request boundary: a catalogue that will not load must answer the client, not break the pipe.
            StationGodMod.LogWarning($"API request failed: {exception}");
            return Answer.Failure(requestId, method, new ErrorView("internal_error", exception.Message), Elapsed(watch));
        }
    }

    /// <summary>method_not_found for a call the mod does not answer.</summary>
    private static ErrorView UnknownMethod(string? method) =>
        new ErrorView("method_not_found", $"Unknown StationGodMCP method '{method}'.");

    // The verb document, read once from the DLL. One that does not load is fatal for requests: every call is answered
    // internal_error naming the problem, rather than run unchecked.
    private static readonly Lazy<LoadedDocument> Document = new Lazy<LoadedDocument>(LoadDocument);

    /// <summary>Loads the embedded verb document now (at mod start), so the log says at once whether it loaded.</summary>
    internal static void Prepare() => _ = Document.Value;

    /// <summary>The embedded verb document (its text and hash for welcome); null when it did not load.</summary>
    internal static StationGodMCP.Pure.Catalogue.VerbDocument? Catalogue => Document.Value.Document;

    private static LoadedDocument LoadDocument()
    {
        try
        {
            StationGodMCP.Pure.Catalogue.VerbDocument document = StationGodMCP.Pure.Catalogue.VerbDocument.Embedded;
            ErrorGuide.Use(document);
            StationGodMod.Log($"Verb catalogue loaded: {document.VerbCount} verbs, {document.MethodCount} whats, " +
                              $"{document.Hash}.");
            return LoadedDocument.Of(document);
        }
        catch (CatalogueException exception)
        {
            return LoadedDocument.Failed($"The verb catalogue did not load: {exception.Message}");
        }
    }

    private sealed class LoadedDocument
    {
        private LoadedDocument(VerbDocument? document, string? problem)
        {
            Document = document;
            Problem = problem;
            if (problem != null)
            {
                StationGodMod.LogError(problem + " Every request is answered internal_error.");
            }
        }

        internal VerbDocument? Document { get; }

        internal string? Problem { get; }

        internal static LoadedDocument Of(VerbDocument document) => new LoadedDocument(document, null);

        internal static LoadedDocument Failed(string problem) => new LoadedDocument(null, problem);
    }

    /// <summary>A reply as JSON text, through the writing thread's serializer.</summary>
    internal static string Serialize(object reply) => ApiJson.WriteShared(reply);

    private static double MillisecondsSince(long timestamp) =>
        (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>The main-thread time spent on a request so far, rounded to 0.01 ms.</summary>
    internal static double Elapsed(Stopwatch watch) => Math.Round(watch.Elapsed.TotalMilliseconds, ElapsedDecimals);

}

/// <summary>A request's refusal before serialisation, and what MethodStats records of it.</summary>
internal sealed class Answer
{
    private Answer(string? requestId, string? method, ErrorView error, double handlerMs)
    {
        RequestId = requestId;
        Method = method;
        Error = error;
        HandlerMs = handlerMs;
    }

    internal string? RequestId { get; }

    internal string? Method { get; }

    internal bool Ok => false;

    internal ErrorView Error { get; }

    internal double HandlerMs { get; }

    internal static Answer Failure(string? requestId, string? method, ErrorView error, double handlerMs) =>
        new Answer(requestId, method, error, handlerMs);

    internal Answer Failed(ErrorView error) => Failure(RequestId, Method, error, HandlerMs);
}

/// <summary>
/// A handled request's reply text, its method when that is a known one (null otherwise), and whether it answered
/// without an error.
/// </summary>
internal sealed class HandledRequest
{
    internal HandledRequest(string json, string? method, bool ok)
    {
        Json = json;
        Method = method;
        Ok = ok;
    }

    internal string Json { get; }

    internal string? Method { get; }

    internal bool Ok { get; }
}

/// <summary>
/// Per-method counters since the mod loaded, for server info (methods and runtime). In memory only; nothing is logged.
/// Counted for a method the dispatcher answers only, so a client sending made-up names cannot grow the table. Thread-safe
/// (MethodTimings): the main thread records times, the listener thread the reply size.
/// </summary>
internal static class MethodStats
{
    private static readonly MethodTimings Timings = new MethodTimings();

    /// <summary>
    /// The method when the dispatcher answers it (a verb's what, find.thing), else null: the verb handlers are the one
    /// method table left.
    /// </summary>
    internal static string? Counted(string? method) =>
        method != null && VerbHandlers.Dispatcher.AnswersKey(method) ? method : null;

    internal static void Record(string? method, bool ok, double handlerMs, double serializeMs, double queueWaitMs)
    {
        string? known = Counted(method);
        if (known != null)
        {
            Timings.Record(known, ok, handlerMs, serializeMs, queueWaitMs);
            Prof.CallTimes(known, ok, handlerMs, serializeMs, queueWaitMs);
        }
    }

    /// <summary>The reply's size, from the listener thread once it has the text.</summary>
    internal static void RecordReply(string? method, long bytes)
    {
        string? known = Counted(method);
        if (known != null)
        {
            Timings.RecordReply(known, bytes);
            Prof.ReplyBytes(known, bytes);
        }
    }

    /// <summary>server info's methods entry: calls, errors and the handler's time, as before 1.9.1.</summary>
    internal static MethodStatsView ViewOf(string method)
    {
        MethodTiming timing = Timings.Snapshot(method);
        return new MethodStatsView(method, timing.Calls, timing.Errors, timing.Handler.Total, timing.Handler.Maximum);
    }

    internal static List<MethodTiming> Called() => Timings.Called();
}
