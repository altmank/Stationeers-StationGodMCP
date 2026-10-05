#nullable enable

using System.Collections.Generic;
using System.Linq;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// A build entry is paid by every held item of its prefab, not only by stacks: an Autolathe's last state takes an
/// Autolathe Printer Mod, a plain Item the game destroys whole when used (Structure.HandleToolUse).
/// </summary>
public sealed class HeldMaterialTests
{
    // As ItemStock adds up Available and ItemStock.Take walks the held items in order.
    private static int Available(IEnumerable<HeldMaterial> held) => held.Sum(material => material.Units);

    private static List<int> Take(IEnumerable<HeldMaterial> held, int quantity)
    {
        List<int> parts = new List<int>();
        int owed = quantity;
        foreach (HeldMaterial material in held)
        {
            int part = material.PartOf(owed);
            parts.Add(part);
            owed -= part;
        }

        return parts;
    }

    [Fact]
    public void APrinterModInALockerPaysTheAutolathesLastState()
    {
        // StructureAutolathe state 5 needs 1 AutolathePrinterMod; Device Kits 2 holds one in slot 8.
        HeldMaterial[] held = { HeldMaterial.Whole };

        Assert.Equal(1, Available(held));
        Assert.Equal(new[] { 1 }, Take(held, 1));
    }

    [Fact]
    public void AWholeItemPaysOneHoweverManyAreOwed() =>
        Assert.Equal(new[] { 1, 1 }, Take(new[] { HeldMaterial.Whole, HeldMaterial.Whole }, 3));

    [Fact]
    public void AWholeItemIsLeftWhenEarlierItemsPaidEverything() =>
        Assert.Equal(new[] { 1, 0 }, Take(new[] { HeldMaterial.Whole, HeldMaterial.Whole }, 1));

    [Fact]
    public void StacksStillPayTheirQuantity()
    {
        HeldMaterial[] held = { HeldMaterial.Stack(6), HeldMaterial.Stack(4) };

        Assert.Equal(10, Available(held));
        Assert.Equal(new[] { 6, 2 }, Take(held, 8));
    }
}
