#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>outer_frames: one page of frames, in the order asked, and the counts over all of them.</summary>
internal sealed class OuterFramesView
{
    internal OuterFramesView(Slice<FrameView> page, int totalFrames, int totalOuter, bool includeInner,
        LocalPlayerView? localPlayer)
    {
        Frames = page.Items;
        Count = page.Items.Count;
        TotalFrames = totalFrames;
        TotalOuter = totalOuter;
        Offset = page.Offset;
        Limit = page.Limit;
        Total = page.Total;
        HasMore = page.HasMore;
        IncludeInner = includeInner;
        LocalPlayer = localPlayer;
    }

    public List<FrameView> Frames { get; }

    public int Count { get; }

    /// <summary>Frames considered, after near_player_m.</summary>
    public int TotalFrames { get; }

    public int TotalOuter { get; }

    public int Offset { get; }

    public int Limit { get; }

    /// <summary>Frames listed across all pages: the outer ones, or every frame with include_inner.</summary>
    public int Total { get; }

    public bool HasMore { get; }

    public bool IncludeInner { get; }

    public LocalPlayerView? LocalPlayer { get; }
}

/// <summary>One frame: which faces touch the world's air, whether it is airtight, and its paint.</summary>
internal sealed class FrameView
{
    internal FrameView(ThingView thing, PositionView position, double? distanceM, List<string> exposedFaces,
        bool blocksAir, int buildState, ColorView color)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        Position = position;
        DistanceM = distanceM;
        ExposedFaces = exposedFaces;
        ExposedFaceCount = exposedFaces.Count;
        BlocksAir = blocksAir;
        BuildState = buildState;
        Color = color;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }

    /// <summary>Any of +x, -x, +y, -y, +z, -z (+z is north, +y up).</summary>
    public List<string> ExposedFaces { get; }

    public int ExposedFaceCount { get; }

    public bool BlocksAir { get; }

    public int BuildState { get; }

    public ColorView Color { get; }
}
