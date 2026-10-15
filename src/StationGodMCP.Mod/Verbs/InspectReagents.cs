#nullable enable

using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect reagents (catalogue inspect.json): the reagents a thing holds, each with its unit, and their total
/// (ThingReagents). One id, or a list of ids (one row each). Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectReagents
{
    internal static VerbOutcome Run(InspectReagentsOptions options) =>
        IdAnswers.Things(options.Selector.Ids!, InspectReagentsOptions.Spec.Label,
            id => IdAnswers.Thing(id, ThingReagents.Read));
}
