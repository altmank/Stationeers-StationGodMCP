using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// StationGodMCP.Server --hub (hub.md, Modes): one HubSession on stdin and stdout for the program that started this
/// hub, with the Program face, over this process's one GameLink. It never adopts journals (SidecarOptions.AdoptsJournals).
/// </summary>
internal static class HubMode
{
    internal static async Task ServeAsync(SidecarOptions options, Stream input, Stream output)
    {
        GameLink link = new(options.Client);
        using CallRecorder? recorder = options.RecordPath is { } path ? new CallRecorder(path) : null;
        // The --hub face serves no runs, but it answers the four script whats (and help) itself: it gets the store.
        VerbPipeline pipeline = new(link, recorder: recorder,
            scripts: ScriptStore.For(options, WorkerPool.ShippedSgRoot));
        HubSession session = new(link, pipeline, Settings(pipeline));
        try
        {
            await session.RunAsync(input, output, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await OnInputEndedAsync(session, link).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The --hub caller's session settings: the program face, pass-through, and the extras that serve `more` over the
    /// pipeline's own hub cursors. Its session_end ends the link's session too: it is the only caller on that link.
    /// </summary>
    internal static HubSessionSettings Settings(VerbPipeline pipeline) =>
        new(Face.Program)
        {
            PassThrough = true,
            EndsLinkSession = true,
            Extras = new ProgramFaceExtras(pipeline)
        };

    /// <summary>
    /// The one place the caller's end is handled (hub.md, Session end): session_end (unless the caller sent it), then the
    /// session closes (its calls cancelled, its streams ended), then the link closes, with bye only when session_end was
    /// sent (GameLink's rule: bye would clear the held restores unwritten).
    /// </summary>
    internal static async Task OnInputEndedAsync(HubSession session, GameLink link)
    {
        // A --hub serves no runs, so nothing waits for cleanup: session_end goes at once, before the session's streams
        // end (stream_end would clear their held restores unwritten).
        if (!session.SessionEndSent)
        {
            await link.SessionEndAsync().ConfigureAwait(false);
        }

        await session.CloseAsync().ConfigureAwait(false);

        await link.DisposeAsync().ConfigureAwait(false);
    }
}
