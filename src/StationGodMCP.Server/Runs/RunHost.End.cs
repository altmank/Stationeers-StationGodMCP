namespace StationGodMCP.Server;

/// <summary>
/// The session-end order (hub.md, Session end): on stdin EOF every run is stopped; for the cleanup window only the
/// writes the stop rule lets act (cleanup writes, hold exits) are forwarded and every other call is refused; then
/// session_end goes and nothing more is forwarded. The mod cancels what still waits and applies the held restores; a
/// cleanup write that never went is journalled not_sent. The client kills the hub about 0.4 s after EOF, so the order
/// is timed to finish well inside that.
/// </summary>
public sealed partial class RunHost
{
    /// <summary>The stop code of a run the hub's end stopped; adoption knows such runs by it.</summary>
    internal const string HubRestartCode = "session_end";

    internal const string HubRestartMessage =
        "ended by hub restart: the MCP client closed this hub (a reconnect or client restart does); the next hub " +
        "keeps its printed output and returned value";

    /// <summary>
    /// Runs the session-end order once; later calls return at once. direct: the MCP client's calls that arrived before
    /// EOF and are still in flight; the window waits for them too, as for the runs' cleanup.
    /// </summary>
    internal async Task EndSessionAsync(Task? direct = null)
    {
        DateTime started = DateTime.UtcNow;
        lock (_gate)
        {
            if (Phase != SessionPhase.Open)
            {
                return;
            }

            Volatile.Write(ref _phase, (int)SessionPhase.Cleanup);
        }

        List<Run> live = LiveRuns();
        List<Task> ended = [];
        foreach (Run run in live)
        {
            run.RequestStop(HubRestartCode, HubRestartMessage, world: false, secondKills: false);
            ended.Add(run.Ended);
        }

        TimeSpan left = _settings.CleanupWindow - (DateTime.UtcNow - started);
        if (direct != null)
        {
            ended.Add(direct);
        }

        if (ended.Count > 0 && left > TimeSpan.Zero)
        {
            await Task.WhenAny(Task.WhenAll(ended), Task.Delay(left)).ConfigureAwait(false);
        }

        lock (_gate)
        {
            Volatile.Write(ref _phase, (int)SessionPhase.Ended);
        }

        await _link.SessionEndAsync().ConfigureAwait(false);
    }
}
