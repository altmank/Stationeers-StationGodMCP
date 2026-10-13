#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find container (catalogue find.json): every outermost holder with an item stored in it (Containers), kept by
/// prefab, part of the shown name and near "player" with radius, nearest first or by id, paged; with the wire cursor a
/// walk (ThingListPage), whose later pages find each container's items again from its slots. Read only.
/// </summary>
internal static class FindContainer
{
    private const string List = "containers";

    private static readonly EntryCost Cost = new EntryCost();

    internal static VerbOutcome Run(FindContainerOptions options)
    {
        Selector selector = options.Selector;
        Paging paging = options.Paging;
        WalkRequest walk = WalkRequest.Of(paging);
        if (walk.Continues)
        {
            PlayerOrigin now = PlayerOrigin.Current();
            return ThingListPage<ContainerView>.Later(walk, paging, id => Containers.Live(id, now)?.ToView(), Cost,
                (page, cursorId) => Reply(page, now, cursorId));
        }

        List<OptionProblem> problems = selector.PlayerRadiusProblems();
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(FindContainerOptions.Spec.Label, problems));
        }

        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(selector.AroundPlayerM.HasValue);
        List<ContainerTally> containers = Containers.Collect(selector.Prefab, selector.Name, selector.AroundPlayerM,
            origin);
        containers.Sort((a, b) => ListKey.Compare(paging.Order, a.Key, b.Key));
        Dictionary<long, ContainerTally> byId = new Dictionary<long, ContainerTally>(containers.Count);
        long[] ids = new long[containers.Count];
        for (int index = 0; index < containers.Count; index++)
        {
            ids[index] = containers[index].Root.ReferenceId;
            byId[ids[index]] = containers[index];
        }

        return ThingListPage<ContainerView>.First(walk, paging, ids, ids.Length,
            id => byId.TryGetValue(id, out ContainerTally? container) ? container.ToView() : null, Cost,
            (page, cursorId) => Reply(page, origin, cursorId));
    }

    private static Newtonsoft.Json.Linq.JObject Reply(Page<ContainerView> page, PlayerOrigin origin, string? cursorId) =>
        ThingListPage<ContainerView>.Reply(List, page, cursorId, ("local_player", origin.View));
}
