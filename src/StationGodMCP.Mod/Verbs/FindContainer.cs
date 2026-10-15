#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find container (catalogue find.json): every outermost holder with an item stored in it (Containers), kept by
/// prefab, part of the shown name and near "player" with radius, nearest first or by id, paged; with the wire cursor a
/// walk (ThingListPage), whose later pages find each container's items again from its slots. Read only.
/// </summary>
[ReplyFact(FactRule.Followed, "which things exist, by class and prefab", Keeper = typeof(StationGodMCP.WorldModel.ThingIndex))]
[ReplyFact(FactRule.Live, "each container's slots and contents")]
internal static class FindContainer
{
    private const string List = "containers";


    // find container answers no scan counts.
    private static readonly object NoCounts = new object();

    internal static VerbOutcome Run(FindContainerOptions options)
    {
        Selector selector = options.Selector;
        Paging paging = options.Paging;
        WalkRequest walk = WalkRequest.Of(paging);
        if (walk.Continues)
        {
            PlayerOrigin now = PlayerOrigin.Current();
            return ThingListPage<ContainerView, object>.Later(walk, paging, id => Containers.Live(id, now)?.ToView(),
                (page, cursorId, _) => Reply(page, now, cursorId));
        }

        List<OptionProblem> problems = selector.PlayerRadiusProblems();
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(FindContainerOptions.Spec.Label, problems));
        }

        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(selector.AroundPlayerM.HasValue);
        Dictionary<long, ContainerTally> found = new Dictionary<long, ContainerTally>();
        ScanBuild build = new ScanBuild(
            keys => Containers.Scan(keys, selector.Prefab, selector.Name, selector.AroundPlayerM, origin, found),
            paging.Order);
        return ThingListPage<ContainerView, object>.Scan(walk, paging, build, static () => NoCounts,
            id => found.TryGetValue(id, out ContainerTally? container) ? container.ToView() : null,
            (page, cursorId, _) => Reply(page, origin, cursorId));
    }

    private static ReplyObject Reply(Page<ContainerView> page, PlayerOrigin origin, string? cursorId) =>
        ThingListReply.Of(List, page, cursorId, ("local_player", origin.View));
}
