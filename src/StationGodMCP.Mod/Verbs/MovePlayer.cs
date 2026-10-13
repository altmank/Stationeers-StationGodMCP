#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// move player (catalogue move.json): a player (by name or id; the player when left out) put at a point, beside a
/// thing or beside another player, dropped onto safe ground when asked (PlayerMovePlan). A cheat: without confirm the
/// call reports where the player would go and moves no one.
/// </summary>
internal static class MovePlayer
{
    internal static VerbOutcome Run(MovePlayerOptions options)
    {
        string label = MovePlayerOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        PlayerMoveOrder? order = PlayerMoveOrder.Of(options.Player, options.At, options.To, options.ToPlayer,
            options.SafeGround == true, problems);
        if (order == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        string undoMark = MovePlayerOptions.Spec.Write!.UndoMark;
        return WriteCheck<PlayerMovePlan>.Run(() => PlayerMovePlan.Check(order, label))
            .Outcome(options.Report,
                plan => new VerbOutcome.Answered(PlayerMoveReplies.Acted(plan.Move())),
                plan => PlayerMoveReplies.Reported(plan.Planned(), undoMark));
    }
}
