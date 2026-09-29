#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>undo_job's planner and the print provenance log (1.4.3).</summary>
public sealed class UndoAndPrintsTests
{
    private static ThingSnapshot Straight(long id, bool onGrid = true) =>
        new ThingSnapshot(id, "StructureCableStraightH", new Vec3(719, 200, 667.5),
            onGrid ? CubeRotation.FromFacing(RunModels.Step("+x"), RunModels.Step("+y")) : null, 0, null);

    private static JobFacts Job(string status = "applied", string tool = "place_cables")
    {
        List<(long, string?)> created = new List<(long, string?)> { (500, "StructureCableJunctionH"), (501, null) };
        Dictionary<long, ThingSnapshot> snapshots = new Dictionary<long, ThingSnapshot> { [400] = Straight(400) };
        return new JobFacts("run-7", tool, status, created, new List<long> { 400 }, snapshots);
    }

    [Fact]
    public void AnAppliedRunIsUndoneByRemovingWhatItBuiltAndRestoringWhatItReplaced()
    {
        Dictionary<long, string> world = new Dictionary<long, string>
        {
            [500] = "StructureCableJunctionH", [501] = "StructureCableStraightH"
        };
        UndoPlan plan = UndoPlanner.Plan(Job(), id => world.TryGetValue(id, out string prefab) ? prefab : null);
        Assert.True(plan.Ready);
        Assert.Equal(new List<long> { 500, 501 }, plan.Remove);
        Assert.Single(plan.Restore);
        Assert.Equal(400, plan.Restore[0].Id);
    }

    [Fact]
    public void AJobAlreadyUndoneSaysSoAndNamesTheUndosJobs()
    {
        JobFacts undone = new JobFacts("run-10", "place_cables", "applied",
            new List<(long, string?)> { (465, null) }, new List<long> { 463 },
            new Dictionary<long, ThingSnapshot> { [463] = Straight(463) },
            new List<UndoStep> { new UndoStep("run-11", "applied") });
        UndoPlan plan = UndoPlanner.Plan(undone, _ => null);
        Assert.False(plan.Ready);
        Assert.Equal(new List<string> { "run-11" }, plan.UndoneBy);
        Assert.Contains(plan.Diverged, reason => reason.Contains("already undone") && reason.Contains("run-11 (applied)"));
        Assert.DoesNotContain(plan.Diverged, reason => reason.Contains("is gone"));
    }

    [Fact]
    public void AnEarlierUndoRefusedWholeDoesNotCountAsUndone()
    {
        Dictionary<long, string> world = new Dictionary<long, string>
        {
            [500] = "StructureCableJunctionH", [501] = "StructureCableStraightH"
        };
        JobFacts job = new JobFacts("run-7", "place_cables", "applied",
            new List<(long, string?)> { (500, "StructureCableJunctionH"), (501, null) }, new List<long> { 400 },
            new Dictionary<long, ThingSnapshot> { [400] = Straight(400) },
            new List<UndoStep> { new UndoStep("run-8", "refused") });
        UndoPlan plan = UndoPlanner.Plan(job, id => world.TryGetValue(id, out string prefab) ? prefab : null);
        Assert.True(plan.Ready);
        Assert.Null(plan.UndoneBy);
        Assert.Contains(plan.Notes, note => note.Contains("refused"));
    }

    [Fact]
    public void AnUndoStepNoLongerKeptStillCountsAsUndone() =>
        Assert.False(new UndoStep("run-11", UndoStep.Unknown).Refused);

    [Fact]
    public void AGoneOrChangedPieceMeansTheWorldDiverged()
    {
        UndoPlan gone = UndoPlanner.Plan(Job(), id => id == 501 ? "StructureCableStraightH" : null);
        Assert.False(gone.Ready);
        Assert.Contains(gone.Diverged, reason => reason.Contains("500"));
        UndoPlan changed = UndoPlanner.Plan(Job(), id => id == 400 ? null : "StructureCableCornerH");
        Assert.Contains(changed.Diverged, reason => reason.Contains("is now StructureCableCornerH"));
    }

    [Fact]
    public void RemovalsWithoutASnapshotOrOffTheGridCannotBeRestored()
    {
        JobFacts noSnapshot = new JobFacts("remove-2", "remove_structure", "applied", new List<(long, string?)>(),
            new List<long> { 900 }, new Dictionary<long, ThingSnapshot>());
        Assert.Contains(UndoPlanner.Plan(noSnapshot, _ => null).Diverged, reason => reason.Contains("no snapshot"));
        JobFacts offGrid = new JobFacts("remove-3", "remove_structure", "applied", new List<(long, string?)>(),
            new List<long> { 400 }, new Dictionary<long, ThingSnapshot> { [400] = Straight(400, false) });
        Assert.Contains(UndoPlanner.Plan(offGrid, _ => null).Diverged, reason => reason.Contains("off the grid"));
    }

    [Fact]
    public void OnlyFinishedPlaceAndRemoveJobsAreUndone()
    {
        Assert.False(UndoPlanner.Plan(Job("running"), _ => "x").Ready);
        Assert.False(UndoPlanner.Plan(Job(tool: "replace_walls"), _ => "x").Ready);
        UndoPlan stopped = UndoPlanner.Plan(Job("stopped"), id => id == 400 ? null : "StructureCableJunctionH");
        Assert.Contains(stopped.Notes, note => note.Contains("stopped"));
    }

    [Fact]
    public void ARemovedThingThatStandsAgainIsNotRebuilt()
    {
        UndoPlan plan = UndoPlanner.Plan(Job(),
            id => id == 500 ? "StructureCableJunctionH" : "StructureCableStraightH");
        Assert.Empty(plan.Restore);
        Assert.Contains(plan.Notes, note => note.Contains("still stands"));
    }

    [Fact]
    public void PrintsAreKeptPerItemAndSplitsInheritTheirMaker()
    {
        PrintLog log = new PrintLog(3);
        log.Record(new PrintRecord(10, "ItemIronSheets", 77, "StructureAutolathe", "Lathe", 100.0, 50, null));
        log.Split(10, 11, 20);
        PrintRecord split = log.Of(11)!;
        Assert.Equal(77, split.MakerId);
        Assert.Equal(10, split.SplitFrom);
        Assert.Equal(20, split.Quantity);
        log.Split(999, 12, 1);
        Assert.Null(log.Of(12));
        log.Record(new PrintRecord(13, "A", 1, null, null, 1, 1, null));
        log.Record(new PrintRecord(14, "B", 1, null, null, 1, 1, null));
        Assert.Null(log.Of(10));
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void PrintFiltersMatchMakerAndTime()
    {
        PrintRecord record = new PrintRecord(10, "ItemIronSheets", 77, "StructureAutolathe", "Lathe", 100.0, 50, null);
        Assert.True(new PrintFilter(null, null, null).Keeps(null));
        Assert.True(new PrintFilter(77, null, null).Keeps(record));
        Assert.False(new PrintFilter(78, null, null).Keeps(record));
        Assert.True(new PrintFilter(null, "autolathe", 99.0).Keeps(record));
        Assert.False(new PrintFilter(null, null, 101.0).Keeps(record));
        Assert.False(new PrintFilter(null, "lathe", null).Keeps(null));
    }
}
