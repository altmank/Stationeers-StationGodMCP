namespace StationGodMCP.Server;

/// <summary>
/// The session's end. Every step is bounded, so a hub whose caller left closes at once even with the game down or a
/// caller that stopped reading: calls in flight get a short wait, subscriptions a short wait, streams end only on an
/// open connection (otherwise the resume token's window ends them), and the queued lines get a short wait.
/// </summary>
public sealed partial class HubSession
{
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ends the session: calls still in flight are cancelled (their replies dropped, the caller is gone), each relayed
    /// subscription is unsubscribed, the hub streams the session made that carried a call are ended, and the lines
    /// queued are let out.
    /// </summary>
    public async Task CloseAsync()
    {
        List<Task> running = [];
        lock (_gate)
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            foreach (InFlight call in _inFlight.Values)
            {
                call.Cancellation.Cancel();
                running.Add(call.Finished.Task);
            }
        }

        _link.EventReceived -= OnEvent;
        _link.WorldChanged -= OnWorldChanged;
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(CloseWait)).ConfigureAwait(false);

        // A subscribe bound while the calls wound down is registered by now, so it is ended here too.
        List<Relay> relays;
        List<long> streams = [];
        lock (_gate)
        {
            relays = [.. _relays.Values];
            _relays.Clear();
            _callerSubscriptions.Clear();
            foreach (long stream in _ownStreams)
            {
                if (_carried.Contains(stream))
                {
                    streams.Add(stream);
                }
            }

            _ownStreams.Clear();
        }

        await UnsubscribeAllAsync(relays).ConfigureAwait(false);
        foreach (long stream in streams)
        {
            await _link.EndStreamIfConnectedAsync(stream).ConfigureAwait(false);
        }

        if (_output != null)
        {
            await _output.CompleteAsync(CloseWait).ConfigureAwait(false);
        }
    }

    private async Task UnsubscribeAllAsync(List<Relay> relays)
    {
        if (relays.Count == 0)
        {
            return;
        }

        // Not disposed: an unsubscribe left behind by the wait below may still be watching its token.
        CancellationTokenSource bound = new(CloseWait);
        Task[] unsubscribing = new Task[relays.Count];
        for (int index = 0; index < relays.Count; index++)
        {
            unsubscribing[index] = UnsubscribeQuietlyAsync(relays[index], bound.Token);
        }

        await Task.WhenAny(Task.WhenAll(unsubscribing), Task.Delay(CloseWait)).ConfigureAwait(false);
    }
}
