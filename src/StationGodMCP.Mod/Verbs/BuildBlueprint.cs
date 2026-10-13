#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// build blueprint (catalogue build.json): paste one of BlueprintMod's blueprints at an anchor and a quarter turn,
/// with no player needed, the call the D.B.P.U. makes. Confirmed, one paste starts and answers at once (job_id
/// "paste", which inspect job then polls); without confirm, a Report.Of report: every piece the file holds as a
/// target, [cell, prefab, ? -> prefab] with the what's undo mark (the newest paste only), the file, the anchor, the
/// turn, the copied angle, how long the paced paste takes and the two things a report cannot show among the body's
/// notes. A check that refuses the paste (no host, no BlueprintMod, no such file, a paste still placing) is the
/// report's one problem.
/// </summary>
internal static class BuildBlueprint
{
    internal static VerbOutcome Run(BuildBlueprintOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        PasteOrder? order = PasteOrder.Of(options, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(BuildBlueprintOptions.Spec.Label, problems));
        }

        return WriteCheck<BlueprintPastes.PastePlan>.Run(() => BlueprintPastes.Check(order))
            .Outcome(order.Report, plan => BlueprintPastes.Start(plan, order),
                plan => BlueprintPastes.Describe(plan, order));
    }
}
