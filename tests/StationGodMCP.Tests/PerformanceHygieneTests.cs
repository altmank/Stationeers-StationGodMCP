#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// 1.9.1 performance phase 1: the request budget, the world epoch, the overrun atmosphere work, the cached names, the
/// shared serializer and the highlight frame helpers.
/// </summary>
public sealed class PerformanceHygieneTests
{
    // ---- H: request budget ----

    [Fact]
    public void BudgetDefaultIsFourMs()
    {
        Assert.Equal(4.0, FrameBudget.For(FrameBudget.DefaultMs, false).LimitMs);
    }

    [Fact]
    public void BudgetAlwaysServesTheFirstRequest()
    {
        FrameBudget budget = FrameBudget.For(4.0, false);

        Assert.True(budget.MayServeAnother(0, 100.0));
        Assert.True(budget.MayServeAnother(1, 3.9));
        Assert.False(budget.MayServeAnother(1, 4.0));
        Assert.False(budget.MayServeAnother(5, 12.0));
    }

    [Fact]
    public void ZeroBudgetIsUnlimited()
    {
        FrameBudget budget = FrameBudget.For(0.0, false);

        Assert.True(budget.Unlimited);
        Assert.True(budget.MayServeAnother(63, 1000.0));
    }

    [Theory]
    [InlineData(4.0, 2.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(0.0, 2.0)]
    public void JobHoldingTheTickCapsTheBudget(double configured, double expected)
    {
        Assert.Equal(expected, FrameBudget.For(configured, true).LimitMs);
    }

    [Theory]
    [InlineData(-1.0, null)]
    [InlineData(double.NaN, null)]
    [InlineData(0.0, 0.0)]
    [InlineData(8.0, 8.0)]
    public void ConfiguredBudgetRefusesNegative(double value, double? expected)
    {
        Assert.Equal(expected, FrameBudget.Configured(value));
    }

    // ---- J: world epoch ----

    [Fact]
    public void WorldScopeClearsOnceWhenAWorldIsLeft()
    {
        WorldScope scope = new WorldScope();
        int cleared = 0;
        scope.Register("store", () => cleared++);

        scope.Observe(false); // the menu at start: nothing to clear
        Assert.Equal(0, cleared);
        scope.Observe(true);
        scope.Observe(true);
        Assert.Equal(0, cleared);
        scope.Observe(false); // loading another save
        scope.Observe(false);
        Assert.Equal(1, cleared);
        Assert.Equal(1, scope.Epoch);
        scope.Observe(true);
        scope.Observe(false);
        Assert.Equal(2, cleared);
        Assert.Equal(2, scope.Epoch);
    }

    [Fact]
    public void WorldScopeClearsEveryStoreWhenOneFails()
    {
        WorldScope scope = new WorldScope();
        List<string> order = new List<string>();
        scope.Register("first", () => order.Add("first"));
        scope.Register("broken", () => throw new InvalidOperationException("boom"));
        scope.Register("last", () => order.Add("last"));
        scope.Observe(true);

        List<(string Name, Exception Failure)> failures = scope.Observe(false);

        Assert.Equal(new[] { "first", "last" }, order.ToArray());
        Assert.Single(failures);
        Assert.Equal("broken", failures[0].Name);
        Assert.Equal("boom", failures[0].Failure.Message);
    }

    [Fact]
    public void ClearedStoresForgetTheWorld()
    {
        PrintLog log = new PrintLog();
        log.Record(new PrintRecord(42, "ItemIronSheets", 7, "StructureAutolathe", "Autolathe", 10.0, 50, null));
        TransferLedger<string, string> ledger = new TransferLedger<string, string>(4, 4);
        long queued = ledger.TryEnqueue(static _ => "move")!.Value;
        Assert.True(ledger.TryBegin(out long applied, out _));
        ledger.Complete(applied, "done");
        long waiting = ledger.TryEnqueue(static _ => "later")!.Value;
        WorldScope scope = new WorldScope();
        scope.Register("print_log", log.Clear);
        scope.Register("gas_moves", ledger.Clear);
        scope.Observe(true);

        scope.Observe(false);

        Assert.Null(log.Of(42));
        Assert.IsType<TransferState<string>.Unknown>(ledger.Find(queued));
        Assert.IsType<TransferState<string>.Unknown>(ledger.Find(waiting));
        Assert.False(ledger.TryBegin(out _, out _));
        Assert.True(ledger.TryEnqueue(static _ => "next") > waiting);
    }

    // ---- K: overrun atmosphere work ----

    [Fact]
    public void WorkWithinTheLimitAnswers()
    {
        OutstandingWork work = new OutstandingWork(TimeSpan.FromSeconds(5));

        Assert.Equal(7, work.Run(static () => 7, "job-1"));
        Assert.False(work.Busy);
        Assert.IsType<WorkSettlement.Idle>(work.Settle());
    }

    [Fact]
    public void WorkFailureIsRethrownAsItWas()
    {
        OutstandingWork work = new OutstandingWork(TimeSpan.FromSeconds(5));

        Assert.Throws<ArgumentException>(() => work.Run<int>(static () => throw new ArgumentException("bad"), null));
        Assert.False(work.Busy);
    }

    [Fact]
    public void OverrunWorkIsKeptUntilItEnds()
    {
        OutstandingWork work = new OutstandingWork(TimeSpan.FromMilliseconds(50));
        using ManualResetEventSlim gate = new ManualResetEventSlim(false);

        Assert.Throws<TimeoutException>(() => work.Run(() => gate.Wait(TimeSpan.FromSeconds(30)), "run-3"));

        Assert.True(work.Busy);
        Assert.IsType<WorkSettlement.Running>(work.Settle());
        // Nothing new may start on what the overrun work is still changing.
        Assert.Throws<WorkStillRunningException>(() => work.Run(static () => 1, "run-4"));
        Assert.True(work.Busy);

        gate.Set();
        WorkSettlement.Ended ended = WaitForEnd(work);
        Assert.Equal("run-3", ended.Owner);
        Assert.Null(ended.Failure);
        Assert.False(work.Busy);
        Assert.IsType<WorkSettlement.Idle>(work.Settle());
        Assert.Equal(2, work.Run(static () => 2, "run-5"));
    }

    [Fact]
    public void OverrunWorkFailureIsHandedBack()
    {
        OutstandingWork work = new OutstandingWork(TimeSpan.FromMilliseconds(50));
        using ManualResetEventSlim gate = new ManualResetEventSlim(false);

        Assert.Throws<TimeoutException>(() => work.Run<int>(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            throw new InvalidOperationException("pipe gone");
        }, "upgrade-2"));
        gate.Set();

        WorkSettlement.Ended ended = WaitForEnd(work);
        Assert.Equal("upgrade-2", ended.Owner);
        Assert.IsType<InvalidOperationException>(ended.Failure);
        Assert.Equal("pipe gone", ended.Failure!.Message);
    }

    private static WorkSettlement.Ended WaitForEnd(OutstandingWork work)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            if (work.Settle() is WorkSettlement.Ended ended)
            {
                return ended;
            }

            Thread.Sleep(5);
        }

        throw new TimeoutException("The overrun work never ended.");
    }

    [Fact]
    public void UncheckedContentsHoldPipeJobs()
    {
        GasLoss loss = GasLoss.Unchecked("run-3", "Its atmosphere work ran past the limit.");

        Assert.Equal("job run-3 left its pipe networks' contents unchecked", loss.Describe());
        Assert.Equal(0.0, loss.MissingMol);
        Assert.Empty(loss.Networks);
        GasHoldVerdict held = GasHoldRule.Judge(loss, true, null);
        Assert.IsType<GasHoldVerdict.Held>(held);
        Assert.Contains("Its atmosphere work ran past the limit.", ((GasHoldVerdict.Held)held).Message);
        Assert.IsType<GasHoldVerdict.Lifting>(GasHoldRule.Judge(loss, true, "run-3"));
        Assert.IsType<GasHoldVerdict.Unaffected>(GasHoldRule.Judge(loss, false, null));
    }

    [Fact]
    public void CheckedLossStillDescribedAsBefore()
    {
        Assert.Equal("job run-4 lost 12.5 mol from pipe network(s) 101, 102",
            new GasLoss("run-4", new List<long> { 101, 102 }, 12.5, "s").Describe());
    }

    // ---- G: cached names and the shared serializer ----

    private enum Aliased : ushort
    {
        Zero = 0,
        One = 1,
        Uno = 1,
        Big = 40000
    }

    [Fact]
    public void CachedNamesEqualEnumGetName()
    {
        foreach (Aliased value in new[] { Aliased.Zero, Aliased.One, Aliased.Uno, Aliased.Big, (Aliased)7 })
        {
            Assert.Equal(Enum.GetName(typeof(Aliased), value), EnumNames<Aliased>.Of(value));
            Assert.Equal(Enum.GetName(typeof(Aliased), value), EnumNames<Aliased>.Of(value));
        }

        foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
        {
            Assert.Equal(Enum.GetName(typeof(DayOfWeek), day), EnumNames<DayOfWeek>.Of(day));
        }

        Assert.Null(EnumNames<Aliased>.Of((Aliased)7));
    }

    [Theory]
    [InlineData("<color=#ff0000>Oxygen</color>")]
    [InlineData("  Liquid Nitrogen ")]
    [InlineData("<N:EN:GasPollutant>")]
    [InlineData("")]
    public void RememberedPlainNamesEqualPlain(string raw)
    {
        Assert.Equal(Text.Plain(raw), Text.PlainName(raw));
        Assert.Equal(Text.Plain(raw), Text.PlainName(raw));
        Assert.Null(Text.PlainName(null));
    }

    public static IEnumerable<object[]> Replies()
    {
        yield return new object[] { CallReplyView.Of("7", new ChuteSummaryView(0), false, 1.25, 0.5, 9) };
        yield return new object[] { CallReplyView.Of(null, new { value = double.NaN, high = double.PositiveInfinity, low = double.NegativeInfinity, at = 0.1f }, false, 0.5, 0, 1) };
        yield return new object[] { CallReplyView.Refused("7", new ErrorView("game_timeout", "t \"quoted\" °C")) };
        yield return new object[] { CallReplyView.Of("x", new { id = new ThingId(123456789012), ids = new List<ThingId> { new ThingId(1), new ThingId(2) }, map = new Dictionary<string, double> { ["Oxygen"] = 1.79, ["CamelKey"] = 2 } }, false, 0.01, 0, 1) };
        yield return new object[] { new LogicTypeView(65535, null) };
        yield return new object[] { RuntimeWireTests.EmptyRuntime() };
    }

    [Theory]
    [MemberData(nameof(Replies))]
    public void SharedSerializerWritesWhatJsonConvertWrites(object reply)
    {
        string expected = JsonConvert.SerializeObject(reply, ApiJson.Settings);

        Assert.Equal(expected, ApiJson.WriteShared(reply));
        Assert.Equal(expected, ApiJson.WriteShared(reply));
        Assert.Equal(expected, ApiJson.WriteFresh(reply));
    }

    // ---- I: highlight frame helpers ----

    private sealed class Mark : IExpiring
    {
        internal Mark(string name, float until)
        {
            Name = name;
            Until = until;
        }

        internal string Name { get; }

        public float Until { get; }
    }

    [Fact]
    public void ExpiredMarksRemovedInPlaceKeepingOrder()
    {
        List<Mark> marks = new List<Mark>
        {
            new Mark("a", 5f), new Mark("b", 1f), new Mark("c", 9f), new Mark("d", 2f), new Mark("e", 3f)
        };

        Assert.Equal(3, Expiry.RemoveExpired(marks, 3f));
        Assert.Equal(new[] { "a", "c" }, marks.ConvertAll(static mark => mark.Name).ToArray());
        Assert.Equal(0, Expiry.RemoveExpired(marks, 4f));
        Assert.Equal(2, Expiry.RemoveExpired(marks, 100f));
        Assert.Empty(marks);
    }

    [Theory]
    [InlineData(1023, 0, 1023)]
    [InlineData(1024, 0, 1023)]
    [InlineData(1024, 1023, 1)]
    [InlineData(2500, 2046, 454)]
    [InlineData(5, 0, 5)]
    public void BatchesAreAtMostTheLimit(int total, int start, int expected)
    {
        Assert.Equal(expected, Expiry.BatchCount(total, start, 1023));
    }

    [Fact]
    public void FrameGroupsRefillWithoutLosingGroups()
    {
        FrameGroups<string, int, string> groups = new FrameGroups<string, int, string>();
        groups.Add("pipe", 1, "red");
        groups.Add("pipe", 2, "red");
        groups.Add("tank", 3, "blue");

        groups.BeginFrame();
        groups.Add("tank", 4, "green");

        Dictionary<string, int> counts = new Dictionary<string, int>();
        foreach (KeyValuePair<string, FrameGroups<string, int, string>.Group> entry in groups)
        {
            counts[entry.Key] = entry.Value.Count;
        }

        Assert.Equal(0, counts["pipe"]);
        Assert.Equal(1, counts["tank"]);
        Assert.Equal(2, groups.GroupCount);
        groups.Reset();
        Assert.Equal(0, groups.GroupCount);
    }
}
