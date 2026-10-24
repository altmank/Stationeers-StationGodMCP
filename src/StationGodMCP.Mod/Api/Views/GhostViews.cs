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
    internal GhostView(BoardGhost ghost, string? displayName, double? distance, GhostCheckView? check = null)
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
        Check = check;
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

    /// <summary>Lattice's validation of the ghost, when find ghost was asked to check.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public GhostCheckView? Check { get; }

    public JObject BuildArguments { get; }
}

/// <summary>
/// Lattice's check of one ghost: passes, waits (for the ghosts in waits_for), fails or warns (why), or unknown (a
/// finding of a newer Lattice).
/// </summary>
internal sealed class GhostCheckView
{
    internal GhostCheckView(string verdict, string? why, IReadOnlyList<long>? waitsFor)
    {
        Verdict = verdict;
        Why = why;
        WaitsFor = waitsFor != null ? new List<long>(waitsFor) : null;
    }

    public string Verdict { get; }

    public string? Why { get; }

    public List<long>? WaitsFor { get; }
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
    internal ShowGhostView(List<PlacedGhostView> placed, List<long> removed, int total, string board,
        GhostPlanView? plan, List<string> notes)
    {
        Placed = placed;
        Removed = removed;
        Total = total;
        Board = board;
        Plan = plan;
        Notes = notes;
    }

    public List<PlacedGhostView> Placed { get; }

    public List<long> Removed { get; }

    public int Total { get; }

    /// <summary>Who keeps the ghosts: lattice, or stationgod (its own board).</summary>
    public string Board { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public GhostPlanView? Plan { get; }

    public List<string> Notes { get; }
}
