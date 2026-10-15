#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// inspect health (catalogue inspect.json): the damage state of any thing (HealthReader). One id answers that thing with
/// its networks and build state; a list of ids (up to 256) one row each, reading networks only when the call's fields
/// keep them; network every piece of one pipe, cable or chute network (kind), worst first, damaged_only, paged
/// (NetworkHealth), with the broken pieces touching it; with the wire cursor a walk (ThingListPage) whose later pages
/// read each piece again and answer the network's facts as the first page found them. id or network, one of them.
/// Read only.
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
            return Network(options, network, shape.Wants("things", "networks"));
        }

        PlayerOrigin origin = PlayerOrigin.Current();
        bool one = !ids!.AnswersRows;
        bool withNetworks = one || shape.Wants("results", "networks");
        return IdAnswers.Things(ids, InspectHealthOptions.Spec.Label,
            id => IdAnswers.Thing(id, thing => HealthReader.Read(thing, origin, withNetworks, withBuildState: one)));
    }

    private static VerbOutcome Network(InspectHealthOptions options, NetworkTarget network, bool withNetworks)
    {
        Paging paging = options.Paging;
        WalkRequest walk = WalkRequest.Of(paging);
        PlayerOrigin origin = PlayerOrigin.Current();
        if (walk.Continues)
        {
            return ThingListPage<HealthView, NetworkHealthFacts>.Later(walk, paging,
                id => Live(id, origin, withNetworks), (page, cursorId, facts) => Reply(page, cursorId, facts!));
        }

        UpgradeFamily family = FamilyNamed(options.Kind);
        NetworkHealth scan = NetworkHealth.Scan(family, NetworkHandles.Resolve(network, Option, family),
            options.DamagedOnly == true);
        long[] ids = new long[scan.Ranked.Count];
        for (int index = 0; index < ids.Length; index++)
        {
            ids[index] = scan.Ranked[index].ReferenceId;
        }

        return ThingListPage<HealthView, NetworkHealthFacts>.First(walk, paging, ids, ids.Length, scan.Facts,
            id => Live(id, origin, withNetworks), (page, cursorId, facts) => Reply(page, cursorId, facts!));
    }

    private static HealthView? Live(long id, PlayerOrigin origin, bool withNetworks) =>
        Assets.Scripts.Objects.Thing.Find(id) is { IsBeingDestroyed: false } thing
            ? HealthReader.Read(thing, origin, withNetworks)
            : null;

    // HealthNetwork's keys: the page of things, the network's facts, then cursor_id while the walk has more.
    private static ReplyObject Reply(Page<HealthView> page, string? cursorId, NetworkHealthFacts facts) =>
        ThingListReply.Of("things", page, cursorId, ("network_id", facts.NetworkId), ("kind", facts.Kind),
            ("pieces", facts.Pieces), ("damaged_only", facts.DamagedOnly), ("warning", facts.Broken.Warning),
            ("broken_neighbour_count", facts.Broken.Count), ("broken_neighbours", facts.Broken.OrNull));

    private static UpgradeFamily FamilyNamed(string? kind) => kind switch
    {
        "cable" => new CableFamily(),
        "chute" => new ChuteFamily(),
        _ => new PipeFamily()
    };
}
