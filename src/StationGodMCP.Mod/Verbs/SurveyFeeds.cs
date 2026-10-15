#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// survey feeds (catalogue survey.json): how a network feeds its devices from a root device (id), over the network
/// network names, or the one the root's port joins, or the root's only network of kind (default cable)
/// (FeedPathsSurvey); {root, network_id, kind, root_room_id, daisy_chains, multiple_feeds, devices, rooms, unreached}.
/// A list of ids answers one row per root (Rows). Read only.
/// </summary>
[ReplyFact(FactRule.Followed, "cable networks' members", Keeper = typeof(Assets.Scripts.Networks.CableNetwork))]
[ReplyFact(FactRule.Followed, "pipe and chute networks' members", Keeper = typeof(Networks.StructureNetwork))]
[ReplyFact(FactRule.Live, "each member's shape and room")]
internal static class SurveyFeeds
{
    internal static VerbOutcome Run(SurveyFeedsOptions options)
    {
        PerIdList ids = options.Selector.Ids!;
        Rows rows = new Rows();
        foreach (string id in ids.Ids)
        {
            rows.Add(id, () => Reply(FeedPathsSurvey.Of(id, options.Kind ?? "cable", options.Selector.Network,
                options.Port is { } port ? (int)port : null)));
        }

        return rows.For(ids);
    }

    private static ReplyObject Reply(FeedPathsView feeds) =>
        ReplyFields.Of(("root", feeds.Root), ("network_id", feeds.NetworkId), ("kind", feeds.Kind),
            ("root_room_id", feeds.RootRoomId), ("daisy_chains", feeds.DaisyChains),
            ("multiple_feeds", feeds.MultipleFeeds), ("devices", feeds.Devices), ("rooms", feeds.Rooms),
            ("unreached", feeds.Unreached));
}
