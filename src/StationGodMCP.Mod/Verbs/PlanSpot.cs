#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// plan spot (catalogue plan.json): free places for one prefab near a point, on a face plane or a room's walls
/// (SpotOrder reads the call, SpotFinder searches), each with build's options for it. Read only.
/// </summary>
internal static class PlanSpot
{
    internal static VerbOutcome Run(PlanSpotOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        SpotOrder? order = SpotOrder.Of(options, problems);
        return order == null
            ? new VerbOutcome.Refused(OptionProblem.Refusal(PlanSpotOptions.Spec.Label, problems))
            : WorkResult<PlanSpotView>.Of(answer => SpotFinder.Finding(order, answer), static view => view);
    }
}
