#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;
using Assets.Scripts.Objects.Entities;

namespace StationGodMCP.Verbs;

/// <summary>
/// write health (catalogue write.json): a player (by name or id, in the game or not; the player when left out) healed
/// of every kind of damage, body and organs (PlayerHeals). A cheat: without confirm the call reports each part's damage
/// now and heals nothing.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class WriteHealth
{
    internal static VerbOutcome Run(WriteHealthOptions options)
    {
        string undoMark = WriteHealthOptions.Spec.Write!.UndoMark;
        return WriteCheck<Human>.Run(() => PlayerHeals.Require(options.Player))
            .Outcome(options.Report,
                player => new VerbOutcome.Answered(PlayerHeals.Heal(player)),
                player => HealReplies.Reported(PlayerHeals.Read(player), undoMark));
    }
}
