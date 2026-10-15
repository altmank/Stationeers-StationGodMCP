#nullable enable

using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect controllers (catalogue inspect.json; PLAN 2.0, controllers): what can write a device, on its data networks
/// (Controllers): chips by pin, id and batch, logic writers, batch writers and mirrors, one entry per controller with
/// the logic types it writes, a chip's line and whether it is on. One id, or a list of ids (one row each). Read only.
/// </summary>
[ReplyFact(FactRule.Live, "the thing's values as the game holds them now")]
internal static class InspectControllers
{
    internal static VerbOutcome Run(InspectControllersOptions options) =>
        IdAnswers.Things(options.Selector.Ids!, InspectControllersOptions.Spec.Label, Controllers.OfDevice);
}
