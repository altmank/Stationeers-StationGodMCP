#nullable enable

using Assets.Scripts.Objects;
using StationGodMCP.Pure.Paging;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// survey network (catalogue survey.json): a cable, pipe or chute network's members (NetworkReader), kept by prefab,
/// open_ends and an area (box, or near with radius), paged, with the network's summary and counts; with summarize its
/// members counted by prefab and colour, with its first limit devices and members with an open end. The network's kind
/// is kind, or read from what network names. box alone lists the pieces in the box with an open end, across every
/// network (kind: only that kind). Either list keeps its members' ids and describes only the page's members; with the
/// wire cursor the rest stays under a cursor (ThingListPage), whose later pages describe each member as it is then and
/// answer the first page's summary and counts. Read only.
/// </summary>
internal static class SurveyNetwork
{
    private const string List = "members";
    private const double MaximumRadiusM = 1000.0;
    private const string Option = "network";

    private static readonly EntryCost Cost = new EntryCost();

    // The box's open ends answer no head.
    private static readonly object NoHead = new object();

    internal static VerbOutcome Run(SurveyNetworkOptions options)
    {
        Selector selector = options.Selector;
        Paging paging = options.Paging;
        WalkRequest walk = WalkRequest.Of(paging);
        if (walk.Continues && options.Summarize != true)
        {
            List<ColorSwatch> colors = ThingColors.Swatches();
            return selector.Network is not { } walked
                ? ThingListPage<NetworkMemberView, object>.Later(walk, paging,
                    id => NetworkReader.OpenEndOf(id, colors), Cost, (page, cursorId, _) => OpenEnds(page, cursorId))
                : ThingListPage<NetworkMemberView, MembersHead>.Later(walk, paging,
                    id => NetworkReader.MemberOf(id, KindOf(options, walked), options.OpenEnds == true, null,
                        colors), Cost, (page, cursorId, head) => Members(page, cursorId, head!));
        }

        List<OptionProblem> problems = new List<OptionProblem>();
        if (selector.Network == null)
        {
            if (selector.Box == null)
            {
                problems.Add(new OptionProblem("network", "or box is needed: the network, or the box to find open ends in"));
            }

            if (selector.Near != null || selector.Radius != null)
            {
                problems.Add(new OptionProblem("near", "goes with network: box alone is the area"));
            }

            if (options.Summarize == true)
            {
                problems.Add(new OptionProblem("summarize", "goes with network"));
            }
        }
        else
        {
            problems.AddRange(selector.AreaProblems(MaximumRadiusM));
            if (options.Summarize == true && paging.Offset > 0)
            {
                problems.Add(new OptionProblem("offset", "goes without summarize: the summary is not paged"));
            }
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(SurveyNetworkOptions.Spec.Label, problems));
        }

        if (selector.Network is not { } network)
        {
            NetworkMemberFilter pieces = NetworkMemberFilter.Of(selector.Prefab, true, PointArea.Anywhere);
            ScanBuild ends = ScanBuild.InScanOrder(NetworkReader.OpenEndsIn(selector.Box!, options.Kind, pieces));
            List<ColorSwatch> swatches = ThingColors.Swatches();
            return ThingListPage<NetworkMemberView, object>.Scan(walk, paging, ends, static () => NoHead,
                id => NetworkReader.OpenEndOf(id, swatches), Cost, (page, cursorId, _) => OpenEnds(page, cursorId));
        }

        UpgradeFamily family = options.Kind is { } kind ? FamilyNamed(kind) : NetworkHandles.FamilyOf(network, Option);
        ThingId id = NetworkHandles.Resolve(network, Option, family);
        NetworkMemberFilter filter = NetworkMemberFilter.Of(selector.Prefab, options.OpenEnds == true,
            selector.Near is { } near && selector.Radius is { } radius
                ? PointArea.Near(NearCentres.Of(near, SurveyNetworkOptions.Spec.Label), radius)
                : selector.AreaAround(null));
        if (options.Summarize == true)
        {
            return WorkResult<NetworkOverviewView>.Of(
                NetworkReader.Overview(family.NetworkKind, id, paging.Limit ?? int.MaxValue, filter), Overview);
        }

        MembersScan scan = NetworkReader.Scan(family.NetworkKind, id, filter);
        List<ColorSwatch> shown = ThingColors.Swatches();
        return ThingListPage<NetworkMemberView, MembersHead>.Scan(walk, paging, ScanBuild.InScanOrder(scan.Scan),
            () => scan.Head!,
            member => NetworkReader.MemberOf(member, family.NetworkKind, filter.OpenEndsOnly,
                scan.OpenEnds.TryGetValue(member, out List<int>? open) ? open : null, shown),
            Cost, (page, cursorId, head) => Members(page, cursorId, head!));
    }

    // The network kind a later page's members are of: the first call's options come again with its cursor.
    private static string KindOf(SurveyNetworkOptions options, NetworkTarget network) =>
        (options.Kind is { } kind ? FamilyNamed(kind) : NetworkHandles.FamilyOf(network, Option)).NetworkKind;

    private static JObject Members(Page<NetworkMemberView> page, string? cursorId, MembersHead head) =>
        ThingListReply.Of(List, page, cursorId, ("network", head.Network), ("summary", head.Summary),
            ("warning", head.Warning), ("structure_count", head.Structures), ("device_count", head.Devices));

    private static JObject OpenEnds(Page<NetworkMemberView> page, string? cursorId) =>
        ThingListReply.Of(List, page, cursorId);

    private static JObject Overview(NetworkOverviewView overview) =>
        ReplyFields.Of(("network", overview.Network), ("summary", overview.Summary), ("warning", overview.Warning),
            ("broken_neighbour_count", overview.BrokenNeighbourCount), ("broken_neighbours", overview.BrokenNeighbours),
            ("structure_count", overview.StructureCount), ("device_count", overview.DeviceCount),
            ("by_prefab", overview.ByPrefab), ("devices", overview.Devices), ("open_ends", overview.OpenEnds),
            ("open_end_count", overview.OpenEndCount));

    private static UpgradeFamily FamilyNamed(string kind) => kind switch
    {
        "cable" => new CableFamily(),
        "chute" => new ChuteFamily(),
        _ => new PipeFamily()
    };
}
