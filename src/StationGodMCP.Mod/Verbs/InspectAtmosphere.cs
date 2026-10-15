#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect atmosphere (catalogue inspect.json): the gas or liquid a thing holds, its pipe or landing pad network's, a
/// mounted device's pipe, its connected pipe networks and the atmospheres in its slots (HeldAtmospheres); a network id
/// or an atmosphere id reads that one. One id, or a list of ids (one row each). Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectAtmosphere
{
    internal static VerbOutcome Run(InspectAtmosphereOptions options) =>
        IdAnswers.Things(options.Selector.Ids!, InspectAtmosphereOptions.Spec.Label, HeldAtmospheres.Read);
}
