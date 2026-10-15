#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// survey feeds (catalogue survey.json): how a network feeds its devices from a root device (id), over the network
/// network names, or the one the root's port joins, or the root's only network of kind (default cable)
/// (FeedPathsSurvey, work over frames); {root, network_id, kind, root_room_id, daisy_chains, multiple_feeds, devices,
/// rooms, unreached}. A list of ids answers one row per root (IdWork). Read only.
/// </summary>
[ReplyFact(FactRule.Followed, "cable networks' members", Keeper = typeof(Assets.Scripts.Networks.CableNetwork))]
[ReplyFact(FactRule.Followed, "pipe and chute networks' members", Keeper = typeof(Networks.StructureNetwork))]
[ReplyFact(FactRule.Live, "each member's shape and room")]
internal static class SurveyFeeds
{
    internal static VerbOutcome Run(SurveyFeedsOptions options) =>
        IdWork.Of<ReplyObject>(options.Selector.Ids!, id =>
        {
            (string referenceId, System.Func<Answer<FeedPathsView>, IEnumerable<object?>> body) =
                FeedPathsSurvey.Start(id, options.Kind ?? "cable", options.Selector.Network,
                    options.Port is { } port ? (int)port : null);
            return (referenceId, reply => Replied(body, reply));
        });

    private static IEnumerable<object?> Replied(System.Func<Answer<FeedPathsView>, IEnumerable<object?>> body,
        Answer<ReplyObject> reply)
    {
        Answer<FeedPathsView> feeds = new Answer<FeedPathsView>();
        foreach (object? step in body(feeds))
        {
            yield return step;
        }

        reply.Value = Reply(feeds.Value);
    }

    private static ReplyObject Reply(FeedPathsView feeds) =>
        ReplyFields.Of(("root", feeds.Root), ("network_id", feeds.NetworkId), ("kind", feeds.Kind),
            ("root_room_id", feeds.RootRoomId), ("daisy_chains", feeds.DaisyChains),
            ("multiple_feeds", feeds.MultipleFeeds), ("devices", feeds.Devices), ("rooms", feeds.Rooms),
            ("unreached", feeds.Unreached));
}
