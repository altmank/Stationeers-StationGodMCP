#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects.Entities;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Scheduling;
using TerrainSystem;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// build terrain and find void: the ground of a box read voxel by voxel, then filled (or levelled) as the game's own
/// dirt placement fills a voxel.
///
/// How the game fills (CODE): the Voxel Tool places dirt with OnServer.PlaceVoxelAtWorldPosition, which sets the
/// voxel's density to 1 with RoomChangeSource.VoxelAdd (VoxelTool.cs:95-97, OnServer.cs:786-811); the console's
/// voxelfillnear sets each voxel back to its generated density the same way (VoxelFillNearCommand.cs:71-93). VoxelAdd
/// gives the cell the planet's air only where it has no atmosphere of its own (ApplyVoxelWorldEffects,
/// AtmosphericEventInstance.CloneGlobal, AtmosphericsController.CloneGlobalAtmosphere keeps a cell's own,
/// AtmosphericsController.cs:289-313), then re-checks rooms. The change reaches clients as a dig's does
/// (VoxelChangeEvent). The fill sets densities through TerrainDigs.Set, so its digging (mode level, or original where
/// ground was added) releases ore as a dig does.
///
/// A voxel's cell holds a structure when its 2 m cell lists one (Cell.AllStructures: frames, walls, large devices) or
/// one of the eight 0.5 m cells it spans holds a piece or device (SmallCell.IsValid, SmallCell.cs:114-121).
/// </summary>
internal static class TerrainFills
{
    private const int ReadsPerStep = 512;

    // The eight 0.5 m cell centres inside a voxel, from its low corner.
    private static readonly float[] Quarters = { 0.25f, 0.75f };

    /// <summary>Every voxel of the box as it reads now, in DigBox.IndexOf order: a body over frames.</summary>
    internal static IEnumerable<object?> Read(DigBox box, Answer<VoxelReading[]> answer)
    {
        TerrainDigs.RequireHost();
        VoxelReading[] readings = new VoxelReading[box.Cells];
        int index = 0;
        foreach (VoxelPoint voxel in box.BottomUp())
        {
            readings[index] = ReadingOf(voxel);
            if (++index % ReadsPerStep == 0)
            {
                yield return null;
            }
        }

        answer.Value = readings;
    }

    /// <summary>
    /// The fill planned over a box: the readings, the voxels in an enclosed pocket (voids mode), each voxel's choice
    /// counted, and the players whose body a raised voxel would take (refused: a fill never buries a player).
    /// </summary>
    internal static FillPlan Plan(DigBox box, FillMode mode, OreHandling ores, VoxelReading[] readings)
    {
        bool[] inVoid = new bool[readings.Length];
        int? voids = null;
        if (mode is FillMode.Voids)
        {
            List<TerrainVoid> found = VoidSearch.Find(box, readings).Voids;
            voids = found.Count;
            foreach (TerrainVoid pocket in found)
            {
                foreach (int cell in pocket.Cells)
                {
                    inVoid[cell] = true;
                }
            }
        }

        DigTally tally = new DigTally();
        List<Human> bodies = PlayersNear(box);
        List<string> buried = new List<string>();
        foreach (VoxelPoint voxel in box.BottomUp())
        {
            int index = box.IndexOf(voxel);
            VoxelChoice choice = VoxelChoice.Of(mode, voxel, readings[index], inVoid[index]);
            choice.Count(tally);
            if (choice is VoxelChoice.Set { Adds: true })
            {
                foreach (Human body in bodies)
                {
                    if (new PlayerBody(V(body.Position)).Overlaps(voxel) && !buried.Contains(body.DisplayName))
                    {
                        buried.Add(body.DisplayName);
                    }
                }
            }
        }

        if (buried.Count > 0)
        {
            throw ApiErrors.Refused("player_in_fill",
                $"The fill would bury {string.Join(", ", buried)}: a voxel it fills is where they stand. Move them " +
                "(move player) or leave their place out of the box.");
        }

        return new FillPlan(box, mode, ores, readings, inVoid, voids,
            new TerrainFillView(box, mode, tally, voids, ores, TerrainDigs.PlayersIn(box)));
    }

    /// <summary>The checks of a fill as a body over frames: the box read, then planned (Plan).</summary>
    internal static IEnumerable<object?> Checked(DigBox box, FillMode mode, OreHandling ores, Answer<FillPlan> answer)
    {
        Answer<VoxelReading[]> readings = new Answer<VoxelReading[]>();
        foreach (object? step in Read(box, readings))
        {
            yield return step;
        }

        answer.Value = Plan(box, mode, ores, readings.Value);
    }

    /// <summary>The fill itself: work the slice lane steps until every voxel is visited.</summary>
    internal static IPendingResult Fill(FillPlan plan) => new Filling(plan);

    /// <summary>The enclosed pockets of a box (find void): read over frames, then searched.</summary>
    internal static TerrainVoidsView Voids(DigBox box, VoxelReading[] readings, int minCells)
    {
        VoidSearch.Result found = VoidSearch.Find(box, readings);
        List<Human> bodies = PlayersNear(box);
        List<VoidView> shown = new List<VoidView>();
        int smaller = 0;
        foreach (TerrainVoid pocket in found.Voids)
        {
            if (pocket.Cells.Count < minCells)
            {
                smaller++;
                continue;
            }

            List<ThingView> inside = new List<ThingView>();
            foreach (Human body in bodies)
            {
                int at = box.IndexOf(VoxelOf(body.Position));
                if (at >= 0 && pocket.Cells.Contains(at))
                {
                    inside.Add(GameLookup.ViewOf(body));
                }
            }

            shown.Add(new VoidView(shown.Count, pocket, box.At(pocket.Cells[0]), inside));
        }

        return new TerrainVoidsView(box, shown, smaller, found);
    }

    private static VoxelReading ReadingOf(VoxelPoint voxel)
    {
        Vector3 at = TerrainDigs.At(voxel);
        Vector3Int point = new Vector3Int(voxel.X, voxel.Y, voxel.Z);
        return new VoxelReading(VoxelTerrain.GetNodeInfoWorldSpace(at).Density,
            VoxelTerrain.GetReadonlyDensityWorldSpace(point), Built(voxel));
    }

    private static bool Built(VoxelPoint voxel)
    {
        GridController world = GridController.World;
        Cell? large = world.GetCell(new Vector3(voxel.X + 0.5f, voxel.Y + 0.5f, voxel.Z + 0.5f));
        if (large != null && large.AllStructures.Count > 0)
        {
            return true;
        }

        foreach (float dx in Quarters)
        {
            foreach (float dy in Quarters)
            {
                foreach (float dz in Quarters)
                {
                    SmallCell? small = world.GetSmallCell(new Vector3(voxel.X + dx, voxel.Y + dy, voxel.Z + dz));
                    if (small != null && small.IsValid())
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // The players near the box: within 2 m of it, so a body standing on its top layer counts.
    private static List<Human> PlayersNear(DigBox box)
    {
        DigBox near = box.Grown(2);
        List<Human> bodies = new List<Human>();
        foreach (NamedPlayer<Human> player in PlayerOrigin.NamedPlayers())
        {
            if (near.Holds(V(player.Body.Position)))
            {
                bodies.Add(player.Body);
            }
        }

        return bodies;
    }

    private static Vec3 V(Vector3 point) => Bodies.V(point);

    private static VoxelPoint VoxelOf(Vector3 point) =>
        new VoxelPoint(Mathf.FloorToInt(point.x), Mathf.FloorToInt(point.y), Mathf.FloorToInt(point.z));

    // The fill: each voxel's choice made again as it is reached (the ground may have changed since the plan).
    private sealed class Filling : VoxelWork
    {
        private readonly FillPlan _plan;

        internal Filling(FillPlan plan)
            : base(plan.Box.BottomUp())
        {
            _plan = plan;
        }

        protected override bool Visit(VoxelPoint voxel)
        {
            int index = _plan.Box.IndexOf(voxel);
            VoxelReading now = ReadingOf(voxel);
            VoxelChoice choice = VoxelChoice.Of(_plan.Mode, voxel, now, _plan.InVoid[index]);
            choice.Count(Tally);
            if (!(choice is VoxelChoice.Set set))
            {
                return false;
            }

            TerrainDigs.Set(voxel, set.Density, set.Opens, _plan.Ores, Tally);
            return true;
        }

        public override object Result() =>
            new TerrainFillView(_plan.Box, _plan.Mode, Tally, _plan.Voids, _plan.Ores, TerrainDigs.PlayersIn(_plan.Box));
    }
}

/// <summary>A checked fill: its box, mode, ore handling, the voxels of its pockets, and the report's reading.</summary>
internal sealed class FillPlan
{
    internal FillPlan(DigBox box, FillMode mode, OreHandling ores, VoxelReading[] readings, bool[] inVoid, int? voids,
        TerrainFillView reading)
    {
        Box = box;
        Mode = mode;
        Ores = ores;
        Readings = readings;
        InVoid = inVoid;
        Voids = voids;
        Reading = reading;
    }

    internal DigBox Box { get; }

    internal FillMode Mode { get; }

    internal OreHandling Ores { get; }

    internal VoxelReading[] Readings { get; }

    internal bool[] InVoid { get; }

    internal int? Voids { get; }

    internal TerrainFillView Reading { get; }
}
