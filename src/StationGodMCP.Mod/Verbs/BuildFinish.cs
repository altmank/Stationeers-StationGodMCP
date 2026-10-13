#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// build finish (catalogue build.json): a placed structure built on to a later build state, paying each state's
/// materials from the holders as a player's construction does. id is one structure, a list or a ref, in order: each is
/// planned and, confirmed, built at once, and each fails alone. Confirmed, the reply is the shared per-id rows (one id
/// its own step: thing, from_state, to_state, last_state, cost, tools, build_state); without confirm, the report: every
/// planned structure a target [id, name, current state -> target state] with the what's undo mark, its step in steps,
/// the call's whole cost in materials, materials_from_player among the warnings, and every refusal a problem about its
/// id's index.
/// </summary>
internal static class BuildFinish
{
    internal static VerbOutcome Run(BuildFinishOptions options)
    {
        FinishOrder order = FinishOrder.Of(options);
        bool acts = order.Acts;
        List<FinishResult> results = new List<FinishResult>(order.Ids.Ids.Count);
        for (int index = 0; index < order.Ids.Ids.Count; index++)
        {
            results.Add(BuildStep.Plan(order, index, order.Ids.Ids[index], acts));
        }

        return acts
            ? FinishReplies.Acted(order.Ids, results)
            : new VerbOutcome.Answered(FinishReplies.Reported(results, order.Free, order.Mark));
    }
}
