#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// The parts a swap or remove report (Report.Of) takes from the job's planned view: its body without the keys a job
/// reply has but a report does not (tool, status, job_id), ready for every problem the report lists, and those
/// problems with the index of the id each is about.
/// </summary>
internal static class JobReports
{
    private static readonly string[] JobKeys = { "tool", "status", "job_id" };

    /// <summary>The view's keys but tool, status and job_id; ready true only when no problem is listed.</summary>
    internal static JObject Body(object view, bool ready)
    {
        JObject body = JObject.FromObject(view, ApiJson.Fresh());
        foreach (string key in JobKeys)
        {
            body.Remove(key);
        }

        if (body.ContainsKey("ready"))
        {
            body["ready"] = ready;
        }

        return body;
    }

    /// <summary>
    /// Each planned problem as a report problem, indexed by the place of its id in the ids named (in the order given,
    /// null where an id could not be read).
    /// </summary>
    internal static List<ReportProblem> Problems(IEnumerable<UpgradeProblemView> planned, IReadOnlyList<ThingId?> named)
    {
        List<ReportProblem> problems = new List<ReportProblem>();
        foreach (UpgradeProblemView problem in planned)
        {
            problems.Add(Problem(problem.Code, problem.Message, problem.ReferenceId, named));
        }

        return problems;
    }

    /// <summary>One problem, indexed by the place of its id in the ids named (left out when it names none of them).</summary>
    internal static ReportProblem Problem(string code, string message, ThingId? id, IReadOnlyList<ThingId?> named)
    {
        int? index = null;
        if (id is { } about)
        {
            for (int at = 0; at < named.Count && index == null; at++)
            {
                if (named[at] is { } given && given.Equals(about))
                {
                    index = at;
                }
            }
        }

        return new ReportProblem(index, id?.ToString(), new VerbError(code, message));
    }
}
