#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect health (catalogue inspect.json): the damage state of any thing (HealthReader). One id answers that thing with
/// its networks and build state; a list of ids (up to 256) one row each, reading networks only when the call's fields
/// keep them; network every piece of one pipe, cable or chute network (kind), worst first, damaged_only, paged by
/// offset (NetworkHealth), with the broken pieces touching it. id or network, one of them. Read only.
/// </summary>
internal static class InspectHealth
{
    private const string Option = "network";

    internal static VerbOutcome Run(InspectHealthOptions options)
    {
        Selector selector = options.Selector;
        PerIdList? ids = selector.Ids;
        NetworkTarget? network = selector.Network;
        if ((ids == null) == (network == null))
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(InspectHealthOptions.Spec.Label,
                new[] { new OptionProblem(ids == null ? "id" : Option, "give id (things) or network, one of them") }));
        }

        ShapeRequest shape = options.Fields.Shape ?? ShapeRequest.None;
        if (network != null)
        {
            if (options.Paging.OffsetOnly() is { } problem)
            {
                return new VerbOutcome.Refused(OptionProblem.Refusal(InspectHealthOptions.Spec.Label, new[] { problem }));
            }

            UpgradeFamily family = FamilyNamed(options.Kind);
            ThingId id = NetworkHandles.Resolve(network, Option, family);
            return new VerbOutcome.Answered(IdAnswers.Payload(NetworkHealth.Read(family, id,
                options.DamagedOnly == true, options.Paging.Offset, options.Paging.Limit,
                shape.Wants("things", "networks")), ApiJson.Fresh()));
        }

        PlayerOrigin origin = PlayerOrigin.Current();
        bool one = !ids!.AnswersRows;
        bool withNetworks = one || shape.Wants("results", "networks");
        return IdAnswers.Things(ids, InspectHealthOptions.Spec.Label,
            id => HealthReader.Read(GameLookup.RequireThing(id), origin, withNetworks, withBuildState: one));
    }

    private static UpgradeFamily FamilyNamed(string? kind) => kind switch
    {
        "cable" => new CableFamily(),
        "chute" => new ChuteFamily(),
        _ => new PipeFamily()
    };
}
