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
        VerbPipeline pipeline = new(link, recorder: recorder);
        HubSession session = new(link, pipeline, new HubSessionSettings { Face = Face.Program, EndsLinkSession = true });
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
    /// The one place the caller's end is handled (hub.md, Session end): the session closes (its calls cancelled, its
    /// streams ended), then the link closes, with bye only when session_end was sent (GameLink's rule: bye would clear
    /// the held restores unwritten). The EOF order with runs (stop them, cleanup writes, then session_end) goes here.
    /// </summary>
    internal static async Task OnInputEndedAsync(HubSession session, GameLink link)
    {
        await session.CloseAsync().ConfigureAwait(false);
        await link.DisposeAsync().ConfigureAwait(false);
    }
}
