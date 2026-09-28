#nullable enable

using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// A real run that found the job slot taken: status busy, the running job (poll it with job_id), how many wait behind
/// it, and nothing changed. With wait: true the run is queued instead (JobQueuedView).
/// </summary>
internal sealed class JobBusyView
{
    internal JobBusyView(string tool, string runningJobId, int waiting, string message)
    {
        Tool = tool;
        RunningJobId = runningJobId;
        Waiting = waiting;
        Message = message;
    }

    public string Tool { get; }

    public string Status => "busy";

    public string RunningJobId { get; }

    /// <summary>Jobs already queued behind the running one.</summary>
    public int Waiting { get; }

    public string Message { get; }
}

/// <summary>
/// A real run queued behind the running job (wait: true): its own job id, where it stands in the line (1: next), and
/// the job running now. Polling its job_id answers this until it starts; then the job's own states follow. When it
/// starts, its whole preflight runs again on the world as the earlier jobs left it, so a plan made stale by them is
/// refused (final_check_failed) and nothing is changed.
/// </summary>
internal sealed class JobQueuedView
{
    internal JobQueuedView(string jobId, string tool, int position, string? runningJobId, object? preflight)
    {
        JobId = jobId;
        Tool = tool;
        Position = position;
        RunningJobId = runningJobId;
        Preflight = preflight;
    }

    public string JobId { get; }

    public string Tool { get; }

    public string Status => "queued";

    public int Position { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? RunningJobId { get; }

    /// <summary>The dry run the request passed when it was queued.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public object? Preflight { get; }
}

/// <summary>A queued run that never started (the world stopped first): status refused, nothing changed.</summary>
internal sealed class JobDroppedView
{
    internal JobDroppedView(string jobId, string tool, ErrorView error)
    {
        JobId = jobId;
        Tool = tool;
        Error = error;
    }

    public string JobId { get; }

    public string Tool { get; }

    public string Status => "refused";

    public ErrorView Error { get; }
}
