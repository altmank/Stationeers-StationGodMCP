#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Pure;

/// <summary>
/// One voxel as a fill reads it: its density now and as the world generated it (bytes, 0 empty to 255 solid; the
/// game keeps both, VoxelTerrain.GetNodeInfoWorldSpace and GetReadonlyDensityWorldSpace, VoxelTerrain.cs:891-910),
/// whether it lies inside structures (TerrainFills.Built), and whether its material is still the generated one (a dig
/// to density 0 clears it, VoxelOctree.cs:524-527, and a refill without a material leaves it cleared).
/// </summary>
internal readonly struct VoxelReading
{
    internal VoxelReading(byte density, byte generated, bool built, bool materialAsGenerated = true)
    {
        Density = density;
        Generated = generated;
        Built = built;
        MaterialAsGenerated = materialAsGenerated;
    }

    internal byte Density { get; }

    internal byte Generated { get; }

    internal bool Built { get; }

    /// <summary>The voxel's material (stone, ice, an ore) is the one the world generated it with.</summary>
    internal bool MaterialAsGenerated { get; }
}

/// <summary>
/// Densities as the game reads them. A voxel under half density is open ground: digging below it releases the ore
/// there (Vein.ShouldReleaseMinables, density &lt; 127, Vein.cs:640-643), and a client's replay of a voxel set under it
/// mines it (VoxelTerrain.cs:67-70). Solid is full density, what the game's own dirt placement sets
/// (OnServer.PlaceVoxelAtWorldPosition: density 1, OnServer.cs:794).
/// </summary>
internal static class Densities
{
    internal const byte Empty = 0;
    internal const byte Solid = 255;
    internal const byte Half = 127;

    internal static bool IsOpen(byte density) => density < Half;
}

/// <summary>
/// How build terrain fills its box. Solid: every voxel to full density. Original: every voxel back to the density the
/// world generated it with, as the console's voxelfillnear reverts ground (VoxelFillNearCommand.cs:71-93), so ground a
/// player added is taken away too. Level: a flat plane, every voxel whose centre lies under the level filled and every
/// one above it dug, so the ground ends at the voxel layer under the level. Voids: only the enclosed empty pockets of
/// the box (VoidSearch) filled solid.
/// </summary>
internal abstract class FillMode
{
    internal const string SolidName = "solid";
    internal const string OriginalName = "original";
    internal const string LevelName = "level";
    internal const string VoidsName = "voids";

    private FillMode(string name)
    {
        Name = name;
    }

    internal string Name { get; }

    /// <summary>The mode the options name (solid when left out), or null with the problem: level with or without a height.</summary>
    internal static FillMode? Of(string? mode, double? level, List<OptionProblem> problems)
    {
        string name = mode ?? SolidName;
        if (name == LevelName)
        {
            if (level is { } height)
            {
                return new Level(height);
            }

            problems.Add(new OptionProblem("level", "is required with mode level: the plane's height in metres"));
            return null;
        }

        if (level != null)
        {
            problems.Add(new OptionProblem("level", "goes only with mode level"));
            return null;
        }

        return name switch
        {
            OriginalName => new Original(),
            VoidsName => new Voids(),
            _ => new Solid()
        };
    }

    /// <summary>
    /// The density this mode wants at a voxel, or null to leave it (it already is so, or the mode does not touch it).
    /// inVoid: the voxel lies in an enclosed pocket VoidSearch found (the voids mode's only voxels).
    /// </summary>
    internal abstract byte? Want(VoxelPoint voxel, VoxelReading reading, bool inVoid);

    internal sealed class Solid : FillMode
    {
        internal Solid()
            : base(SolidName)
        {
        }

        internal override byte? Want(VoxelPoint voxel, VoxelReading reading, bool inVoid) => Densities.Solid;
    }

    internal sealed class Original : FillMode
    {
        internal Original()
            : base(OriginalName)
        {
        }

        internal override byte? Want(VoxelPoint voxel, VoxelReading reading, bool inVoid) => reading.Generated;
    }

    internal sealed class Level : FillMode
    {
        internal Level(double height)
            : base(LevelName)
        {
            Height = height;
        }

        /// <summary>The plane's height in metres: voxels whose centre lies under it are filled, the rest dug.</summary>
        internal double Height { get; }

        internal override byte? Want(VoxelPoint voxel, VoxelReading reading, bool inVoid) =>
            voxel.Centre.Y < Height ? Densities.Solid : Densities.Empty;
    }

    internal sealed class Voids : FillMode
    {
        internal Voids()
            : base(VoidsName)
        {
        }

        internal override byte? Want(VoxelPoint voxel, VoxelReading reading, bool inVoid) =>
            inVoid ? Densities.Solid : null;
    }
}

/// <summary>What a fill does at one voxel: the density it sets, or why it leaves the voxel as it is.</summary>
internal abstract class VoxelChoice
{
    private VoxelChoice()
    {
    }

    internal static VoxelChoice Untouched { get; } = new Keep(KeepReason.Untouched);

    /// <summary>
    /// The fill's choice at a voxel: bedrock is never changed (the game does nothing there, VoxelTerrain.cs:953); a
    /// voxel inside structures (reading.Built: all eight cubes around it built) is kept, so ground meets a frame or wall
    /// at its faces (the game's own dirt placement checks only the density, VoxelTool.cs:95-97; this guard is
    /// StationGod's); a voxel already at the density the mode wants is the same; any other is set. A pipe, cable or
    /// chute lying in open ground fills one cube, never all eight around a voxel, so the fill covers it: TerrainCover
    /// names those pieces in the report.
    /// </summary>
    internal static VoxelChoice Of(FillMode mode, VoxelPoint voxel, VoxelReading reading, bool inVoid)
    {
        if (!(mode.Want(voxel, reading, inVoid) is { } wanted))
        {
            return Untouched;
        }

        if (!DigBox.AboveBedrock(voxel))
        {
            return new Keep(KeepReason.Bedrock);
        }

        if (reading.Built)
        {
            return new Keep(KeepReason.Structure);
        }

        bool materialRight = !(mode is FillMode.Original) || reading.MaterialAsGenerated;
        return wanted == reading.Density && materialRight ? new Keep(KeepReason.Same) : new Set(wanted, reading.Density);
    }

    /// <summary>Counts the choice into a tally: filled, dug, the same, kept at a structure or at bedrock.</summary>
    internal abstract void Count(DigTally tally);

    internal enum KeepReason
    {
        Untouched,
        Same,
        Structure,
        Bedrock
    }

    internal sealed class Keep : VoxelChoice
    {
        internal Keep(KeepReason reason)
        {
            Reason = reason;
        }

        internal KeepReason Reason { get; }

        internal override void Count(DigTally tally)
        {
            switch (Reason)
            {
                case KeepReason.Same:
                    tally.Same();
                    break;
                case KeepReason.Structure:
                    tally.Built();
                    break;
                case KeepReason.Bedrock:
                    tally.Bedrock();
                    break;
            }
        }
    }

    internal sealed class Set : VoxelChoice
    {
        internal Set(byte density, byte before)
        {
            Density = density;
            Before = before;
        }

        internal byte Density { get; }

        internal byte Before { get; }

        /// <summary>Raises the density: the game's VoxelAdd (a dirt placement); otherwise a dig, VoxelRemove.</summary>
        internal bool Adds => Density > Before;

        /// <summary>Takes the voxel under half density from at or over it: the ore there is released.</summary>
        internal bool Opens => Densities.IsOpen(Density) && !Densities.IsOpen(Before);

        internal override void Count(DigTally tally)
        {
            if (Adds)
            {
                tally.Fill();
            }
            else if (Density == Before)
            {
                tally.Material();
            }
            else
            {
                tally.Solid();
            }
        }
    }
}

/// <summary>A player's body as a fill guards it: 1 m across, 2 m up from the feet (where Human.Position is).</summary>
internal readonly struct PlayerBody
{
    private const double HalfWidth = 0.5;
    private const double Height = 2.0;

    internal PlayerBody(Vec3 feet)
    {
        Feet = feet;
    }

    internal Vec3 Feet { get; }

    /// <summary>Whether the voxel's 1 m cube overlaps the body.</summary>
    internal bool Overlaps(VoxelPoint voxel) =>
        voxel.X < Feet.X + HalfWidth && voxel.X + 1 > Feet.X - HalfWidth &&
        voxel.Y < Feet.Y + Height && voxel.Y + 1 > Feet.Y &&
        voxel.Z < Feet.Z + HalfWidth && voxel.Z + 1 > Feet.Z - HalfWidth;
}

/// <summary>One enclosed empty pocket: its voxels (array indices over the searched box), bounds and whether a player is in it.</summary>
internal sealed class TerrainVoid
{
    internal TerrainVoid(List<int> cells, VoxelPoint low, VoxelPoint high)
    {
        Cells = cells;
        Low = low;
        High = high;
    }

    internal List<int> Cells { get; }

    internal VoxelPoint Low { get; }

    internal VoxelPoint High { get; }

    /// <summary>The box to give build terrain's voids mode: the pocket's bounds and one voxel more on every side, so the ground enclosing it lies in the box.</summary>
    internal DigBox FillBox => DigBox.Between(Low, High).Grown(1);
}

/// <summary>
/// The enclosed empty pockets of a box. A voxel is open when under half density (Densities.IsOpen), voxels join their
/// six face neighbours, and a set of joined open voxels is a pocket (a void) when it reaches neither the box's outer
/// layer (it may go on outside, up to the surface or into a dug hallway) nor any voxel whose cell holds a structure
/// (it is part of the base: a room, a frame's inside, a pipe run). Bedrock counts as ground. What reaches the box edge is
/// counted apart as open, so a bigger box can judge it.
/// </summary>
internal static class VoidSearch
{
    internal sealed class Result
    {
        internal Result(List<TerrainVoid> voids, int openToEdge, int openToEdgeCells, int touchingStructures)
        {
            Voids = voids;
            OpenToEdge = openToEdge;
            OpenToEdgeCells = openToEdgeCells;
            TouchingStructures = touchingStructures;
        }

        /// <summary>The pockets, largest first.</summary>
        internal List<TerrainVoid> Voids { get; }

        internal int OpenToEdge { get; }

        internal int OpenToEdgeCells { get; }

        internal int TouchingStructures { get; }
    }

    /// <summary>The pockets over a box whose voxels read so (an array in DigBox.IndexOf order).</summary>
    internal static Result Find(DigBox box, IReadOnlyList<VoxelReading> readings)
    {
        int count = readings.Count;
        bool[] seen = new bool[count];
        List<TerrainVoid> voids = new List<TerrainVoid>();
        int openToEdge = 0;
        int openToEdgeCells = 0;
        int touchingStructures = 0;
        Queue<int> queue = new Queue<int>();
        for (int start = 0; start < count; start++)
        {
            if (seen[start] || !Open(readings[start]))
            {
                continue;
            }

            List<int> cells = new List<int>();
            bool edge = false;
            bool built = false;
            VoxelPoint first = box.At(start);
            int lowX = first.X, lowY = first.Y, lowZ = first.Z, highX = first.X, highY = first.Y, highZ = first.Z;
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                VoxelPoint voxel = box.At(index);
                cells.Add(index);
                built |= readings[index].Built;
                edge |= box.OnEdge(voxel);
                lowX = Math.Min(lowX, voxel.X);
                lowY = Math.Min(lowY, voxel.Y);
                lowZ = Math.Min(lowZ, voxel.Z);
                highX = Math.Max(highX, voxel.X);
                highY = Math.Max(highY, voxel.Y);
                highZ = Math.Max(highZ, voxel.Z);
                foreach (VoxelPoint next in Neighbours(voxel))
                {
                    int at = box.IndexOf(next);
                    if (at >= 0 && !seen[at] && Open(readings[at]))
                    {
                        seen[at] = true;
                        queue.Enqueue(at);
                    }
                }
            }

            if (edge)
            {
                openToEdge++;
                openToEdgeCells += cells.Count;
            }
            else if (built)
            {
                touchingStructures++;
            }
            else
            {
                voids.Add(new TerrainVoid(cells, new VoxelPoint(lowX, lowY, lowZ), new VoxelPoint(highX, highY, highZ)));
            }
        }

        voids.Sort((a, b) => b.Cells.Count.CompareTo(a.Cells.Count));
        return new Result(voids, openToEdge, openToEdgeCells, touchingStructures);
    }

    // A voxel in a structure's cell is open space of the base whatever its density; bedrock counts as ground.
    private static bool Open(VoxelReading reading) => reading.Built || Densities.IsOpen(reading.Density);

    private static IEnumerable<VoxelPoint> Neighbours(VoxelPoint voxel)
    {
        yield return new VoxelPoint(voxel.X + 1, voxel.Y, voxel.Z);
        yield return new VoxelPoint(voxel.X - 1, voxel.Y, voxel.Z);
        yield return new VoxelPoint(voxel.X, voxel.Y + 1, voxel.Z);
        yield return new VoxelPoint(voxel.X, voxel.Y - 1, voxel.Z);
        yield return new VoxelPoint(voxel.X, voxel.Y, voxel.Z + 1);
        yield return new VoxelPoint(voxel.X, voxel.Y, voxel.Z - 1);
    }
}

/// <summary>
/// The pieces a fill covers: a 1 m cube holding a pipe, cable or chute and no large-grid structure (a piece lying in
/// open ground, not inside a frame) is covered when the fill adds ground at any of the eight voxels at its corners.
/// </summary>
internal static class TerrainCover
{
    /// <summary>Whether the fill adds ground at a corner of the cube from (x, y, z) up.</summary>
    internal static bool Covers(VoxelPoint cube, ICollection<VoxelPoint> added)
    {
        for (int dx = 0; dx <= 1; dx++)
        {
            for (int dy = 0; dy <= 1; dy++)
            {
                for (int dz = 0; dz <= 1; dz++)
                {
                    if (added.Contains(new VoxelPoint(cube.X + dx, cube.Y + dy, cube.Z + dz)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>The cubes that have the voxel as a corner: the eight cubes from (x - 1, y - 1, z - 1) to (x, y, z).</summary>
    internal static IEnumerable<VoxelPoint> CubesAround(VoxelPoint voxel)
    {
        for (int dx = -1; dx <= 0; dx++)
        {
            for (int dy = -1; dy <= 0; dy++)
            {
                for (int dz = -1; dz <= 0; dz++)
                {
                    yield return new VoxelPoint(voxel.X + dx, voxel.Y + dy, voxel.Z + dz);
                }
            }
        }
    }
}
