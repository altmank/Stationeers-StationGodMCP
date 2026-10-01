#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>
/// deep_miner_spots: the region and ore profile at a point, the world's profiles, and with ores the nearest spots
/// whose profile gives them, with beacons ready for highlight's point targets.
/// </summary>
internal sealed class DeepMinerSpotsView
{
    internal DeepMinerSpotsView(MinerPointView at, List<MinerProfileView> profiles, MinerSearchView? search,
        List<MinerSpotView>? spots, List<MinerBeaconView>? beacons)
    {
        At = at;
        Profiles = profiles;
        Search = search;
        Spots = spots;
        Beacons = beacons;
    }

    /// <summary>The point asked (the player by default): its regions and the profile a miner there takes.</summary>
    public MinerPointView At { get; }

    /// <summary>Every deep-mining profile of this world, in the order the miner tries them.</summary>
    public List<MinerProfileView> Profiles { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public MinerSearchView? Search { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<MinerSpotView>? Spots { get; }

    /// <summary>The spots as {at, label}, the shape highlight takes for point targets.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<MinerBeaconView>? Beacons { get; }
}

/// <summary>A point's regions (one per region set of the world) and the profile a miner there takes, or null.</summary>
internal sealed class MinerPointView
{
    internal MinerPointView(PositionView position, List<MinerRegionView> regions, string? profileId)
    {
        Position = position;
        Regions = regions;
        ProfileId = profileId;
    }

    public PositionView Position { get; }

    public List<MinerRegionView> Regions { get; }

    /// <summary>Null: no profile matches, so a miner there cannot mine (its error light).</summary>
    public string? ProfileId { get; }
}

internal sealed class MinerRegionView
{
    internal MinerRegionView(string set, string region, string? name)
    {
        Set = set;
        Region = region;
        Name = name;
    }

    /// <summary>The region set (e.g. VulcanDeepMiningRegions, or the named-area set).</summary>
    public string Set { get; }

    public string Region { get; }

    /// <summary>The region's shown name, where it has one.</summary>
    public string? Name { get; }
}

/// <summary>A deep-mining profile: what each dirty ore holds, how many per drop and how long a drop takes.</summary>
internal sealed class MinerProfileView
{
    internal MinerProfileView(string id, List<MinerReagentView> reagents, int[] quantity, int[] timeS,
        List<string> regions)
    {
        Id = id;
        Reagents = reagents;
        Quantity = quantity;
        TimeS = timeS;
        Regions = regions;
    }

    public string Id { get; }

    /// <summary>Grams of each reagent in one dirty ore (only those above zero).</summary>
    public List<MinerReagentView> Reagents { get; }

    /// <summary>[min, max): the game draws min to max - 1 ores per drop.</summary>
    public int[] Quantity { get; }

    /// <summary>[min, max): seconds per drop at 200 RPM, drawn min to max - 1.</summary>
    public int[] TimeS { get; }

    /// <summary>The region ids its conditions ask for.</summary>
    public List<string> Regions { get; }
}

internal sealed class MinerReagentView
{
    internal MinerReagentView(string reagent, double grams)
    {
        Reagent = reagent;
        Grams = grams;
    }

    public string Reagent { get; }

    public double Grams { get; }
}

/// <summary>How the search ran: what it looked for, over what, and how far it got.</summary>
internal sealed class MinerSearchView
{
    internal MinerSearchView(IReadOnlyList<string> ores, List<string> profiles, double radiusM, double stepM,
        double separationM, int samples, int matched, double searchedRadiusM, bool truncated)
    {
        Ores = new List<string>(ores);
        Profiles = profiles;
        RadiusM = radiusM;
        StepM = stepM;
        MinSeparationM = separationM;
        Samples = samples;
        Matched = matched;
        SearchedRadiusM = searchedRadiusM;
        Truncated = truncated;
    }

    public List<string> Ores { get; }

    /// <summary>The profiles holding every ore asked for.</summary>
    public List<string> Profiles { get; }

    public double RadiusM { get; }

    public double StepM { get; }

    public double MinSeparationM { get; }

    public int Samples { get; }

    public int Matched { get; }

    /// <summary>Every sample within this distance was looked at; spots beyond it may exist.</summary>
    public double SearchedRadiusM { get; }

    public bool Truncated { get; }
}

/// <summary>A spot whose profile gives the ores: where, how far and which way from the centre, and the ground.</summary>
internal sealed class MinerSpotView
{
    internal MinerSpotView(PositionView at, double distanceM, double bearingDeg, string compass, string profileId,
        List<MinerRegionView> regions, MinerTerrainView terrain)
    {
        At = at;
        DistanceM = distanceM;
        BearingDeg = bearingDeg;
        Compass = compass;
        ProfileId = profileId;
        Regions = regions;
        Terrain = terrain;
    }

    /// <summary>The 2 m cell centre over the spot, at the ground when it was found.</summary>
    public PositionView At { get; }

    /// <summary>Horizontal distance from the search centre.</summary>
    public double DistanceM { get; }

    /// <summary>0 = +z (north), 90 = +x (east).</summary>
    public double BearingDeg { get; }

    public string Compass { get; }

    public string ProfileId { get; }

    public List<MinerRegionView> Regions { get; }

    public MinerTerrainView Terrain { get; }
}

/// <summary>
/// The ground at a spot, read from the terrain's own density: its height, and the miner's own terrain rule
/// (DeepMiner.CanConstruct: the world volume at its position at most 6000 L) at the cell centre that meets it.
/// </summary>
internal sealed class MinerTerrainView
{
    internal MinerTerrainView(double? groundY, double? cellVolumeL, bool? terrainOk, string note)
    {
        GroundY = groundY;
        CellVolumeL = cellVolumeL;
        TerrainOk = terrainOk;
        Note = note;
    }

    /// <summary>Null when no ground was found in the height range read.</summary>
    public double? GroundY { get; }

    /// <summary>The world volume of the cell at the spot's at (8000 L is open air).</summary>
    public double? CellVolumeL { get; }

    /// <summary>The miner's terrain rule holds at at; null when not checked.</summary>
    public bool? TerrainOk { get; }

    public string Note { get; }
}

/// <summary>A point target for highlight: where and what to call it.</summary>
internal sealed class MinerBeaconView
{
    internal MinerBeaconView(double[] at, string label)
    {
        At = at;
        Label = label;
    }

    public double[] At { get; }

    public string Label { get; }
}
