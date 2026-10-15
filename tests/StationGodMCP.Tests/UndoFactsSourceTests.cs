#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// undo's choice of a job's facts: the job's kept final view, planned as it ended; undo_expired when what undoing it
/// needs was evicted; else job_not_found. The lists the kept view carries (JobEndFacts) plan that same undo.
/// </summary>
public sealed class UndoFactsSourceTests
{
    // How the things the run removed stood: 55 (a straight) and 60 (the straight the tee took the place of).
    private static readonly Dictionary<long, ThingSnapshot> Snapshots = new Dictionary<long, ThingSnapshot>
    {
        [55] = Snapshot(55),
        [60] = Snapshot(60)
    };

    [Fact]
    public void AKeptJobIsPlannedFromItsOwnFinalView()
    {
        UndoFactsSource.Kept source = Assert.IsType<UndoFactsSource.Kept>(
            UndoFactsSource.Choose(Wire(Run()), jobId: "run-7"));

        Assert.Equal("build pipe", (string?)source.Facts["tool"]);
        Assert.Equal(new long[] { 101, 102 },
            JobEndFacts.Created(source.Facts).ConvertAll(created => created.Id).ToArray());
        Assert.Equal(new long[] { 55, 60 }, JobEndFacts.Removed(source.Facts).ToArray());

        UndoPlan plan = UndoPlanner.Plan(Facts(source.Facts), Standing);

        Assert.True(plan.Ready, string.Join("; ", plan.Diverged));
        Assert.Equal(new long[] { 101, 102 }, plan.Remove.ToArray());
        Assert.Equal(new long[] { 55, 60 }, plan.Restore.ConvertAll(snapshot => snapshot.Id).ToArray());
    }

    [Fact]
    public void AnIdTheModNeverHadIsNotFound() =>
        Assert.IsType<UndoFactsSource.NotFound>(UndoFactsSource.Choose(kept: null, jobId: "place-99"));

    [Fact]
    public void AJobWhoseSnapshotsWereEvictedIsExpiredAndSaysSo()
    {
        UndoFactsSource.Expired expired = Assert.IsType<UndoFactsSource.Expired>(
            UndoFactsSource.Choose(kept: null, snapshotEvicted: true, jobId: "place-1"));

        Assert.Equal("undo_expired", UndoFactsSource.Expired.Code);
        Assert.Equal("place-1", expired.JobId);
        Assert.Contains("place-1", expired.Message);
        Assert.Contains("evicted under the memory bound", expired.Message);
    }

    private static JobFacts Facts(JObject facts) =>
        new JobFacts("run-7", "build pipe", "applied", JobEndFacts.Created(facts), JobEndFacts.Removed(facts),
            Snapshots);

    // What the run left: its two pieces stand, the two it removed are gone.
    private static string? Standing(long id) => id switch
    {
        101 => "StructurePipeStraight",
        102 => "StructurePipeTJunction",
        _ => null
    };

    private static ThingSnapshot Snapshot(long id) =>
        new ThingSnapshot(id, "StructurePipeStraight", new Vec3(id, 0, 0), CubeRotation.Identity, 0, null);

    // A build pipe run that built 101 (new) and 102 (a tee in place of 60), and removed 55.
    private static RunJobView Run()
    {
        RunLogView log = new RunLogView();
        log.Removed.Add(new ThingId(55));
        log.Changed.Add(new UpgradeSwappedView(new ThingId(60), new ThingId(102), "StructurePipeStraight",
            "StructurePipeTJunction"));
        log.PlacedPieces.Add(new ThingView(new ThingId(101), "StructurePipeStraight", "Pipe"));
        log.AddCreated("run", new ThingId(101));
        log.AddCreated("run", new ThingId(102));
        return new RunJobView("run-7", "build pipe", "applied", null, new RunJobResultView(null, log, null, null));
    }

    private static JObject Wire(object view) => JObject.FromObject(view, ApiJson.Serializer);
}
