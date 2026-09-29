#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Fixes from round 3 of the cables live test of 1.4.4.</summary>
public sealed class CablesRound3Tests
{
    private static readonly ExtraEnd[] NoExtra = new ExtraEnd[0];

    private static List<GridCell> Run(params GridCell[] waypoints) => RunPath.FromWaypoints(waypoints, out _)!;

    private static GridCell At(int x, int y, int z) => RunModels.At(x, y, z);

    // cables-22 / cables-32: the OpenEnds entry and the OutputConnection field are separate copies of one port, so
    // the shared Unity components decide which side it is, and the role only without them.
    [Theory]
    [InlineData(true, null, ChuteRoles.Input, true)]
    [InlineData(false, null, ChuteRoles.Output, false)]
    [InlineData(null, true, ChuteRoles.None, true)]
    [InlineData(null, false, ChuteRoles.Output, false)]
    [InlineData(null, null, ChuteRoles.Output, true)]
    [InlineData(null, null, ChuteRoles.Output2, true)]
    [InlineData(null, null, ChuteRoles.Input, false)]
    public void APortIsTheOutputByItsSharedComponentsElseItsRole(bool? sameTransform, bool? sameCollider, int role,
        bool output)
    {
        Assert.Equal(output, PortSides.IsOutput(sameTransform, sameCollider, role));
    }

    // cables-33: undoing a burnt-cable repair removes the replacement and leaves the burnt piece gone.
    [Fact]
    public void ABurntCableTheJobRemovedIsNotedAndTheRestIsUndone()
    {
        ThingSnapshot burnt = new ThingSnapshot(621, "StructureCableStraightBurnt", new Vec3(-1126.5, 232, -701),
            null, 0, null, null, burnt: true);
        JobFacts job = new JobFacts("run-31", "place_cables", "applied",
            new List<(long, string?)> { (645, "StructureCableStraight") }, new List<long> { 621 },
            new Dictionary<long, ThingSnapshot> { [621] = burnt });

        UndoPlan plan = UndoPlanner.Plan(job, id => id == 645 ? "StructureCableStraight" : null);

        Assert.True(plan.Ready);
        Assert.Equal(new List<long> { 645 }, plan.Remove);
        Assert.Empty(plan.Restore);
        Assert.Empty(plan.Diverged);
        Assert.Contains(plan.Notes, note => note.Contains("621") && note.Contains("burnt"));
    }

    // cables-34: a run end on a network's piece has arrived there; another piece's open end pointing into that cell
    // from the side (another network's) is not joined.
    [Fact]
    public void ARunEndOnAnExistingPieceDoesNotJoinAnOpenEndFromTheSide()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(1230, At(2, 0, 0), "+z", "-z"));
        around.AddPiece(RunModels.Piece(1228, At(1, 0, 0), "+x", "-x"));

        RunLayout layout = RunLayoutPlanner.Plan(Run(At(2, 0, 3), At(2, 0, 0)), around, JoinMode.Ends, NoExtra,
            null);

        LayoutCell last = layout.At(At(2, 0, 0))!;
        Assert.Equal(RunModels.Ends("+z", "-z"), last.Ends);
        Assert.DoesNotContain(last.Joins, join => join.TargetId == 1228);
    }

    [Fact]
    public void JoinAllStillJoinsAnOpenEndFromTheSideOfAnExistingPiece()
    {
        RunSurroundings around = new RunSurroundings();
        around.AddPiece(RunModels.Piece(1230, At(2, 0, 0), "+z", "-z"));
        around.AddPiece(RunModels.Piece(1228, At(1, 0, 0), "+x", "-x"));

        RunLayout layout = RunLayoutPlanner.Plan(Run(At(2, 0, 3), At(2, 0, 0)), around, JoinMode.All, NoExtra, null);

        Assert.Contains(layout.At(At(2, 0, 0))!.Joins, join => join.Kind == "piece" && join.TargetId == 1228);
    }
}
