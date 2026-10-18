#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>
/// remove terrain's reply, and its report's body: the voxel-aligned box (show preview's boxes take it as it is), how
/// many voxels it holds, how many the dig takes (or took), the ones already empty and those kept at bedrock, what is
/// done with the ore and the ore released, and the players standing in the box (left out when none).
/// </summary>
internal sealed class TerrainDigView
{
    internal TerrainDigView(DigBox box, DigTally tally, OreHandling ores, List<ThingView> playersInBox)
    {
        Box = new DigBoxView(PointView.Of(box.Min), PointView.Of(box.Max));
        Cells = box.Cells;
        Dug = tally.Dug;
        AlreadyEmpty = tally.AlreadyEmpty;
        BedrockKept = tally.BedrockKept;
        OresHandling = ores.Name;
        Ores = new List<OreView>(tally.Ores.Count);
        foreach (OreTally ore in tally.Ores)
        {
            Ores.Add(new OreView(ore.PrefabName, ore.Minables, ore.Quantity));
        }

        PlayersInBox = playersInBox.Count > 0 ? playersInBox : null;
    }

    public DigBoxView Box { get; }

    public long Cells { get; }

    public int Dug { get; }

    public int AlreadyEmpty { get; }

    public int BedrockKept { get; }

    public string OresHandling { get; }

    public List<OreView> Ores { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? PlayersInBox { get; }
}

/// <summary>The two corners of a dig's voxel-aligned box, in metres.</summary>
internal sealed class DigBoxView
{
    internal DigBoxView(PointView min, PointView max)
    {
        Min = min;
        Max = max;
    }

    public PointView Min { get; }

    public PointView Max { get; }
}

/// <summary>One ore a dig releases: its prefab, its minables, and the quantity dropped (null when none is known or made).</summary>
internal sealed class OreView
{
    internal OreView(string prefabName, int minables, int? quantity)
    {
        PrefabName = prefabName;
        Minables = minables;
        Quantity = quantity;
    }

    public string PrefabName { get; }

    public int Minables { get; }

    public int? Quantity { get; }
}

/// <summary>
/// build terrain's reply, and its report's body: the voxel-aligned box, its voxels, the mode (and level), how many the
/// fill raises and digs (or would), those already so, those kept at a structure or at bedrock, the pockets filled
/// (voids mode), the ore its digging releases, and the players standing in the box (left out when none).
/// </summary>
internal sealed class TerrainFillView
{
    internal TerrainFillView(DigBox box, FillMode mode, DigTally tally, int? voids, OreHandling ores,
        List<ThingView> playersInBox, List<ThingView> covers)
    {
        Covers = covers.Count > 0 ? covers : null;
        Box = new DigBoxView(PointView.Of(box.Min), PointView.Of(box.Max));
        Cells = box.Cells;
        Mode = mode.Name;
        Level = mode is FillMode.Level level ? level.Height : null;
        Filled = tally.Filled;
        Dug = tally.Dug;
        Unchanged = tally.Unchanged;
        MaterialRestored = mode is FillMode.Original ? tally.MaterialRestored : null;
        StructureKept = tally.StructureKept;
        BedrockKept = tally.BedrockKept;
        Voids = voids;
        OresHandling = ores.Name;
        Ores = new List<OreView>(tally.Ores.Count);
        foreach (OreTally ore in tally.Ores)
        {
            Ores.Add(new OreView(ore.PrefabName, ore.Minables, ore.Quantity));
        }

        PlayersInBox = playersInBox.Count > 0 ? playersInBox : null;
    }

    public DigBoxView Box { get; }

    public long Cells { get; }

    public string Mode { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? Level { get; }

    public int Filled { get; }

    public int Dug { get; }

    public int Unchanged { get; }

    /// <summary>Voxels whose shape was right but whose material it put back (mode original only).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? MaterialRestored { get; }

    public int StructureKept { get; }

    public int BedrockKept { get; }

    /// <summary>The pockets filled (voids mode only).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Voids { get; }

    public string OresHandling { get; }

    public List<OreView> Ores { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? PlayersInBox { get; }

    /// <summary>
    /// The pipes, cables and chutes lying in open ground (outside frames) that the fill covers with ground; left out
    /// when it covers none. They keep working buried.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? Covers { get; }
}

/// <summary>
/// find void's reply: the box searched, the enclosed pockets of at least min_cells voxels (largest first), and what was
/// not judged a pocket: empty space reaching the box's edge, and space joined to a structure (part of the base).
/// </summary>
internal sealed class TerrainVoidsView
{
    internal TerrainVoidsView(DigBox searched, List<VoidView> voids, int smaller, VoidSearch.Result found)
    {
        Searched = new DigBoxView(PointView.Of(searched.Min), PointView.Of(searched.Max));
        SearchedCells = searched.Cells;
        Voids = voids;
        Count = voids.Count;
        SmallerThanMin = smaller;
        OpenToEdge = found.OpenToEdge;
        OpenToEdgeCells = found.OpenToEdgeCells;
        TouchingStructures = found.TouchingStructures;
    }

    public DigBoxView Searched { get; }

    public long SearchedCells { get; }

    public List<VoidView> Voids { get; }

    public int Count { get; }

    public int SmallerThanMin { get; }

    public int OpenToEdge { get; }

    public int OpenToEdgeCells { get; }

    public int TouchingStructures { get; }
}

/// <summary>
/// One pocket: its voxels, its voxel-aligned bounds, a voxel in it, the box build terrain's voids mode takes for it
/// (one voxel more on every side, [[x, y, z], [x, y, z]]), and any player inside it.
/// </summary>
internal sealed class VoidView
{
    internal VoidView(int index, TerrainVoid found, VoxelPoint inside, List<ThingView> players)
    {
        DigBox bounds = DigBox.Between(found.Low, found.High);
        DigBox fill = found.FillBox;
        Index = index;
        Cells = found.Cells.Count;
        Min = PointView.Of(bounds.Min);
        Max = PointView.Of(bounds.Max);
        Inside = PointView.Of(inside.Centre);
        FillBox = new List<double[]>
        {
            new[] { fill.Min.X, fill.Min.Y, fill.Min.Z },
            new[] { fill.Max.X, fill.Max.Y, fill.Max.Z }
        };
        PlayersInside = players.Count > 0 ? players : null;
    }

    public int Index { get; }

    public int Cells { get; }

    public PointView Min { get; }

    public PointView Max { get; }

    public PointView Inside { get; }

    public List<double[]> FillBox { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? PlayersInside { get; }
}
