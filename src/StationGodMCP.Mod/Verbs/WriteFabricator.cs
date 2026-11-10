#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write fabricator (catalogue write.json): a recipe's item made at once on a fabricator of the printer family
/// (Fabrication), the recipe's time and energy skipped, optionally with ingots loaded into its stock first. A cheat (the
/// cheat switch admits it outside creative): without confirm the call reports what it would load, consume and make, and
/// changes nothing; a refused call loads and makes nothing.
/// </summary>
[ReplyFact(FactRule.Live, "the fabricator's stock and state, read and checked when it acts")]
internal static class WriteFabricator
{
    internal static VerbOutcome Run(WriteFabricatorOptions options)
    {
        string label = WriteFabricatorOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        FabricateOrder? order = FabricateOrder.Of(options.Selector.Prefab, options.Load, options.Make, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        string id = options.Selector.Ids is { Ids.Count: > 0 } ids ? ids.Ids[0] : string.Empty;
        string undoMark = WriteFabricatorOptions.Spec.Write!.UndoMark;
        return WriteCheck<FabricationPlan>.Run(() => Fabrication.Check(order, label, id))
            .Outcome(options.Report,
                plan => new VerbOutcome.Answered(Fabrication.Make(plan)),
                plan => FabricateReplies.Reported(Fabrication.Preview(plan), undoMark));
    }
}
