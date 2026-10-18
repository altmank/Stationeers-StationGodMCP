#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Pure;

/// <summary>
/// One 1 m terrain voxel, by the whole-metre world point at its low corner: the game keys a voxel by that point
/// (VoxelTerrain.GetDensityWorldSpace(Vector3Int); Explosion.Explode digs vector3Int + offset, Explosion.cs:44-56) and
/// draws its surface half a metre on (VoxelConstants.TerrainMeshOffset 0.499, VoxelConstants.cs:23).
/// </summary>
internal readonly struct VoxelPoint : IEquatable<VoxelPoint>
{
    internal VoxelPoint(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    internal int X { get; }

    internal int Y { get; }

    internal int Z { get; }

    internal Vec3 Centre => new Vec3(X + 0.5, Y + 0.5, Z + 0.5);

    public bool Equals(VoxelPoint other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is VoxelPoint other && Equals(other);

    public override int GetHashCode() => (X * 397 ^ Y) * 397 ^ Z;

    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>
/// The voxels remove terrain digs: every voxel whose centre lies in the box the call gives, corners in either order. Its
/// Min and Max corners are the voxel-aligned box those voxels fill (what show preview draws). At most MostCells voxels a
/// call, the same 32 m cube swap chute takes, so a dig ends well inside its call's deadline. The game never digs at or
/// under bedrock: SetDensityWorldSpace does nothing where LodManager.IsAtOrUnderBedrock (position.y &lt; 2,
/// VoxelTerrain.cs:953, LodManager.cs:519-522), so a voxel below BedrockY is kept.
/// </summary>
internal sealed class DigBox
{
    internal const long MostCells = 32768;

    /// <summary>The most voxels find void reads in one call: a 64 m cube, read over frames, nothing changed.</summary>
    internal const long MostSearched = 262144;

    internal const int BedrockY = 2;

    private DigBox(VoxelPoint low, VoxelPoint high)
    {
        Low = low;
        High = high;
    }

    internal VoxelPoint Low { get; }

    internal VoxelPoint High { get; }

    internal long Cells => Span(Low.X, High.X) * Span(Low.Y, High.Y) * Span(Low.Z, High.Z);

    internal int SizeX => High.X - Low.X + 1;

    internal int SizeY => High.Y - Low.Y + 1;

    internal int SizeZ => High.Z - Low.Z + 1;

    internal Vec3 Min => new Vec3(Low.X, Low.Y, Low.Z);

    internal Vec3 Max => new Vec3(High.X + 1, High.Y + 1, High.Z + 1);

    /// <summary>The box's voxels, or null with a problem on box: none in it, or more than MostCells.</summary>
    internal static DigBox? Of(BoxCorners? corners, List<OptionProblem> problems) =>
        Of(corners, problems, MostCells, "a call digs or fills");

    /// <summary>The box's voxels, at most `most` of them (doing: what the call does with them, for the problem).</summary>
    internal static DigBox? Of(BoxCorners? corners, List<OptionProblem> problems, long most, string doing)
    {
        if (corners == null)
        {
            problems.Add(new OptionProblem("box", "is required: two corners [[x, y, z], [x, y, z]] in metres"));
            return null;
        }

        Vec3 a = corners.First;
        Vec3 b = corners.Second;
        VoxelPoint low = new VoxelPoint(FirstIn(Math.Min(a.X, b.X)), FirstIn(Math.Min(a.Y, b.Y)),
            FirstIn(Math.Min(a.Z, b.Z)));
        VoxelPoint high = new VoxelPoint(LastIn(Math.Max(a.X, b.X)), LastIn(Math.Max(a.Y, b.Y)),
            LastIn(Math.Max(a.Z, b.Z)));
        if (high.X < low.X || high.Y < low.Y || high.Z < low.Z)
        {
            problems.Add(new OptionProblem("box",
                "holds no voxel: a voxel is in it when its centre (x + 0.5, y + 0.5, z + 0.5 from its whole-metre corner) is"));
            return null;
        }

        DigBox box = new DigBox(low, high);
        if (box.Cells > most)
        {
            problems.Add(new OptionProblem("box",
                $"holds {box.Cells} voxels, more than {most} (a {Edge(most)} m cube) {doing}: split it, by height for one"));
            return null;
        }

        return box;
    }

    /// <summary>The box of whole voxels from low to high, both included (low at or under high on every axis).</summary>
    internal static DigBox Between(VoxelPoint low, VoxelPoint high) => new DigBox(low, high);

    /// <summary>The same box grown by margin voxels on every side.</summary>
    internal DigBox Grown(int margin) =>
        new DigBox(new VoxelPoint(Low.X - margin, Low.Y - margin, Low.Z - margin),
            new VoxelPoint(High.X + margin, High.Y + margin, High.Z + margin));

    /// <summary>A voxel's place in an array over the box (x fastest, then z, then y); -1 outside it.</summary>
    internal int IndexOf(VoxelPoint voxel) =>
        voxel.X < Low.X || voxel.X > High.X || voxel.Y < Low.Y || voxel.Y > High.Y || voxel.Z < Low.Z ||
        voxel.Z > High.Z
            ? -1
            : ((voxel.Y - Low.Y) * SizeZ + (voxel.Z - Low.Z)) * SizeX + (voxel.X - Low.X);

    /// <summary>The voxel at an array index (IndexOf's inverse).</summary>
    internal VoxelPoint At(int index) =>
        new VoxelPoint(Low.X + index % SizeX, Low.Y + index / (SizeX * SizeZ), Low.Z + index / SizeX % SizeZ);

    /// <summary>Whether a voxel lies on the box's outer layer.</summary>
    internal bool OnEdge(VoxelPoint voxel) =>
        voxel.X == Low.X || voxel.X == High.X || voxel.Y == Low.Y || voxel.Y == High.Y || voxel.Z == Low.Z ||
        voxel.Z == High.Z;

    /// <summary>Whether the game digs this voxel at all (above bedrock).</summary>
    internal static bool AboveBedrock(VoxelPoint voxel) => voxel.Y >= BedrockY;

    /// <summary>Whether a point lies in the voxel-aligned box.</summary>
    internal bool Holds(Vec3 point) =>
        point.X >= Min.X && point.X <= Max.X && point.Y >= Min.Y && point.Y <= Max.Y && point.Z >= Min.Z &&
        point.Z <= Max.Z;

    /// <summary>Every voxel, top layer first, so a dig cut short leaves its lower layers whole.</summary>
    internal IEnumerable<VoxelPoint> TopDown()
    {
        for (int y = High.Y; y >= Low.Y; y--)
        {
            for (int x = Low.X; x <= High.X; x++)
            {
                for (int z = Low.Z; z <= High.Z; z++)
                {
                    yield return new VoxelPoint(x, y, z);
                }
            }
        }
    }

    /// <summary>Every voxel in array order (IndexOf): bottom layer first, so a fill cut short rests on what it filled.</summary>
    internal IEnumerable<VoxelPoint> BottomUp()
    {
        for (int y = Low.Y; y <= High.Y; y++)
        {
            for (int z = Low.Z; z <= High.Z; z++)
            {
                for (int x = Low.X; x <= High.X; x++)
                {
                    yield return new VoxelPoint(x, y, z);
                }
            }
        }
    }

    private static long Span(int low, int high) => (long)high - low + 1;

    private static long Edge(long cells) => (long)Math.Round(Math.Pow(cells, 1.0 / 3.0));

    // The lowest whole metre p with p + 0.5 at or above edge, and the highest with p + 0.5 at or below it.
    private static int FirstIn(double edge) => (int)Math.Ceiling(edge - 0.5);

    private static int LastIn(double edge) => (int)Math.Floor(edge - 0.5);
}

/// <summary>
/// What remove terrain does with the ore in the voxels it digs. Drop: each ore falls as an ore item where it lay, as a
/// mining charge leaves it (Explosion.Explode: Vein.TryMineServer, Explosion.cs:57-60). Discard: none is made, as a
/// deep miner's drill takes the ground it passes (Vein.TryRemoveServer, DeepMiner.cs:351-352). Either way the ore is
/// gone from the vein, on the clients too: their replay of a voxel set under half density mines it there
/// (VoxelChangeEvent.Execute, Vein.MineAtPositionClient, VoxelTerrain.cs:63-76).
/// </summary>
internal abstract class OreHandling
{
    private OreHandling(string name)
    {
        Name = name;
    }

    internal static OreHandling Dropped { get; } = new Drop();

    internal static OreHandling Discarded { get; } = new Discard();

    internal string Name { get; }

    /// <summary>The option's value (drop when left out; the catalogue's enum allows nothing else).</summary>
    internal static OreHandling Of(string? given) => given == Discarded.Name ? Discarded : Dropped;

    internal sealed class Drop : OreHandling
    {
        internal Drop()
            : base("drop")
        {
        }
    }

    internal sealed class Discard : OreHandling
    {
        internal Discard()
            : base("discard")
        {
        }
    }
}

/// <summary>One ore a dig releases: its prefab, how many of its minables, and the quantity dropped (unknown in a report).</summary>
internal sealed class OreTally
{
    internal OreTally(string prefabName)
    {
        PrefabName = prefabName;
    }

    public string PrefabName { get; }

    public int Minables { get; private set; }

    public int? Quantity { get; private set; }

    internal void Add(int? quantity)
    {
        Minables++;
        if (quantity is { } dropped)
        {
            Quantity = (Quantity ?? 0) + dropped;
        }
    }
}

/// <summary>
/// The count of a dig's voxels as it reads or digs them: dug (or to dig), already empty, kept at bedrock, and the ore
/// released by prefab in the order first met.
/// </summary>
internal sealed class DigTally
{
    private readonly List<OreTally> _ores = new List<OreTally>();

    internal int Dug { get; private set; }

    internal int AlreadyEmpty { get; private set; }

    internal int BedrockKept { get; private set; }

    internal IReadOnlyList<OreTally> Ores => _ores;

    internal void Solid() => Dug++;

    internal void Empty() => AlreadyEmpty++;

    internal void Bedrock() => BedrockKept++;

    /// <summary>Voxels a fill raises (or would raise) to its density.</summary>
    internal int Filled { get; private set; }

    /// <summary>Voxels already as the fill would leave them.</summary>
    internal int Unchanged { get; private set; }

    /// <summary>Voxels a fill leaves alone because a structure stands in their cell.</summary>
    internal int StructureKept { get; private set; }

    internal void Fill() => Filled++;

    internal void Same() => Unchanged++;

    internal void Built() => StructureKept++;

    /// <summary>One minable released: its ore's prefab, and the quantity dropped (null when none was made or known).</summary>
    internal void Ore(string prefabName, int? quantity)
    {
        foreach (OreTally ore in _ores)
        {
            if (ore.PrefabName == prefabName)
            {
                ore.Add(quantity);
                return;
            }
        }

        OreTally first = new OreTally(prefabName);
        first.Add(quantity);
        _ores.Add(first);
    }
}
