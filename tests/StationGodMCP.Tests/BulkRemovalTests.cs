#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Hub;
using StationGodMCP.Tests.Runs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// remove structure in bulk: a box (with a prefab filter) names every structure in it, one job however many; the
/// removal's checks read each neighbour once for the whole plan, not once per piece beside it.
/// </summary>
public sealed class BulkRemovalTests
{
    private static RemovalBox? Box(double x1, double y1, double z1, double x2, double y2, double z2,
        out List<OptionProblem> problems)
    {
        problems = new List<OptionProblem>();
        return RemovalBox.Of(new BoxCorners(new Vec3(x1, y1, z1), new Vec3(x2, y2, z2)), problems);
    }

    [Fact]
    public void ABoxHoldsWhatLiesInItCornersIncluded()
    {
        RemovalBox box = Box(10, 240, -5, 0, 236, 5, out _)!;
        Assert.Equal(0, box.Min.X);
        Assert.Equal(240, box.Max.Y);
        Assert.True(box.Holds(0, 236, -5));
        Assert.True(box.Holds(10, 240, 5));
        Assert.True(box.Holds(5.25, 238.5, 0));
        Assert.False(box.Holds(10.5, 238, 0));
        Assert.False(box.Holds(5, 235.5, 0));
    }

    [Fact]
    public void ABoxIsAtMost32MetresASideAndAtMost256StructuresAreTakenInOneCall()
    {
        Assert.Null(Box(0, 0, 0, 40, 1, 1, out List<OptionProblem> problems));
        Assert.Contains(problems, problem => problem.Path == "box" && problem.Problem.Contains("32 m"));
        Assert.NotNull(Box(0, 0, 0, 32, 32, 32, out _));
        Assert.Null(RemovalBox.TooMany(256));
        Assert.Contains("257 structures", RemovalBox.TooMany(257));
    }

    [Fact]
    public void AScanVisitsEveryCellNearTheBoxOnce()
    {
        List<double> steps = RemovalBox.Steps(1.2, 3.0, 0.5, 0.5);
        Assert.Equal(new List<double> { 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5 }, steps);
        Assert.Equal(new List<double> { -2.0, 0.0, 2.0, 4.0 }, RemovalBox.Steps(0.0, 2.0, 2.0, 2.0));
    }

    // 256 frames in a 16 x 16 floor: each piece's support check reads the devices on the faces around it, and a device
    // between four frames is asked about four times. The plan reads each one once: 64 reads, not 256.
    [Fact]
    public void APlanReadsEachNeighbourOnceHoweverManyPiecesItStandsBeside()
    {
        int reads = 0;
        PlanMemo<long, string> bodies = new PlanMemo<long, string>(id =>
        {
            reads++;
            return "body " + id;
        });
        int asked = 0;
        for (int x = 0; x < 16; x++)
        {
            for (int z = 0; z < 16; z++)
            {
                // The device at each corner of four frames: (x / 2, z / 2) on an 8 x 8 grid of devices.
                long device = (x / 2) * 8 + z / 2;
                Assert.Equal("body " + device, bodies.Get(device));
                asked++;
            }
        }

        Assert.Equal(256, asked);
        Assert.Equal(64, reads);
        Assert.Equal(64, bodies.Computed);
    }
}

/// <summary>A run that takes down a box of scratch pieces sends one remove structure call: one job, not one a piece.</summary>
[Collection(PythonCollection.Name)]
public sealed class BulkRemovalRunTests
{
    [PythonFact]
    public async Task ABoxIsOneRemovalCall()
    {
        await using RunBench bench = new();

        string text = await bench.RunAsync(
            "sg.remove('structure', box=[[0, 236, -5], [10, 240, 5]], prefab='StructurePipeStraight')", confirm: true);

        Assert.True(text.StartsWith("script-1 done"), text);
        Assert.Equal(1, bench.World.Count("remove.structure"));
    }
}
