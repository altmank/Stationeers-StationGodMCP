#nullable enable

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
/// open_ends and an area (box, or near with radius), paged by offset, with the network's summary and counts; with
/// summarize its members counted by prefab and colour, with its first limit devices and members with an open end. The
/// network's kind is kind, or read from what network names. box alone lists the pieces in the box with an open end,
/// across every network (kind: only that kind). Read only.
/// </summary>
internal static class SurveyNetwork
{
    private const string List = "members";
    private const double MaximumRadiusM = 1000.0;
    private const string Option = "network";

    internal static VerbOutcome Run(SurveyNetworkOptions options)
    {
        Selector selector = options.Selector;
        Paging paging = options.Paging;
        List<OptionProblem> problems = new List<OptionProblem>();
        if (paging.OffsetOnly() is { } cursor)
        {
            problems.Add(cursor);
        }

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
            JsonSerializer serializer = ApiJson.Fresh();
            return new VerbOutcome.Answered(ListReply.Of(List, NetworkReader.OpenEndsIn(selector.Box!, options.Kind,
                pieces), paging, member => JToken.FromObject(member, serializer)));
        }

        UpgradeFamily family = options.Kind is { } kind ? FamilyNamed(kind) : NetworkHandles.FamilyOf(network, Option);
        ThingId id = NetworkHandles.Resolve(network, Option, family);
        NetworkMemberFilter filter = NetworkMemberFilter.Of(selector.Prefab, options.OpenEnds == true,
            selector.Near is { } near && selector.Radius is { } radius
                ? PointArea.Near(NearCentres.Of(near, SurveyNetworkOptions.Spec.Label), radius)
                : selector.AreaAround(null));
        return new VerbOutcome.Answered(options.Summarize == true
            ? Overview(NetworkReader.Overview(family.NetworkKind, id, paging.Limit ?? int.MaxValue, filter))
            : Members(NetworkReader.Read(family.NetworkKind, id, paging, filter)));
    }

    private static JObject Members(MembersPage members)
    {
        JsonSerializer serializer = ApiJson.Fresh();
        return ListReply.OfPage(List, members.Page, member => JToken.FromObject(member, serializer),
            ReplyFields.Of(("network", members.Network), ("summary", members.Summary), ("warning", members.Warning),
                ("structure_count", members.Structures), ("device_count", members.Devices)));
    }

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
