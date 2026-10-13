#nullable enable

using System;
using System.Diagnostics;
using System.Text;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Protocol;

/// <summary>
/// A verb call run on the main thread (protocol.md, Verb calls): the dispatcher binds and runs its (verb, what) handler,
/// and the reply message is the handler's reply shaped by the call's shape, or its refusal. A reply larger than
/// max_reply_bytes (or the shape's max_bytes) is answered reply_too_large; a handler's failure is internal_error.
/// </summary>
internal static class VerbCalls
{
    private const int ElapsedDecimals = 2;

    internal static bool Handles(string? method) => VerbSurface.Verb(method) != null;

    /// <summary>The verb call at this frame: its reply message.</summary>
    internal static string Run(VerbDispatcher verbs, ProtocolCall call, double queueWaitMs, long frame, int maxReplyBytes)
    {
        Stopwatch watch = Stopwatch.StartNew();
        CallRequest request = call.Request;
        VerbOutcome outcome;
        try
        {
            outcome = verbs.Run(request.Method, request.Params, request.Shape, request.ArgumentNamesChecked);
        }
        catch (ApiException refused)
        {
            outcome = new VerbOutcome.Refused(new VerbError(refused.Code, refused.Message,
                refused.Details == null ? null : Newtonsoft.Json.Linq.JToken.FromObject(refused.Details)));
        }
        catch (Exception exception)
        {
            // The request boundary: a handler's bug answers this call, never the frame.
            outcome = new VerbOutcome.Refused(new VerbError("internal_error", exception.Message));
        }

        double elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, ElapsedDecimals);
        double queueMs = Math.Round(queueWaitMs, ElapsedDecimals);
        bool budgetHit = CurrentFrame.BudgetReached(StopwatchClock.Instance.NowMs);
        switch (outcome)
        {
            case VerbOutcome.Answered answered:
                ShapeRequest shape = request.Shape ?? ShapeRequest.None;
                ShapedText shaped = ApiJson.WriteReply(
                    CallReplyView.Of(request.Id, answered.Reply, request.Shape != null, elapsedMs, queueMs, frame,
                        budgetHit), shape, Array.Empty<Truncation>());
                int bytes = Encoding.UTF8.GetByteCount(shaped.Json);
                int limit = Math.Min(shape.MaxBytes ?? maxReplyBytes, maxReplyBytes);
                return bytes <= limit
                    ? shaped.Json
                    : ApiJson.WriteShared(CallReplyView.Failed(request.Id, ReplyTooLarge.Of(bytes, limit, shaped.Outcome),
                        elapsedMs, queueMs, frame, budgetHit));
            case VerbOutcome.Refused refused:
                return ApiJson.WriteShared(CallReplyView.Failed(request.Id,
                    new ErrorView(refused.Error.Code, refused.Error.Message, refused.Error.Data), elapsedMs, queueMs, frame,
                    budgetHit));
            default:
                throw new InvalidOperationException("A verb outcome is answered or refused.");
        }
    }
}
