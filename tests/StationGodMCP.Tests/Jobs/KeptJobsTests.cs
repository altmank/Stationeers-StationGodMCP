#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Jobs;
using StationGodMCP.Pure.Protocol;
using Xunit;

namespace StationGodMCP.Tests.Jobs;

/// <summary>
/// The kept jobs (KeptJobs): job snapshots and final statuses bounded by memory, not by count; eviction oldest first,
/// passing over pinned jobs until pinned jobs alone pass the budget; the newest few always kept; a job pinned to the
/// (owner, stream) that started it, released by release, an owner's end, a session end or a world change, never by
/// stream_end; and the bounded record of evicted ids that tells undo_expired from job_not_found.
/// </summary>
public sealed class KeptJobsTests
{
    private const long Entry = 1000;

    private static readonly CallContext Run = new CallContext("hub", 7);
    private static readonly CallContext Direct = new CallContext("hub", 0);

    [Fact]
    public void APinnedJobsSnapshotAndFinalStatusOutlastFortyLaterJobs()
    {
        KeptJobs kept = Small(entries: 10);
        Start(kept, "place-1", Run);
        for (int index = 2; index <= 41; index++)
        {
            Start(kept, "place-" + index, CallContext.None);
        }

        Assert.NotNull(kept.Snapshots.Of("place-1"));
        Assert.NotNull(kept.Finals.Of("place-1"));
        Assert.False(kept.Snapshots.Contains("place-2"));
        Assert.True(kept.Snapshots.WasEvicted("place-2"));
        Assert.IsType<UndoFactsSource.Kept>(Choose(kept, "place-1"));
    }

    [Fact]
    public void AReleasedJobIsEvictedUnderMemoryPressureAndItsUndoIsExpiredNamingIt()
    {
        KeptJobs kept = Small(entries: 10);
        Start(kept, "place-1", Run);
        kept.Release("hub", 7);
        for (int index = 2; index <= 12; index++)
        {
            Start(kept, "place-" + index, CallContext.None);
        }

        Assert.False(kept.Snapshots.Contains("place-1"));
        UndoFactsSource.Expired expired = Assert.IsType<UndoFactsSource.Expired>(Choose(kept, "place-1"));
        Assert.Equal("place-1", expired.JobId);
        Assert.Contains("place-1", expired.Message);
    }

    [Fact]
    public void AnIdTheModNeverHadIsNotFoundNotExpired()
    {
        KeptJobs kept = Small(entries: 10);

        Assert.IsType<UndoFactsSource.NotFound>(Choose(kept, "place-99"));
    }

    [Fact]
    public void AStreamEndKeepsPinsAndOnlyReleaseOfThatStreamDropsThem()
    {
        KeptJobs kept = Small(entries: 10);
        kept.Pin("place-1", Run);
        kept.Pin("place-2", Direct);

        kept.Ended(StreamEnd.Of("hub", 7));
        Assert.True(kept.Pins.IsPinned("place-1"));

        kept.Release("hub", 8);
        kept.Release("other", 7);
        Assert.True(kept.Pins.IsPinned("place-1"));

        kept.Release("hub", 7);
        Assert.False(kept.Pins.IsPinned("place-1"));
        Assert.True(kept.Pins.IsPinned("place-2"));
    }

    [Fact]
    public void ReleaseWorksOnStreamZeroToo()
    {
        KeptJobs kept = Small(entries: 10);
        kept.Pin("place-2", Direct);

        kept.Release("hub", 0);

        Assert.False(kept.Pins.IsPinned("place-2"));
    }

    [Fact]
    public void AnOwnersEndReleasesEveryPinOfItAndNoOther()
    {
        KeptJobs kept = Small(entries: 10);
        kept.Pin("place-1", Run);
        kept.Pin("place-2", Direct);
        kept.Pin("place-3", new CallContext("c9", 1));

        kept.Ended(StreamEnd.AllOf("hub"));

        Assert.False(kept.Pins.IsPinned("place-1"));
        Assert.False(kept.Pins.IsPinned("place-2"));
        Assert.True(kept.Pins.IsPinned("place-3"));
    }

    [Fact]
    public void ASessionEndReleasesTheOwnersPins()
    {
        KeptJobs kept = Small(entries: 10);
        kept.Pin("place-1", Run);
        kept.Pin("place-3", new CallContext("c9", 1));

        kept.SessionEnded("hub");

        Assert.False(kept.Pins.IsPinned("place-1"));
        Assert.True(kept.Pins.IsPinned("place-3"));
    }

    [Fact]
    public void AConnectionsCloseUnderItsClientIdLeavesItsResumeTokensPins()
    {
        // A resumed connection's owner is the token's key; its close ends its client id's state only (CallScheduler).
        KeptJobs kept = Small(entries: 10);
        kept.Pin("place-1", new CallContext("token-key", 3));

        kept.Ended(StreamEnd.AllOf("c4"));

        Assert.True(kept.Pins.IsPinned("place-1"));
    }

    [Fact]
    public void AWorldChangeDropsEveryPin()
    {
        KeptJobs kept = Small(entries: 10);
        kept.Pin("place-1", Run);

        kept.Ended(StreamEnd.World);

        Assert.Equal(0, kept.Pins.Count);
    }

    [Fact]
    public void EvictionGoesOldestFirstPassingOverPinnedJobs()
    {
        List<string> evicted = new List<string>();
        JobPins pins = new JobPins();
        KeptJobStore<string> store = new KeptJobStore<string>(4 * Entry, pins, newestKept: 1);
        store.Evicted += evicted.Add;
        pins.Pin("a", Run);
        store.Put("a", "a", Entry);
        store.Put("b", "b", Entry);
        pins.Pin("c", Run);
        store.Put("c", "c", Entry);
        store.Put("d", "d", Entry);

        store.Put("e", "e", Entry);
        store.Put("f", "f", Entry);

        Assert.Equal(new[] { "b", "d" }, evicted);
        Assert.True(store.Contains("a"));
        Assert.True(store.Contains("c"));
        Assert.Equal(4 * Entry, store.Bytes);
        Assert.Equal(2, store.PinnedCount());
    }

    [Fact]
    public void WhenPinnedJobsAlonePassTheBudgetTheOldestPinnedGoes()
    {
        List<string> evicted = new List<string>();
        JobPins pins = new JobPins();
        KeptJobStore<string> store = new KeptJobStore<string>(3 * Entry, pins, newestKept: 1);
        store.Evicted += evicted.Add;
        foreach (string id in new[] { "a", "b", "c", "d" })
        {
            pins.Pin(id, Run);
            store.Put(id, id, Entry);
        }

        Assert.Equal(new[] { "a" }, evicted);
        Assert.Equal(3 * Entry, store.Bytes);
    }

    [Fact]
    public void UnpinnedJobsGoBeforeAnyPinnedOneEvenWhenTheyAreNewer()
    {
        List<string> evicted = new List<string>();
        JobPins pins = new JobPins();
        KeptJobStore<string> store = new KeptJobStore<string>(3 * Entry, pins, newestKept: 1);
        store.Evicted += evicted.Add;
        pins.Pin("a", Run);
        store.Put("a", "a", Entry);
        pins.Pin("b", Run);
        store.Put("b", "b", Entry);
        store.Put("c", "c", Entry);

        store.Put("d", "d", 2 * Entry);

        Assert.Equal(new[] { "c", "a" }, evicted);
    }

    [Fact]
    public void TheNewestJobsStayWhateverTheirSizeSoOneHugePinnedJobDoesNotEvictARecentOne()
    {
        JobPins pins = new JobPins();
        KeptJobStore<string> store = new KeptJobStore<string>(10 * Entry, pins, newestKept: 2);
        store.Put("old", "old", Entry);
        store.Put("recent", "recent", Entry);
        pins.Pin("huge", Run);

        store.Put("huge", "huge", 50 * Entry);

        Assert.False(store.Contains("old"));
        Assert.True(store.Contains("recent"));
        Assert.True(store.Contains("huge"));
        Assert.Equal(51 * Entry, store.Bytes);

        store.Put("next", "next", Entry);
        Assert.False(store.Contains("recent"));
        Assert.True(store.Contains("huge"));
        Assert.True(store.Contains("next"));
    }

    [Fact]
    public void StoringAJobAgainReplacesItsBytesInPlace()
    {
        KeptJobStore<string> store = new KeptJobStore<string>(10 * Entry, new JobPins());
        store.Put("a", "first", Entry);
        store.Put("b", "b", Entry);

        store.Put("a", "second", 3 * Entry);

        Assert.Equal("second", store.Of("a"));
        Assert.Equal(2, store.Count);
        Assert.Equal(4 * Entry, store.Bytes);
    }

    [Fact]
    public void TheEvictedRecordIsBoundedAndAnIdOlderThanItIsNotFound()
    {
        KeptJobStore<string> store = new KeptJobStore<string>(Entry, new JobPins(), newestKept: 1);
        store.Put("first", "first", Entry);
        store.Put("second", "second", Entry);
        Assert.True(store.WasEvicted("first"));

        for (int index = 0; index < KeptJobStore<string>.EvictedKept; index++)
        {
            store.Put("filler-" + index, "x", Entry);
        }

        Assert.False(store.WasEvicted("first"));
        Assert.True(store.WasEvicted("filler-0"));
    }

    [Fact]
    public void ClearingForgetsJobsBytesAndTheEvictedRecord()
    {
        KeptJobStore<string> store = new KeptJobStore<string>(Entry, new JobPins(), newestKept: 1);
        store.Put("first", "first", Entry);
        store.Put("second", "second", Entry);

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.Bytes);
        Assert.False(store.WasEvicted("first"));
    }

    [Fact]
    public void APinIsForgottenOnceItsJobHasLeftBothStores()
    {
        KeptJobs kept = Small(entries: 2, newestKept: 1);
        Start(kept, "place-1", Run);
        kept.Pin("place-2", Run);
        kept.Pin("place-3", Run);
        kept.Snapshots.Put("place-2", Job(), Entry);
        kept.Snapshots.Put("place-3", Job(), Entry);
        Assert.False(kept.Snapshots.Contains("place-1"));
        Assert.True(kept.Pins.IsPinned("place-1"));

        kept.Finals.Put("place-2", new object(), Entry);
        kept.Finals.Put("place-3", new object(), Entry);

        Assert.False(kept.Finals.Contains("place-1"));
        Assert.False(kept.Pins.IsPinned("place-1"));
        Assert.Equal(2, kept.Pins.Count);
    }

    [Fact]
    public void AFinalStatusEvictedIsExpiredEvenWhileItsSnapshotsAreKept()
    {
        KeptJobs kept = Small(entries: 10);
        kept.Snapshots.Put("place-1", Job(), Entry);
        kept.Finals.Put("place-1", new object(), Entry);
        for (int index = 2; index <= 12; index++)
        {
            kept.Finals.Put("place-" + index, new object(), Entry);
        }

        UndoFactsSource.Expired expired = Assert.IsType<UndoFactsSource.Expired>(Choose(kept, "place-1"));
        Assert.Equal("place-1", expired.JobId);
    }

    [Fact]
    public void ASnapshotEvictedMakesUndoExpiredEvenWhileItsFinalStatusIsKept()
    {
        UndoFactsSource source = UndoFactsSource.Choose(new JObject { ["job_id"] = "place-1" },
            snapshotEvicted: true, finalEvicted: false, jobId: "place-1");

        Assert.IsType<UndoFactsSource.Expired>(source);
    }

    [Fact]
    public void TheSnapshotSizeGrowsWithItsThingsLabelsAndPieceCells()
    {
        long none = KeptJobSizes.Snapshot(Job());
        long one = KeptJobSizes.Snapshot(Job(Thing(1, null, 0)));
        long labelled = KeptJobSizes.Snapshot(Job(Thing(1, "Airlock", 0)));
        long piece = KeptJobSizes.Snapshot(Job(Thing(1, null, 3)));
        long two = KeptJobSizes.Snapshot(Job(Thing(1, null, 0), Thing(2, null, 0)));

        Assert.Equal(KeptJobSizes.JobBytes, none);
        Assert.Equal(none + KeptJobSizes.ThingBytes, one);
        Assert.Equal(one + KeptJobSizes.TextBytes + 2 * "Airlock".Length, labelled);
        Assert.Equal(one + KeptJobSizes.PieceBytes + 3 * KeptJobSizes.CellBytes, piece);
        Assert.Equal(one + KeptJobSizes.ThingBytes, two);
    }

    [Fact]
    public void TheFinalStatusSizeIsTwoBytesAPolledCharacterPlusTheEntry()
    {
        Assert.Equal(KeptJobSizes.FinalBytes, KeptJobSizes.Final(0));
        Assert.Equal(KeptJobSizes.FinalBytes + 2000, KeptJobSizes.Final(1000));
    }

    [Fact]
    public void AFinalViewIsMeasuredAsPollsWriteIt()
    {
        JobDroppedView view = new JobDroppedView("upgrade-4", "swap cable", JobDrops.Dropped());

        Assert.Equal(ApiJson.WriteShared(view).Length, ApiJson.Length(view));
        Assert.Equal(4, ApiJson.Length(null));
    }

    [Fact]
    public void TheDefaultBudgetsAreThirtyTwoAndFourMegabytes()
    {
        KeptJobs kept = new KeptJobs();

        Assert.Equal(32L * 1024 * 1024, kept.Snapshots.BudgetBytes);
        Assert.Equal(4L * 1024 * 1024, kept.Finals.BudgetBytes);
        Assert.Equal(KeptJobStore<object>.DefaultNewestKept, kept.Finals.NewestKept);
    }

    private static KeptJobs Small(int entries, int newestKept = 2) =>
        new KeptJobs(entries * Entry, entries * Entry, newestKept);

    // A job as HeldTickJobs and JobSnapshots keep it: pinned to its caller when it starts, its snapshot, its end.
    private static void Start(KeptJobs kept, string id, CallContext caller)
    {
        kept.Pin(id, caller);
        kept.Snapshots.Put(id, Job(), Entry);
        kept.Finals.Put(id, new JObject { ["job_id"] = id, ["status"] = "applied" }, Entry);
    }

    private static UndoFactsSource Choose(KeptJobs kept, string id) =>
        UndoFactsSource.Choose(kept.Finals.Of(id) as JObject, kept.Snapshots.WasEvicted(id),
            kept.Finals.WasEvicted(id), id);

    private static RecordedJob Job(params ThingSnapshot[] removed)
    {
        Dictionary<long, ThingSnapshot> snapshots = new Dictionary<long, ThingSnapshot>();
        foreach (ThingSnapshot snapshot in removed)
        {
            snapshots[snapshot.Id] = snapshot;
        }

        return new RecordedJob("remove structure", snapshots, JobSource.Unknown);
    }

    private static ThingSnapshot Thing(long id, string? label, int cells)
    {
        NetworkPiece? piece = null;
        if (cells > 0)
        {
            List<PieceCell> parts = new List<PieceCell>();
            for (int index = 0; index < cells; index++)
            {
                parts.Add(new PieceCell(new GridCell(index, 0, 0), default));
            }

            piece = new NetworkPiece("cable", "heavy", parts);
        }

        return new ThingSnapshot(id, "StructureCableStraight", new Vec3(0, 0, 0), CubeRotation.All[0], 0, label, piece);
    }
}
