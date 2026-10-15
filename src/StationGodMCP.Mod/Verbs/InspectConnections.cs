#nullable enable

using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect connections (catalogue inspect.json): every connection end of a thing and what is attached there
/// (EndsReader), with its own network and colour. One id, or a list of ids (one row each). Read only.
/// </summary>
internal static class InspectConnections
{
    internal static VerbOutcome Run(InspectConnectionsOptions options) =>
        IdAnswers.Things(options.Selector.Ids!, InspectConnectionsOptions.Spec.Label,
            id => IdAnswers.Thing(id, EndsReader.Read));
}
