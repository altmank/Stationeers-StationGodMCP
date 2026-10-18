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
