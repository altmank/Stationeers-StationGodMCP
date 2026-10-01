#nullable enable

using System.Collections.Generic;
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
    internal JobQueuedView(string jobId, string tool, int position, string? runningJobId, object? preflight,
        JobPreflightSummaryView? preflightSummary = null)
    {
        PreflightSummary = preflightSummary;
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

    /// <summary>The dry run in short, in a brief reply to the run that queued the job.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public JobPreflightSummaryView? PreflightSummary { get; }
}

/// <summary>
/// A confirmed run's dry run in short, for the reply that starts or queues the job: how many pieces it places, changes
/// and removes, and its warning codes, each once. The whole dry run is what the request's own dry run answered;
/// verbose: true repeats it (preflight).
/// </summary>
internal sealed class JobPreflightSummaryView
{
    internal JobPreflightSummaryView(int? placed, int? changed, int? removed, List<string> warnings)
    {
        Placed = placed;
        Changed = changed;
        Removed = removed;
        Warnings = warnings;
    }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Placed { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Changed { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Removed { get; }

    public List<string> Warnings { get; }

    internal static JobPreflightSummaryView Of(RunReportView report)
    {
        List<string> codes = new List<string>(report.Warnings.Count);
        foreach (RunIssueView warning in report.Warnings)
        {
            AddOnce(codes, warning.Code);
        }

        return new JobPreflightSummaryView(report.Placed, report.Changed, report.RemovedCount, codes);
    }

    internal static JobPreflightSummaryView Of(PlaceReportView report) =>
        new JobPreflightSummaryView(report.Placements.Count, null, null, CodesOf(report.Warnings));

    internal static JobPreflightSummaryView Of(RemoveReportView report) =>
        new JobPreflightSummaryView(null, null, report.Removals.Count, CodesOf(report.Warnings));

    private static List<string> CodesOf(List<BuildIssueView> warnings)
    {
        List<string> codes = new List<string>(warnings.Count);
        foreach (BuildIssueView warning in warnings)
        {
            AddOnce(codes, warning.Code);
        }

        return codes;
    }

    private static void AddOnce(List<string> codes, string code)
    {
        if (!codes.Contains(code))
        {
            codes.Add(code);
        }
    }
}

/// <summary>
/// The reply to a confirmed place_*, remove_*, place_structure or remove_structure run: unless verbose, the started or
/// queued job without the preflight it carries (the request's dry run answered it already), with the preflight in
/// short. A busy reply, or any other, is returned as it is.
/// </summary>
internal static class JobStartReplies
{
    internal static object Brief(object started, JobPreflightSummaryView summary) => started switch
    {
        RunJobView run => RunJobView.Brief(run, summary),
        BuildJobView build => BuildJobView.Brief(build, summary),
        JobQueuedView queued => new JobQueuedView(queued.JobId, queued.Tool, queued.Position, queued.RunningJobId,
            null, summary),
        _ => started
    };
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
