#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>highlight: what each target drew, how many earlier marks went, for how long, with what.</summary>
internal sealed class HighlightView
{
    internal HighlightView(List<HighlightTargetView> targets, int cleared, double seconds, string renderer,
        List<string> notes)
    {
        Targets = targets;
        Cleared = cleared;
        Seconds = seconds;
        Renderer = renderer;
        Notes = notes;
    }

    public List<HighlightTargetView> Targets { get; }

    /// <summary>Marks of earlier calls removed (all of them unless keep).</summary>
    public int Cleared { get; }

    public double Seconds { get; }

    /// <summary>The see-through material: "t-ray (its shader)" from the T-Ray SPU, else "built-in".</summary>
    public string Renderer { get; }

    public List<string> Notes { get; }
}

/// <summary>One target: its kind, colour and label, what it drew, and where it is from the camera.</summary>
internal sealed class HighlightTargetView
{
    private HighlightTargetView(int index, string kind, HighlightTarget target)
    {
        Index = index;
        Kind = kind;
        Color = target.ColorName;
        Label = target.Label;
        Pulse = target.Pulse;
    }

    public int Index { get; }

    /// <summary>things, network or point.</summary>
    public string Kind { get; }

    public string Color { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Label { get; }

    public bool Pulse { get; }

    /// <summary>The network drawn (its current id; the one named may have been a piece of it).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? NetworkId { get; private set; }

    /// <summary>Things drawn (a network: its pieces); left out for a point.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Things { get; private set; }

    /// <summary>Ids that name nothing (or something being destroyed); left out when none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingId>? Missing { get; private set; }

    /// <summary>A network of more pieces than the cap: only the first were drawn.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Truncated { get; private set; }

    /// <summary>A point's position, as given.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public PositionView? At { get; private set; }

    /// <summary>From the camera to the point (things: the nearest of them); null when nothing was found.</summary>
    public double? DistanceM { get; private set; }

    public double? BearingDeg { get; private set; }

    /// <summary>N, NE, E, SE, S, SW, W or NW.</summary>
    public string? Compass { get; private set; }

    /// <summary>Metres above the camera (negative below).</summary>
    public double? RiseM { get; private set; }

    internal static HighlightTargetView OfPoint(int index, HighlightTarget.Point point, Heading heading) =>
        new HighlightTargetView(index, "point", point)
        {
            At = new PositionView(point.At.X, point.At.Y, point.At.Z)
        }.With(heading);

    internal static HighlightTargetView OfThings(int index, HighlightTarget target, string kind, ThingId? network,
        int drawn, List<ThingId> missing, Heading? nearest, bool truncated)
    {
        HighlightTargetView view = new HighlightTargetView(index, kind, target)
        {
            NetworkId = network,
            Things = drawn,
            Missing = missing.Count > 0 ? missing : null,
            Truncated = truncated ? true : null
        };
        return nearest.HasValue ? view.With(nearest.Value) : view;
    }

    private HighlightTargetView With(Heading heading)
    {
        DistanceM = Math.Round(heading.Distance, 1);
        BearingDeg = Math.Round(heading.Bearing, 0);
        Compass = heading.CompassPoint;
        RiseM = Math.Round(heading.Rise, 1);
        return this;
    }
}
