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
    internal GhostView(BoardGhost ghost, string? displayName, double? distance, GhostSpotView spot,
        GhostCheckView? check = null)
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
        Orientation = spot.Orientation;
        Ports = spot.Ports;
        Layout = spot.PortChecks != null ? new PortChecksView(spot.PortChecks) : null;
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

    /// <summary>Its turn as facing and up (euler only for a turn off the grid's quarter turns).</summary>
    public OrientationView Orientation { get; }

    /// <summary>A device's cable, pipe and chute ports where the ghost stands, as build structure's report has them.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<SurveyPortView>? Ports { get; }

    /// <summary>What stands in each port's joining cell now (layout.port_checks, as build structure's report).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public PortChecksView? Layout { get; }

    /// <summary>Lattice's validation of the ghost, when find ghost was asked to check.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public GhostCheckView? Check { get; }

    public JObject BuildArguments { get; }
}

/// <summary>Where a ghost's piece would stand: its orientation, and a device's ports and their checks.</summary>
internal sealed class GhostSpotView
{
    internal GhostSpotView(OrientationView orientation, List<SurveyPortView>? ports, List<PortCheckView>? portChecks)
    {
        Orientation = orientation;
        Ports = ports;
        PortChecks = portChecks;
    }

    internal OrientationView Orientation { get; }

    internal List<SurveyPortView>? Ports { get; }

    internal List<PortCheckView>? PortChecks { get; }
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

/// <summary>
/// show ghost's plan in short: ready, the problems and warnings, and each placement's spot (as show preview's brief
/// plan: position, orientation, a device's ports and port checks).
/// </summary>
internal sealed class GhostPlanView
{
    internal GhostPlanView(bool ready, List<BuildIssueView> problems, List<BuildIssueView> warnings,
        List<PreviewPlacementView> placements)
    {
        Ready = ready;
        Problems = problems;
        Warnings = warnings;
        Placements = placements;
    }

    public bool Ready { get; }

    public List<BuildIssueView> Problems { get; }

    public List<BuildIssueView> Warnings { get; }

    public List<PreviewPlacementView> Placements { get; }
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
