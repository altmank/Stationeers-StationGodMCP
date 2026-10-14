#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// undo_job's choice of facts: the job's kept final view (the request's facts ignored), else the request's facts while
/// the job's snapshots are kept, else job_not_found; and the request's lists, as a job_ended event carries them, plan
/// the same undo as the final view they came from.
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
    public void UndoOfAJobPastTheKeptOnesPlansFromTheRequestsFacts()
    {
        JObject given = Given(JobEndFacts.Of(Wire(Run())));

        UndoFactsSource.Given source = Assert.IsType<UndoFactsSource.Given>(
            UndoFactsSource.Choose(kept: null, snapshotsKept: true, given));
        UndoPlan plan = UndoPlanner.Plan(Facts(source.Facts, UndoFactsSource.Given.Status), Standing);

        Assert.True(plan.Ready, string.Join("; ", plan.Diverged));
        Assert.Equal(new long[] { 101, 102 }, plan.Remove.ToArray());
        Assert.Equal(new long[] { 55, 60 }, plan.Restore.ConvertAll(snapshot => snapshot.Id).ToArray());
    }

    [Fact]
    public void WithoutTheRequestsFactsAJobPastTheKeptOnesIsNotFound()
    {
        UndoFactsSource.NotFound source = Assert.IsType<UndoFactsSource.NotFound>(
            UndoFactsSource.Choose(kept: null, snapshotsKept: true, given: null));

        Assert.False(source.FactsGiven);
    }

    [Fact]
    public void WithTheSnapshotsGoneTooTheRequestsFactsAreNotEnough()
    {
        UndoFactsSource.NotFound source = Assert.IsType<UndoFactsSource.NotFound>(
            UndoFactsSource.Choose(kept: null, snapshotsKept: false, Given(JobEndFacts.Of(Wire(Run())))));

        Assert.True(source.FactsGiven);
    }

    [Fact]
    public void AKeptJobIsPlannedFromItsOwnFinalViewAndTheRequestsFactsAreIgnored()
    {
        JObject stale = JobEndFacts.FromRequest(new JArray("999"), null, null, null, out string? problem)!;
        Assert.Null(problem);

        UndoFactsSource.Kept source = Assert.IsType<UndoFactsSource.Kept>(
            UndoFactsSource.Choose(Wire(Run()), snapshotsKept: false, stale));

        Assert.Equal("build pipe", (string?)source.Facts["tool"]);
        Assert.Equal(new long[] { 101, 102 }, JobEndFacts.Created(source.Facts).ConvertAll(created => created.Id).ToArray());
    }

    [Fact]
    public void TheRequestsListsTellWhatTheFinalViewTells()
    {
        JObject run = JobEndFacts.Of(Wire(Run()));
        JObject structure = JobEndFacts.Of(Wire(StructureJob()));

        Assert.Equal(JobEndFacts.Created(run), JobEndFacts.Created(Given(run)));
        Assert.Equal(new (long, string?)[] { (101, "StructurePipeStraight"), (102, "StructurePipeTJunction") },
            JobEndFacts.Created(run).ToArray());
        Assert.Equal(new long[] { 55, 60 }, JobEndFacts.Removed(Given(run)).ToArray());
        Assert.Equal(new (long, string?)[] { (7, "StructureWallIron") }, JobEndFacts.Created(Given(structure)).ToArray());
        Assert.Equal(new long[] { 5 }, JobEndFacts.Removed(Given(structure)).ToArray());
    }

    [Fact]
    public void RemovedTakesTheThingsAStructureJobsResultNames()
    {
        JObject facts = JobEndFacts.FromRequest(null,
            new JArray("5", new JObject { ["reference_id"] = "6", ["prefab_name"] = "StructureFrame", ["index"] = 1 }),
            null, null, out string? problem)!;

        Assert.Null(problem);
        Assert.Equal(new long[] { 5, 6 }, JobEndFacts.Removed(facts).ToArray());
    }

    [Theory]
    [InlineData("""{"created_ids": "101"}""", "'created_ids' must be a list")]
    [InlineData("""{"created_ids": ["101", "pipe"]}""", "Every entry of 'created_ids' must be a reference id")]
    [InlineData("""{"removed": [true]}""", "Every entry of 'removed' must be a reference id or {reference_id}")]
    [InlineData("""{"changed": [{"old_reference_id": "60"}]}""", "Every entry of 'changed' must be {old_reference_id")]
    [InlineData("""{"placed": ["101"]}""", "Every entry of 'placed' must be {reference_id, prefab_name}")]
    public void ARequestListOfAnotherShapeIsAnInvalidArgument(string request, string expected)
    {
        JObject given = JObject.Parse(request);

        JObject? facts = JobEndFacts.FromRequest(given["created_ids"], given["removed"], given["changed"],
            given["placed"], out string? problem);

        Assert.Null(facts);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void NoListsIsNoFacts()
    {
        Assert.Null(JobEndFacts.FromRequest(null, JValue.CreateNull(), null, null, out string? problem));
        Assert.Null(problem);
    }

    // The request a hub sends: the event's lists as undo_job arguments, read back.
    private static JObject Given(JObject facts)
    {
        JObject given = JobEndFacts.FromRequest(facts[JobEndFacts.CreatedIdsKey], facts[JobEndFacts.RemovedKey],
            facts[JobEndFacts.ChangedKey], facts[JobEndFacts.PlacedKey], out string? problem)!;
        Assert.Null(problem);
        return given;
    }

    private static JobFacts Facts(JObject facts, string status) =>
        new JobFacts("run-7", "build pipe", status, JobEndFacts.Created(facts), JobEndFacts.Removed(facts), Snapshots);

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

    private static BuildJobView StructureJob()
    {
        BuildLog log = new BuildLog();
        log.Placed.Add(new BuiltPieceView(0, new ThingView(new ThingId(7), "StructureWallIron", "Wall")));
        log.Removed.Add(new BuiltPieceView(1, new ThingView(new ThingId(5), "StructureFrame", "Frame")));
        return new BuildJobView("place-3", "build structure", "applied", null,
            new BuildJobResult(null, log, new List<BuildCheckView>(), null));
    }

    private static JObject Wire(object view) => JObject.FromObject(view, ApiJson.Fresh());
}
