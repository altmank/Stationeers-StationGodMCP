#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;
using Assets.Scripts.Objects.Entities;

namespace StationGodMCP.Verbs;

/// <summary>
/// write needs (catalogue write.json): a player's nutrition, hydration and waste set as shares of their capacity and
/// read back (PlayerNeeds). A cheat: without confirm the call reports each value now and sets nothing. Every check
/// runs before anything is set, so a refused call changes nothing.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class WriteNeeds
{
    internal static VerbOutcome Run(WriteNeedsOptions options)
    {
        string label = WriteNeedsOptions.Spec.Label;
        NeedsAsk ask = NeedsAsk.Of(label, options.Nutrition, options.Hydration, options.Waste);
        if (ask is not Asked asked)
        {
            return new VerbOutcome.Refused(((NothingAsked)ask).Error);
        }

        NeedsRequest request = asked.Request;
        string undoMark = WriteNeedsOptions.Spec.Write!.UndoMark;
        return WriteCheck<Human>.Run(() =>
            {
                Human player = PlayerNeeds.Require(options.Player);
                PlayerNeeds.Check(player, request);
                return player;
            })
            .Outcome(options.Report,
                player => PlayerNeeds.Write(player, request),
                player => NeedsReplies.Reported(PlayerNeeds.Reading(player, request), request, undoMark));
    }
}
