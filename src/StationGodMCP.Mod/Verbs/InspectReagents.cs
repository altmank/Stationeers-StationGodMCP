#nullable enable

using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect reagents (catalogue inspect.json): the reagents a thing holds, each with its unit, and their total
/// (ThingReagents). One id, or a list of ids (one row each). Read only.
/// </summary>
internal static class InspectReagents
{
    internal static VerbOutcome Run(InspectReagentsOptions options) =>
        IdAnswers.Things(options.Selector.Ids!, InspectReagentsOptions.Spec.Label,
            id => ThingReagents.Read(GameLookup.RequireThing(id)));
}
