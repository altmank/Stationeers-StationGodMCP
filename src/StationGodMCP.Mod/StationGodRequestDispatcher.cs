#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP;

/// <summary>
/// Hands requests from the pipe and TCP threads to the main thread and the replies back. The listener threads queue a
/// request and wait; StationGodMod.Update drains the queue on the main thread, where the game's objects may be used
/// (ApiHost.Handle). A request the main thread does not reach in time answers game_timeout and is dropped unrun.
/// </summary>
internal sealed class StationGodRequestDispatcher
{
    /// <summary>At most this many requests per frame, so a flood cannot stall the game.</summary>
    private const int RequestsPerFrame = 64;

    private const int MillisecondsPerSecond = 1000;

    private readonly ConcurrentQueue<PendingRequest> _requests = new ConcurrentQueue<PendingRequest>();

    internal void ProcessPendingRequests()
    {
        int processed = 0;
        while (processed < RequestsPerFrame && _requests.TryDequeue(out PendingRequest pending))
        {
            processed++;
            if (pending.IsExpired)
            {
                continue;
            }

            string response;
            try
            {
                response = ApiHost.Handle(pending.Json);
            }
            catch (Exception exception)
            {
                // ApiHost.Handle answers every tool error itself; this is the serializer failing on a reply.
                response = ApiHost.Serialize(
                    new ErrorReplyView(null, new ErrorView("internal_error", exception.Message), null));
            }

            pending.TryComplete(response);
        }
    }

    internal string Dispatch(string requestJson, int timeoutMilliseconds)
    {
        PendingRequest pending = new PendingRequest(requestJson);
        _requests.Enqueue(pending);
        if (pending.Wait(timeoutMilliseconds))
        {
            return pending.Response!;
        }

        pending.Expire();
        ErrorView error = new ErrorView("game_timeout",
            "The Stationeers main thread did not process the request within " +
            $"{timeoutMilliseconds / MillisecondsPerSecond} seconds.");
        return ApiHost.Serialize(new ErrorReplyView(ReadRequestId(requestJson), error, null));
    }

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
        private readonly ManualResetEventSlim _completed = new ManualResetEventSlim(false);
        private int _expired;

        internal PendingRequest(string json)
        {
            Json = json;
        }

        internal string Json { get; }

        internal string? Response { get; private set; }

        internal bool IsExpired => Volatile.Read(ref _expired) != 0;

        internal bool Wait(int milliseconds) => _completed.Wait(milliseconds);

        internal void Expire()
        {
            Interlocked.Exchange(ref _expired, 1);
        }

        internal void TryComplete(string response)
        {
            if (IsExpired)
            {
                return;
            }

            Response = response;
            _completed.Set();
        }
    }
}
