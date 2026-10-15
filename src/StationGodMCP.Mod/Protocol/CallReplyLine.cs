#nullable enable

using System;
using System.Diagnostics;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Protocol;

/// <summary>
/// A verb call's reply as the game thread hands it over (cost design A5): its plain answer or refusal, the notes its
/// handler took, its times. The connection's writer thread writes it once when it reaches it: a reply whose form is
/// chosen as it is written (a report) first takes its form, then the envelope is written through the call's shape
/// and stopped at the size cap as it goes (reply_too_large, with the size counted on without keeping text). The
/// writer's own milliseconds are the call's serialise time.
/// </summary>
internal sealed class CallReplyLine : IOutboundLine
{
    private const int ElapsedDecimals = 2;

    private readonly VerbRunner _runner;
    private readonly CallRequest _request;
    private readonly string _what;
    private readonly VerbOutcome _outcome;
    private readonly VerbNotes _notes;
    private readonly double _handlerMs;
    private readonly double _queueWaitMs;
    private readonly long _frame;
    private readonly bool _budgetHit;

    internal CallReplyLine(VerbRunner runner, CallRequest request, string what, VerbOutcome outcome, VerbNotes notes,
        double handlerMs, double queueWaitMs, long frame, bool budgetHit)
    {
        _runner = runner;
        _request = request;
        _what = what;
        _outcome = outcome;
        _notes = notes;
        _handlerMs = handlerMs;
        _queueWaitMs = queueWaitMs;
        _frame = frame;
        _budgetHit = budgetHit;
    }

    public bool TryTake(out string line)
    {
        long started = Stopwatch.GetTimestamp();
        bool ok;
        try
        {
            line = Written(ApiJson.Serializer, out ok);
        }
        catch (Exception exception)
        {
            // A reply that will not write (a view's own bug): the call is answered internal_error, the connection goes on.
            ProtocolLog.Warning($"A verb reply could not be written: {exception}");
            line = Failed(new ErrorView("internal_error", exception.Message));
            ok = false;
        }

        double writeMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        _runner.Timed?.Invoke(_what, ok, _handlerMs, writeMs, _queueWaitMs);
        return true;
    }

    private string Written(JsonSerializer serializer, out bool ok)
    {
        ok = false;
        if (_outcome is VerbOutcome.Refused refused)
        {
            return Failed(new ErrorView(refused.Error.Code, refused.Error.Message, refused.Error.Data));
        }

        object reply = ((VerbOutcome.Answered)_outcome).Reply;
        if (reply is IReplyForm form)
        {
            try
            {
                reply = form.Form(serializer);
            }
            catch (ApiException tooLarge)
            {
                return Failed(new ErrorView(tooLarge.Code, tooLarge.Message, tooLarge.Details));
            }
        }

        ShapeRequest shape = _request.Shape ?? ShapeRequest.None;
        int limit = Math.Min(shape.MaxBytes ?? _runner.MaxReplyBytes, _runner.MaxReplyBytes);
        CappedText written = ApiJson.WriteReply(serializer,
            CallReplyView.Of(_request.Id, _notes.Attach(reply), _request.Shape != null, ElapsedMs, QueueMs, _frame,
                _budgetHit), shape, _notes.Truncations, limit);
        if (written.Over)
        {
            return Failed(ReplyTooLarge.Of(written.Bytes, limit, written.Outcome));
        }

        ok = true;
        return written.Json!;
    }

    private double ElapsedMs => Math.Round(_handlerMs, ElapsedDecimals);

    private double QueueMs => Math.Round(_queueWaitMs, ElapsedDecimals);

    private string Failed(ErrorView error) =>
        ApiJson.WriteShared(CallReplyView.Failed(_request.Id, error, ElapsedMs, QueueMs, _frame, _budgetHit));
}
