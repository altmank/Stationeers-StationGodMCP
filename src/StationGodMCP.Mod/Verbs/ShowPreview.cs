#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Show;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// show preview (catalogue show.json): a planned structure layout (build structure's own report of the placements,
/// without tool, status and job_id) and a planned cable, pipe or chute run (planned as build would plan it, over ticks,
/// without the lint), drawn as wire boxes, ghosts of the structures or both, plus the cells and boxes the call gives,
/// on the player's screen only and for the call's seconds. A new call replaces what the last drew unless keep; clear
/// only removes what is drawn; xray draws them through walls, frames and terrain. Read only: nothing in the world
/// changes, and a run is never acted on.
/// </summary>
[ReplyFact(FactRule.Live, "the things and places shown")]
internal static class ShowPreview
{
    internal static VerbOutcome Run(ShowPreviewOptions options)
    {
        string label = ShowPreviewOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        PreviewOrder? order = PreviewOrder.Of(options, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        if (order.Clear)
        {
            return new VerbOutcome.Answered(PreviewScreen.Clear());
        }

        if (!(order.Run is { } run))
        {
            return new VerbOutcome.Answered(PreviewScreen.Show(order, null));
        }

        RunRequest? request = BuildRuns.RequestOf(BuildRuns.KindOf(run.Kind), run, new List<ThingId?>(),
            new List<ReportProblem>(), out ThingId? _, out OptionProblem? problem);
        if (request == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label,
                new[] { new OptionProblem($"{PreviewOrder.ByRun}.{problem!.Path}", problem.Problem, problem.Nearest) }));
        }

        return WorkResult<RunPlan>.Of(answer => RunPlanner.Planning(request, false, answer),
            plan => new VerbOutcome.Answered(PreviewScreen.Show(order, plan)));
    }
}
