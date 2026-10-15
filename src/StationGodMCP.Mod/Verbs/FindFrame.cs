#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find frame (catalogue find.json): every frame with a face on the world's air (OuterFrames), with include inner every
/// frame, within near "player" with radius when given, nearest first or by id, paged, with the census's counts
/// (total_frames, total_outer); with the wire cursor a walk (ThingListPage), whose later pages measure each frame's
/// faces again and answer the first page's census counts. Read only.
/// </summary>
internal static class FindFrame
{
    private const string List = "frames";
    private const string Inner = "inner";

    private static readonly EntryCost Cost = new EntryCost();

    internal static VerbOutcome Run(FindFrameOptions options)
    {
        Selector selector = options.Selector;
        Paging paging = options.Paging;
        bool includeInner = options.Fields.Includes(Inner);
        WalkRequest walk = WalkRequest.Of(paging);
        if (walk.Continues)
        {
            CellAir now = OuterFrames.Air();
            PlayerOrigin standing = PlayerOrigin.Current();
            return ThingListPage<FrameView, FrameCounts>.Later(walk, paging,
                id => OuterFrames.Live(id, now, standing)?.ToView(), Cost,
                (page, cursorId, counts) => Reply(page, cursorId, counts ?? FrameCounts.None, includeInner, standing));
        }

        List<OptionProblem> problems = selector.PlayerRadiusProblems();
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(FindFrameOptions.Spec.Label, problems));
        }

        CellAir air = OuterFrames.Air();
        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(selector.AroundPlayerM.HasValue);
        FrameCensus census = new FrameCensus(air, origin, selector.AroundPlayerM, includeInner);
        return ThingListPage<FrameView, FrameCounts>.Scan(walk, paging, new ScanBuild(census.Scan, paging.Order),
            () => new FrameCounts(census.TotalFrames, census.TotalOuter),
            id => census.Rows.TryGetValue(id, out FrameRow? row) ? row.ToView() : null, Cost,
            (page, cursorId, counts) => Reply(page, cursorId, counts!, includeInner, origin));
    }

    private static ReplyObject Reply(Page<FrameView> page, string? cursorId, FrameCounts counts, bool includeInner,
        PlayerOrigin origin) =>
        ThingListReply.Of(List, page, cursorId, ("total_frames", counts.TotalFrames),
            ("total_outer", counts.TotalOuter), ("include_inner", includeInner), ("local_player", origin.View));

    /// <summary>The census's counts a walk's every page answers.</summary>
    internal sealed class FrameCounts
    {
        internal FrameCounts(int totalFrames, int totalOuter)
        {
            TotalFrames = totalFrames;
            TotalOuter = totalOuter;
        }

        public int TotalFrames { get; }

        public int TotalOuter { get; }

        internal static FrameCounts None { get; } = new FrameCounts(0, 0);
    }
}
