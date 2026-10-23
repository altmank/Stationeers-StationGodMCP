#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Views;

/// <summary>One ghost of the board, as find ghost lists it, with the build structure options that build it.</summary>
internal sealed class GhostView
{
    internal GhostView(BoardGhost ghost, string? displayName, double? distance)
    {
        Id = ghost.Id;
        PrefabName = ghost.PrefabName;
        DisplayName = displayName;
        Position = new PositionView(ghost.Position.X, ghost.Position.Y, ghost.Position.Z);
        RotationDeg = new PositionView(ghost.Euler.X, ghost.Euler.Y, ghost.Euler.Z);
        BuildState = ghost.BuildState;
        Owner = ghost.Owner;
        Source = ghost.Source == GhostSource.Player ? "player" : "agent";
        Label = ghost.Label;
        Tint = new List<double>
        {
            Math.Round(ghost.Tint.Red, 2), Math.Round(ghost.Tint.Green, 2), Math.Round(ghost.Tint.Blue, 2),
            Math.Round(ghost.Tint.Alpha, 2)
        };
        PlacedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)(ghost.PlacedAt * 1000.0)).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        DistanceM = distance.HasValue ? Math.Round(distance.Value, 1) : (double?)null;
        BuildArguments = GhostFilter.BuildArguments(ghost);
    }

    public long Id { get; }

    public string PrefabName { get; }

    public string? DisplayName { get; }

    public PositionView Position { get; }

    public PositionView RotationDeg { get; }

    public int BuildState { get; }

    public string Owner { get; }

    public string Source { get; }

    public string? Label { get; }

    public List<double> Tint { get; }

    public string PlacedAt { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? DistanceM { get; }

    public JObject BuildArguments { get; }
}

/// <summary>One ghost show ghost put on the board.</summary>
internal sealed class PlacedGhostView
{
    internal PlacedGhostView(BoardGhost ghost)
    {
        Id = ghost.Id;
        PrefabName = ghost.PrefabName;
        Position = new PositionView(ghost.Position.X, ghost.Position.Y, ghost.Position.Z);
    }

    public long Id { get; }

    public string PrefabName { get; }

    public PositionView Position { get; }
}

/// <summary>show ghost's plan in short: ready, and the problems and warnings of the placements.</summary>
internal sealed class GhostPlanView
{
    internal GhostPlanView(bool ready, List<BuildIssueView> problems, List<BuildIssueView> warnings)
    {
        Ready = ready;
        Problems = problems;
        Warnings = warnings;
    }

    public bool Ready { get; }

    public List<BuildIssueView> Problems { get; }

    public List<BuildIssueView> Warnings { get; }
}

/// <summary>show ghost: the ghosts it placed, the ids it took off, the board's size after, the plan, notes.</summary>
internal sealed class ShowGhostView
{
    internal ShowGhostView(List<PlacedGhostView> placed, List<long> removed, int total, GhostPlanView? plan,
        List<string> notes)
    {
        Placed = placed;
        Removed = removed;
        Total = total;
        Plan = plan;
        Notes = notes;
    }

    public List<PlacedGhostView> Placed { get; }

    public List<long> Removed { get; }

    public int Total { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public GhostPlanView? Plan { get; }

    public List<string> Notes { get; }
}
