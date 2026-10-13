#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

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
