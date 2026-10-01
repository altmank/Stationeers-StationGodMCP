#nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP;

/// <summary>
/// Hands requests from the pipe and TCP threads to the main thread and the replies back. The listener threads queue a
/// request and wait; StationGodMod.Update drains the queue on the main thread, where the game's objects may be used
/// (ApiHost.Handle), first in first out, within the frame's request budget (FrameBudget: the first request of a
/// frame always runs). A request the main thread does not reach in time answers game_timeout and is dropped unrun.
/// </summary>
internal sealed class StationGodRequestDispatcher
{
    /// <summary>At most this many requests per frame, so a flood cannot stall the game.</summary>
    private const int RequestsPerFrame = 64;

    private const int MillisecondsPerSecond = 1000;

    private readonly ConcurrentQueue<PendingRequest> _requests = new ConcurrentQueue<PendingRequest>();

    /// <summary>The per-frame counters mod_info reports.</summary>
    internal static DispatchStats Stats { get; } = new DispatchStats();

    internal void ProcessPendingRequests(FrameBudget budget)
    {
        long frameStarted = Stopwatch.GetTimestamp();
        int taken = 0;
        int served = 0;
        bool budgetStopped = false;
        while (taken < RequestsPerFrame)
        {
            if (!budget.MayServeAnother(served, MillisecondsSince(frameStarted)))
            {
                budgetStopped = !_requests.IsEmpty;
                break;
            }

            if (!_requests.TryDequeue(out PendingRequest pending))
            {
                break;
            }

            taken++;
            if (pending.IsExpired)
            {
                // Its client already answered game_timeout: not run, and not charged to the budget.
                Stats.Expired();
                continue;
            }

            HandledRequest handled;
            try
            {
                handled = ApiHost.Handle(pending.Json, MillisecondsSince(pending.QueuedAt));
            }
            catch (Exception exception)
            {
                // ApiHost.Handle answers every tool and serializer error itself; this is the error reply failing too.
                handled = new HandledRequest(ApiHost.Serialize(
                    new ErrorReplyView(null, new ErrorView("internal_error", exception.Message), null)), null);
            }

            served++;
            pending.TryComplete(handled);
        }

        Stats.Frame(served, MillisecondsSince(frameStarted), budgetStopped);
    }

    internal string Dispatch(string requestJson, int timeoutMilliseconds)
    {
        PendingRequest pending = new PendingRequest(requestJson, Stopwatch.GetTimestamp());
        _requests.Enqueue(pending);
        if (pending.Wait(timeoutMilliseconds))
        {
            string response = pending.Response!;
            // Counted here, off the main thread, from the text the listener is about to write.
            MethodStats.RecordReply(pending.Method, Encoding.UTF8.GetByteCount(response));
            return response;
        }

        pending.Expire();
        ErrorView error = new ErrorView("game_timeout",
            "The Stationeers main thread did not process the request within " +
            $"{timeoutMilliseconds / MillisecondsPerSecond} seconds.");
        return ApiHost.SerializeOffMainThread(new ErrorReplyView(ReadRequestId(requestJson), error, null));
    }

    private static double MillisecondsSince(long timestamp) =>
        (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;

    private static string? ReadRequestId(string json)
    {
        try
        {
            return JObject.Parse(json).Value<string>("id");
        }
        catch (Exception)
        {
            // JObject.Parse on a line that is not JSON: the reply has no id to echo.
            return null;
        }
    }

    private sealed class PendingRequest
    {
        // Not disposed, on purpose: a ManualResetEventSlim makes a kernel event only when its WaitHandle is asked for,
        // and only Wait(int) and Set are used here. Disposing it after Wait returns would race the tail of Set on the
        // main thread, and after an expiry a late TryComplete; the gain is nothing (performance review 2026-10-01).
        private readonly ManualResetEventSlim _completed = new ManualResetEventSlim(false);
        private int _expired;

        internal PendingRequest(string json, long queuedAt)
        {
            Json = json;
            QueuedAt = queuedAt;
        }

        internal string Json { get; }

        /// <summary>Stopwatch.GetTimestamp when the listener queued it.</summary>
        internal long QueuedAt { get; }

        internal string? Response { get; private set; }

        /// <summary>The known method it named (for the reply size), set with Response.</summary>
        internal string? Method { get; private set; }

        internal bool IsExpired => Volatile.Read(ref _expired) != 0;

        internal bool Wait(int milliseconds) => _completed.Wait(milliseconds);

        internal void Expire()
        {
            Interlocked.Exchange(ref _expired, 1);
        }

        internal void TryComplete(HandledRequest handled)
        {
            if (IsExpired)
            {
                return;
            }

            Response = handled.Json;
            Method = handled.Method;
            _completed.Set();
        }
    }
}
