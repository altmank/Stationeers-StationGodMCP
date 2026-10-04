#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Every builder (place_structure, the place, replace, upgrade and clean runs) takes its materials through one take:
/// exactly the quantity a build state needs, the last stack used split and the rest of it left where it was.
/// </summary>
public sealed class StockTakeTests
{
    // The take as ItemStock.Take walks the stacks, each giving StockTake.PartOf what is still owed.
    private static List<int> Take(IReadOnlyList<int> held, int quantity)
    {
        List<int> parts = new List<int>(held.Count);
        int owed = quantity;
        foreach (int stack in held)
        {
            int part = StockTake.PartOf(owed, stack);
            parts.Add(part);
            owed -= part;
        }

        return parts;
    }

    [Fact]
    public void TwoSteelSheetsComeOffAStackOfSixWhichKeepsFour()
    {
        // A Combustion Centrifuge's later states: 2 Steel Sheets from a stack of 6.
        List<int> parts = Take(new[] { 6 }, 2);

        Assert.Equal(new[] { 2 }, parts);
    }

    [Fact]
    public void OneCableCoilComesOffTheFirstStackAndTheOthersAreUntouched() =>
        Assert.Equal(new[] { 1, 0, 0 }, Take(new[] { 4, 3, 50 }, 1));

    [Fact]
    public void AShortFirstStackIsUsedUpAndTheRestComesFromTheNext() =>
        Assert.Equal(new[] { 1, 1, 0 }, Take(new[] { 1, 6, 6 }, 2));

    [Fact]
    public void NeverMoreThanAskedWhateverTheStacksHold()
    {
        int[] held = { 100, 100, 12, 100 };
        foreach (int quantity in new[] { 0, 1, 2, 99, 100, 101, 250, 312, 400 })
        {
            int total = 0;
            foreach (int part in Take(held, quantity))
            {
                total += part;
            }

            Assert.Equal(System.Math.Min(quantity, 312), total);
        }
    }

    [Fact]
    public void AnEmptyOrNegativeStackGivesNothing() =>
        Assert.Equal(new[] { 0, 0, 2 }, Take(new[] { 0, -1, 5 }, 2));

    [Theory]
    [InlineData(2, 6, 4, false)]
    [InlineData(1, 1, 0, false)]
    [InlineData(2, 6, 0, true)]
    [InlineData(1, 4, 2, true)]
    public void AStackThatLostMoreThanItsPartIsCaught(int part, int before, int after, bool tooMuch) =>
        Assert.Equal(tooMuch, StockTake.TookTooMuch(part, before, after));
}
