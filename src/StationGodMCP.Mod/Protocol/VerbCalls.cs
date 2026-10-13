#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Protocol;

/// <summary>One what's call timed: its key (find.thing), whether it answered, its handler, serialising and queue ms.</summary>
internal delegate void VerbTimed(string what, bool ok, double handlerMs, double serializeMs, double queueWaitMs);

/// <summary>
/// What verb calls run with: the (verb, what) table, the largest reply sent, the frame a reply names, and where each
/// answered call's times go (MethodStats in the mod; null keeps none).
/// </summary>
internal sealed class VerbRunner
{
    internal VerbRunner(VerbDispatcher verbs, int maxReplyBytes, Func<long> frame, VerbTimed? timed = null)
    {
        Verbs = verbs;
        MaxReplyBytes = maxReplyBytes;
        Frame = frame;
        Timed = timed;
    }

    public VerbDispatcher Verbs { get; }

    public int MaxReplyBytes { get; }

    public Func<long> Frame { get; }

    public VerbTimed? Timed { get; }
}

/// <summary>
/// A verb call run on the main thread (protocol.md, Verb calls): the dispatcher binds and runs its (verb, what) handler,
/// and the reply message is the handler's reply shaped by the call's shape, or its refusal. A reply larger than
/// max_reply_bytes (or the shape's max_bytes) is answered reply_too_large; a handler's failure is internal_error. A
/// handler whose answer is not ready (VerbOutcome.Pending) leaves the call deferred: its work is stepped in the slice
/// lane (PendingVerbCall) and answers through the same writer once ready. Each answer is counted under the call's what
/// (its profile's key, find.thing), and its serialise time teaches the entry cost its handler named (EntryCost).
/// </summary>
internal static class VerbCalls
{
    private const int ElapsedDecimals = 2;

    internal static bool Handles(string? method) => VerbSurface.Verb(method) != null;

    /// <summary>The verb call at this frame: its reply message, or its work over later frames.</summary>
    internal static CallOutcome Run(VerbRunner runner, ProtocolCall call, double queueWaitMs)
    {
        string what = call.Profile.Method;
        Stopwatch watch = Stopwatch.StartNew();
        EntryCost.Begin();
        CallRequest request = call.Request;
        VerbOutcome outcome = Handled(() =>
            runner.Verbs.Run(request.Method, request.Params, request.Shape, request.ArgumentNamesChecked));
        double handlerMs = watch.Elapsed.TotalMilliseconds;
        return outcome is VerbOutcome.Pending pending
            ? CallOutcome.Continued(new PendingVerbCall(runner, request, what, pending.Work, handlerMs, queueWaitMs), what)
            : new CallOutcome(Write(runner, request, what, outcome, handlerMs, queueWaitMs), what);
    }

    /// <summary>
    /// The reply message of an answered or refused call: elapsed_ms its handler time, budget_hit when the frame's
    /// budget is reached as it is written; the serialise time is observed (EntryCost) and the call timed.
    /// </summary>
    internal static string Write(VerbRunner runner, CallRequest request, string what, VerbOutcome outcome,
        double handlerMs, double queueWaitMs)
    {
        double elapsedMs = Math.Round(handlerMs, ElapsedDecimals);
        double queueMs = Math.Round(queueWaitMs, ElapsedDecimals);
        long frame = runner.Frame();
        bool budgetHit = CurrentFrame.BudgetReached(StopwatchClock.Instance.NowMs);
        long started = Stopwatch.GetTimestamp();
        bool ok;
        string json;
        switch (outcome)
        {
            case VerbOutcome.Answered answered:
                ShapeRequest shape = request.Shape ?? ShapeRequest.None;
                ShapedText shaped = ApiJson.WriteReply(
                    CallReplyView.Of(request.Id, answered.Reply, request.Shape != null, elapsedMs, queueMs, frame,
                        budgetHit), shape, Array.Empty<Truncation>());
                int bytes = Encoding.UTF8.GetByteCount(shaped.Json);
                int limit = Math.Min(shape.MaxBytes ?? runner.MaxReplyBytes, runner.MaxReplyBytes);
                ok = bytes <= limit;
                json = ok
                    ? shaped.Json
                    : ApiJson.WriteShared(CallReplyView.Failed(request.Id, ReplyTooLarge.Of(bytes, limit, shaped.Outcome),
                        elapsedMs, queueMs, frame, budgetHit));
                break;
            case VerbOutcome.Refused refused:
                ok = false;
                json = ApiJson.WriteShared(CallReplyView.Failed(request.Id,
                    new ErrorView(refused.Error.Code, refused.Error.Message, refused.Error.Data), elapsedMs, queueMs,
                    frame, budgetHit));
                break;
            default:
                throw new InvalidOperationException("A verb call is written once it is answered or refused.");
        }

        double serializeMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        EntryCost.Observed(serializeMs);
        runner.Timed?.Invoke(what, ok, handlerMs, serializeMs, queueWaitMs);
        return json;
    }

    /// <summary>
    /// A handler's run at the request boundary: an ApiException is its refusal, a game member this build lacks
    /// game_changed, any other failure internal_error.
    /// </summary>
    internal static VerbOutcome Handled(Func<VerbOutcome> run)
    {
        try
        {
            return run();
        }
        catch (ApiException refused)
        {
            return new VerbOutcome.Refused(new VerbError(refused.Code, refused.Message,
                refused.Details == null ? null : JToken.FromObject(refused.Details, ApiJson.Fresh())));
        }
        catch (GameChangedException changed)
        {
            return new VerbOutcome.Refused(new VerbError(ApiErrors.GameChangedCode, changed.Message));
        }
        catch (Exception exception)
        {
            // The request boundary: a handler's bug answers this call, never the frame.
            ProtocolLog.Warning($"A verb call failed: {exception}");
            return new VerbOutcome.Refused(new VerbError("internal_error", exception.Message));
        }
    }
}

/// <summary>
/// A deferred verb call's work: its pending result stepped over frames, the main-thread time of every slice summed for
/// its reply's elapsed_ms, then its answer through VerbCalls.Write, the entry cost begun anew for that slice. A step
/// that throws ends the work, and the call answers the failure as a handler's would.
/// </summary>
internal sealed class PendingVerbCall : IDeferredWork
{
    private readonly VerbRunner _runner;
    private readonly CallRequest _request;
    private readonly string _what;
    private readonly double _queueWaitMs;
    private IPendingResult _pending;
    private double _spentMs;

    internal PendingVerbCall(VerbRunner runner, CallRequest request, string what, IPendingResult pending,
        double spentMs, double queueWaitMs)
    {
        _runner = runner;
        _request = request;
        _what = what;
        _pending = pending;
        _spentMs = spentMs;
        _queueWaitMs = queueWaitMs;
    }

    public bool Step(double untilMs)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            return _pending.Step(untilMs);
        }
        catch (Exception exception)
        {
            _pending = new Thrown(ExceptionDispatchInfo.Capture(exception));
            return true;
        }
        finally
        {
            _spentMs += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        }
    }

    public string Finish()
    {
        long started = Stopwatch.GetTimestamp();
        EntryCost.Begin();
        VerbOutcome outcome = VerbCalls.Handled(() => VerbOutcome.Of(_pending.Result()));
        _spentMs += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        return VerbCalls.Write(_runner, _request, _what, outcome, _spentMs, _queueWaitMs);
    }

    // A step's failure, raised again where the answer is built.
    private sealed class Thrown : IPendingResult
    {
        private readonly ExceptionDispatchInfo _failure;

        internal Thrown(ExceptionDispatchInfo failure) => _failure = failure;

        public bool Step(double untilMs) => true;

        public object Result()
        {
            _failure.Throw();
            return null!;
        }
    }
}
