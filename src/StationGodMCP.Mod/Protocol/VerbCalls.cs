#nullable enable

using System;
using System.Collections.Generic;
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
using StationGodMCP.Pure.Profiling;

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
/// What a handler adds to its reply on the side while it runs (PLAN 2.0 cut notes): the lists it cut (Truncations,
/// written as truncated by the shaping writer) and the gas hold its job met (GasHoldReply, added as gas_hold). Gathered
/// around each run of the handler's code, a deferred call's slices too, and written with its answer.
/// </summary>
internal sealed class VerbNotes
{
    private readonly List<Truncation> _truncations = new List<Truncation>();
    private GasHoldView? _gasHold;

    internal IReadOnlyList<Truncation> Truncations => _truncations;

    /// <summary>Starts gathering for a run of the handler's code.</summary>
    internal static void Begin()
    {
        Pure.Shaping.Truncations.Begin();
        GasHoldReply.Begin();
    }

    /// <summary>Adds what the run since Begin noted; a later gas hold replaces an earlier one.</summary>
    internal void Take()
    {
        Pure.Shaping.Truncations.MoveInto(_truncations);
        _gasHold = GasHoldReply.Take() ?? _gasHold;
    }

    /// <summary>The reply with gas_hold added when the run met one (its keys spread, nothing copied).</summary>
    internal object Attach(object reply) =>
        _gasHold == null ? reply : new ReplyObject().Spread(reply).Add("gas_hold", _gasHold);
}

/// <summary>
/// A verb call run on the main thread (protocol.md, Verb calls): the dispatcher binds and runs its (verb, what) handler,
/// and the reply message is the handler's reply shaped by the call's shape, or its refusal. A reply larger than
/// max_reply_bytes (or the shape's max_bytes) is answered reply_too_large; a handler's failure is internal_error. A
/// handler whose answer is not ready (VerbOutcome.Pending) leaves the call deferred: its work is stepped in the slice
/// lane (PendingVerbCall) and answers through the same writer once ready. Each answer is counted under the call's what
/// (its profile's key, find.thing) when the connection's writer has written it.
/// </summary>
internal static class VerbCalls
{


    internal static bool Handles(string? method) => VerbSurface.Verb(method) != null;

    /// <summary>The verb call at this frame: its reply message, or its work over later frames.</summary>
    internal static CallOutcome Run(VerbRunner runner, ProtocolCall call, double queueWaitMs)
    {
        string what = call.Profile.Method;
        Stopwatch watch = Stopwatch.StartNew();
        CallRequest request = call.Request;
        VerbNotes notes = new VerbNotes();
        VerbNotes.Begin();
        VerbOutcome outcome = Handled(() =>
            runner.Verbs.Run(request.Method, request.Params, request.Shape, request.ArgumentNamesChecked));
        notes.Take();
        double handlerMs = watch.Elapsed.TotalMilliseconds;
        return outcome is VerbOutcome.Pending pending
            ? CallOutcome.Continued(
                new PendingVerbCall(runner, request, what, pending.Work, handlerMs, queueWaitMs, notes), what)
            : new CallOutcome(Write(runner, request, what, outcome, handlerMs, queueWaitMs, notes), what);
    }

    /// <summary>
    /// The reply line of an answered or refused call: elapsed_ms its handler time, budget_hit when the frame's budget
    /// is reached as it is handed over. The game thread hands over the plain reply; the connection's writer thread
    /// writes it (CallReplyLine), where the call is timed.
    /// </summary>
    internal static ReplyLine Write(VerbRunner runner, CallRequest request, string what, VerbOutcome outcome,
        double handlerMs, double queueWaitMs, VerbNotes? notes = null)
    {
        if (outcome is VerbOutcome.Pending)
        {
            throw new InvalidOperationException("A verb call is written once it is answered or refused.");
        }

        return ReplyLine.Of(new CallReplyLine(runner, request, what, outcome, notes ?? new VerbNotes(), handlerMs,
            queueWaitMs, runner.Frame(), CurrentFrame.BudgetReached(StopwatchClock.Instance.NowMs)));
    }

    /// <summary>
    /// A handler's run (Handled) for a lane with its own share, which ends at untilMs: work the handler steps in its
    /// own call (WorkResult) stops there instead of at the frame's end (review D, R-D-8: a find subscription's
    /// evaluation).
    /// </summary>
    internal static VerbOutcome HandledUntil(double untilMs, Func<VerbOutcome> run)
    {
        using (CurrentFrame.Capped(untilMs))
        {
            return Handled(run);
        }
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
                refused.Details == null ? null : JToken.FromObject(refused.Details, ApiJson.Serializer)));
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
    private readonly VerbNotes _notes;
    private IPendingResult _pending;
    private VerbOutcome? _outcome;
    private double _spentMs;

    internal PendingVerbCall(VerbRunner runner, CallRequest request, string what, IPendingResult pending,
        double spentMs, double queueWaitMs, VerbNotes? notes = null)
    {
        _notes = notes ?? new VerbNotes();
        _runner = runner;
        _request = request;
        _what = what;
        _pending = pending;
        _spentMs = spentMs;
        _queueWaitMs = queueWaitMs;
    }

    // A result that goes on with more work (VerbOutcome.Pending: a report described over frames) is stepped on in
    // the same slice while the slice has time, and in later slices after.
    public bool Step(double untilMs)
    {
        long started = Stopwatch.GetTimestamp();
        VerbNotes.Begin();
        try
        {
            while (true)
            {
                if (!_pending.Step(untilMs))
                {
                    return false;
                }

                VerbOutcome outcome = VerbCalls.Handled(() => VerbOutcome.Of(_pending.Result()));
                if (!(outcome is VerbOutcome.Pending next))
                {
                    _outcome = outcome;
                    return true;
                }

                _pending = next.Work;
                if (StopwatchClock.Instance.NowMs >= untilMs)
                {
                    return false;
                }
            }
        }
        catch (Exception exception)
        {
            _pending = new Thrown(ExceptionDispatchInfo.Capture(exception));
            return true;
        }
        finally
        {
            _notes.Take();
            _spentMs += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        }
    }

    [Profiled]
    public ReplyLine Finish()
    {
        long started = Stopwatch.GetTimestamp();
        VerbNotes.Begin();
        VerbOutcome outcome = _outcome ?? VerbCalls.Handled(() => VerbOutcome.Of(_pending.Result()));
        _notes.Take();
        _spentMs += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        return VerbCalls.Write(_runner, _request, _what, outcome, _spentMs, _queueWaitMs, _notes);
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
