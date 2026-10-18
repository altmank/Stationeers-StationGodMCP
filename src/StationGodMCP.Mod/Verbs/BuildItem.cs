#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// build item (catalogue build.json): items made out of nothing (ItemSpawns), by prefab and quantity, into a holder's
/// slots or loose at a point. A cheat, creative worlds only: without confirm the call reports each stack it would make
/// and makes nothing.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class BuildItem
{
    internal static VerbOutcome Run(BuildItemOptions options)
    {
        string label = BuildItemOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        ItemSpawnOrder? order = ItemSpawnOrder.Of(options.Selector.Prefab, options.Quantity, options.To, options.Slot,
            options.At, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        string undoMark = BuildItemOptions.Spec.Write!.UndoMark;
        return WriteCheck<ItemSpawnPlan>.Run(() => ItemSpawns.Check(order, label))
            .Outcome(options.Report,
                plan => new VerbOutcome.Answered(ItemSpawns.Make(plan)),
                plan => ItemSpawnReplies.Reported(ItemSpawns.Preview(plan), undoMark));
    }
}
