#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>landing_pads: every trader landing pad centre.</summary>
internal sealed class LandingPadsView
{
    internal LandingPadsView(List<LandingPadView> pads)
    {
        Pads = pads;
        Count = pads.Count;
    }

    public List<LandingPadView> Pads { get; }

    public int Count { get; }
}

internal sealed class LandingPadView
{
    internal LandingPadView(ThingView pad, PositionView position, PadNetworkFacts network, PadMeasure measure,
        List<ShipFitView> fitsByShip, List<ContactFitView> contacts)
    {
        ReferenceId = pad.ReferenceId;
        PrefabName = pad.PrefabName;
        DisplayName = pad.DisplayName;
        Position = position;
        Forward = measure.Forward;
        IsNetworkCenter = network.IsNetworkCenter;
        PieceCount = network.Pieces;
        Extent = network.Extent;
        LargestSquareTiles = measure.LargestSquare;
        RunwayOk = network.RunwayOk;
        FitsByShip = fitsByShip;
        Contacts = contacts;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public PositionView Position { get; }

    /// <summary>+x, -x, +z or -z; +z is north.</summary>
    public string Forward { get; }

    public bool IsNetworkCenter { get; }

    /// <summary>The number of pieces on the pad's landing-pad network.</summary>
    public int PieceCount { get; }

    public ExtentView? Extent { get; }

    public int LargestSquareTiles { get; }

    public bool? RunwayOk { get; }

    public List<ShipFitView> FitsByShip { get; }

    public List<ContactFitView> Contacts { get; }
}

/// <summary>What a pad's landing-pad network says about it.</summary>
internal sealed class PadNetworkFacts
{
    internal PadNetworkFacts(bool isNetworkCenter, int pieces, ExtentView? extent, bool? runwayOk)
    {
        IsNetworkCenter = isNetworkCenter;
        Pieces = pieces;
        Extent = extent;
        RunwayOk = runwayOk;
    }

    internal bool IsNetworkCenter { get; }

    internal int Pieces { get; }

    internal ExtentView? Extent { get; }

    internal bool? RunwayOk { get; }
}

/// <summary>A pad centre's facing and the largest square pad the game accepts there.</summary>
internal sealed class PadMeasure
{
    internal PadMeasure(string forward, int largestSquare)
    {
        Forward = forward;
        LargestSquare = largestSquare;
    }

    internal string Forward { get; }

    internal int LargestSquare { get; }
}

internal sealed class ExtentView
{
    internal ExtentView(int xTiles, int zTiles)
    {
        XTiles = xTiles;
        ZTiles = zTiles;
    }

    public int XTiles { get; }

    public int ZTiles { get; }
}

internal sealed class ShipFitView
{
    internal ShipFitView(string shuttleType, int[] padSize, int runway, bool needsThreshold, bool fits)
    {
        ShuttleType = shuttleType;
        PadSizeTiles = padSize;
        RunwayTiles = runway;
        NeedsThreshold = needsThreshold;
        Fits = fits;
    }

    public string ShuttleType { get; }

    public int[] PadSizeTiles { get; }

    /// <summary>RequiredRunwayLength: the approach path length, in tiles; 0 for ships that land vertically.</summary>
    public int RunwayTiles { get; }

    public bool NeedsThreshold { get; }

    public bool Fits { get; }
}

internal sealed class ContactFitView
{
    internal ContactFitView(ThingId referenceId, string? name, string shuttleType, int[] padSize, PadVerdict verdict)
    {
        ReferenceId = referenceId;
        Name = name;
        ShuttleType = shuttleType;
        PadSizeTiles = padSize;
        Fits = verdict.Fits;
        CanLand = verdict.CanLand;
        Reason = verdict.Reason;
    }

    /// <summary>The contact's id, as trader_contacts gives it.</summary>
    public ThingId ReferenceId { get; }

    public string? Name { get; }

    public string ShuttleType { get; }

    public int[] PadSizeTiles { get; }

    public bool Fits { get; }

    public bool CanLand { get; }

    /// <summary>The game's message; empty when it can land.</summary>
    public string Reason { get; }
}

/// <summary>The game's checks of one contact against one pad.</summary>
internal sealed class PadVerdict
{
    internal PadVerdict(bool fits, bool canLand, string reason)
    {
        Fits = fits;
        CanLand = canLand;
        Reason = reason;
    }

    internal bool Fits { get; }

    internal bool CanLand { get; }

    internal string Reason { get; }
}
