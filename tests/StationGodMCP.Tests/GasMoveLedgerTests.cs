#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// move_gas's queue bookkeeping (a known id is never "not found" while it is applied) and the room rule for arriving
/// liquid the room's air would lose.
/// </summary>
public sealed class GasMoveLedgerTests
{
    private static TransferLedger<string, string> Ledger() => new TransferLedger<string, string>(2, 2);

    [Fact]
    public void AMoveBeingAppliedIsStillWaiting()
    {
        TransferLedger<string, string> ledger = Ledger();
        long id = ledger.TryEnqueue(n => "move " + n)!.Value;
        Assert.IsType<TransferState<string>.Waiting>(ledger.Find(id));

        Assert.True(ledger.TryBegin(out long begun, out string? move));
        Assert.Equal(id, begun);
        Assert.Equal("move " + id, move);
        Assert.IsType<TransferState<string>.Waiting>(ledger.Find(id));

        ledger.Complete(id, "applied");
        TransferState<string>.Done done = Assert.IsType<TransferState<string>.Done>(ledger.Find(id));
        Assert.Equal("applied", done.Outcome);
    }

    [Fact]
    public void AFullQueueRefusesAndAnUnknownIdIsNotFound()
    {
        TransferLedger<string, string> ledger = Ledger();
        Assert.NotNull(ledger.TryEnqueue(n => "a"));
        Assert.NotNull(ledger.TryEnqueue(n => "b"));
        Assert.Null(ledger.TryEnqueue(n => "c"));
        Assert.IsType<TransferState<string>.Unknown>(ledger.Find(99));
    }

    [Fact]
    public void OnlyTheLastOutcomesAreKept()
    {
        TransferLedger<string, string> ledger = Ledger();
        List<long> ids = new List<long>();
        for (int round = 0; round < 3; round++)
        {
            ids.Add(ledger.TryEnqueue(n => "m")!.Value);
            Assert.True(ledger.TryBegin(out long id, out _));
            ledger.Complete(id, "applied " + id);
        }

        Assert.IsType<TransferState<string>.Unknown>(ledger.Find(ids[0]));
        Assert.IsType<TransferState<string>.Done>(ledger.Find(ids[1]));
        Assert.IsType<TransferState<string>.Done>(ledger.Find(ids[2]));
        Assert.False(ledger.TryBegin(out _, out _));
    }

    private static readonly LiquidArrival Water = new LiquidArrival("Water", 5.0, 6.3);

    [Fact]
    public void WaterIntoAThinRoomThatCannotBoilItIsLost()
    {
        // The live case: 5 mol water into a room of 6 mol O2 at 1.78 kPa.
        RoomAirAfter air = new RoomAirAfter(1.78, 285.6, 0.0, 0.0, false);
        string? loss = RoomLiquids.Loss(air, new[] { Water });
        Assert.NotNull(loss);
        Assert.Contains("Water", loss);
        Assert.Contains("6.3 kPa", loss);
    }

    [Fact]
    public void WaterIntoABreathableRoomStaysLiquid()
    {
        RoomAirAfter air = new RoomAirAfter(101.3, 293.0, 0.0, 0.0, false);
        Assert.Null(RoomLiquids.Loss(air, new[] { Water }));
    }

    [Fact]
    public void LiquidTheRoomBoilsAwayIsNotLost()
    {
        RoomAirAfter air = new RoomAirAfter(1.78, 400.0, 0.0, 0.0, true);
        Assert.Null(RoomLiquids.Loss(air, new[] { Water }));
    }

    [Fact]
    public void MoreFrozenThanBeforeIsLostWhateverThePressure()
    {
        RoomAirAfter colder = new RoomAirAfter(101.3, 260.0, 0.0, 3.0, false);
        Assert.Contains("would freeze", RoomLiquids.Loss(colder, new[] { Water }));
        RoomAirAfter unchanged = new RoomAirAfter(101.3, 260.0, 3.0, 3.0, false);
        Assert.Null(RoomLiquids.Loss(unchanged, new LiquidArrival[0]));
    }

    [Fact]
    public void GasAloneIntoAThinRoomIsFine()
    {
        RoomAirAfter air = new RoomAirAfter(1.78, 285.6, 0.0, 0.0, false);
        Assert.Null(RoomLiquids.Loss(air, new LiquidArrival[0]));
    }
}
