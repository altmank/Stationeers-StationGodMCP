#nullable enable

using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect job (catalogue inspect.json; SPEC-NOTES 16, Notes 1 and 24): one job's status by its id (JobStatus): a build,
/// removal, run, swap, upgrade or undo job, a gas move, or "paste"; in brief unless verbose. The hub waits on it
/// (max_wait is the hub's) and reads status and job_id. No id, script-N and write-N are the hub's (HubJobs). A list of
/// ids answers one row each. Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the mod's job records")]
internal static class InspectJob
{
    internal static VerbOutcome Run(InspectJobOptions options)
    {
        if (HubJobs.Refusal(InspectJobOptions.Spec, options.Selector.Ids) is { } refused)
        {
            return new VerbOutcome.Refused(refused);
        }

        bool verbose = options.Fields.Verbose;
        return IdAnswers.Of(options.Selector.Ids!, id => (id.Trim(), JobStatus.Of(id, verbose)));
    }
}
