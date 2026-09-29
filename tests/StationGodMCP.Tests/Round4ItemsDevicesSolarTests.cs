#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using StationGodMCP.Pure;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Fixes from round 4 of the headless live test (2026-09-29), items, devices and solar: a grower's hand interaction
/// decides its plant and fertiliser slots even when they are hidden (a planter, a station's fertiliser slots); a
/// numeric logic_type is judged as a number however large; exact-length arrays say "exactly"; label.labels needs one
/// entry; dish_aim measures its error in double.
/// </summary>
public sealed class Round4ItemsDevicesSolarTests
{
    [Theory]
    [InlineData("Plant", true, true)]
    [InlineData("Plant", false, false)]
    [InlineData("Fertiliser", false, true)]
    [InlineData("Fertiliser", true, true)]
    [InlineData("Other", true, false)]
    [InlineData("Other", false, false)]
    public void TheHandDecidesPlantsInPlantSlotsAndEverythingInFertiliserSlots(string kind, bool isPlant, bool decides)
    {
        Assert.Equal(decides, GrowerSlotRule.HandDecides(System.Enum.Parse<GrowerSlotKind>(kind), isPlant));
    }

    [Fact]
    public void ANonFertiliserInAHandDecidedFertiliserSlotIsStillRefused()
    {
        // HandDecides lets the grower rules speak before the hidden-slot rule; they refuse anything but fertiliser.
        Assert.True(GrowerSlotRule.HandDecides(GrowerSlotKind.Fertiliser, isPlant: true));
        Assert.Equal(GrowerRefusal.NotFertiliser,
            GrowerSlotRule.Into(GrowerSlotKind.Fertiliser, isFertiliser: false, occupied: false, quantity: 1));
    }

    [Theory]
    [InlineData(0d, true, (ushort)0)]
    [InlineData(65535d, true, (ushort)65535)]
    [InlineData(70d, true, (ushort)70)]
    [InlineData(65536d, false, (ushort)0)]
    [InlineData(-1d, false, (ushort)0)]
    [InlineData(12.5d, false, (ushort)0)]
    [InlineData(1e19, false, (ushort)0)]
    [InlineData(double.NaN, false, (ushort)0)]
    public void ANumericLogicTypeIsAWholeNumberFrom0To65535(double value, bool fits, ushort id)
    {
        Assert.Equal(fits, LogicTypeNumber.TryId(value, out ushort read));
        Assert.Equal(id, read);
    }

    [Fact]
    public void AnExactLengthArraySaysExactly()
    {
        Assert.Equal("Argument 'anchor' must be an array of exactly 3 entries; it has 2.",
            Assert.Single(Problems("paste_blueprint", """{"anchor":[1,2]}""")));
    }

    [Fact]
    public void AnEmptyLabelListIsRefusedByTheSidecar()
    {
        Assert.Equal("Argument 'labels' must be an array of 1 to 64 entries; it has 0.",
            Assert.Single(Problems("label", """{"labels":[]}""")));
    }

    [Fact]
    public void ASmallMissBetweenTwoDirectionsIsNotRoundedToZero()
    {
        // solar-18: dish 852's pointing and target, 0.0254 degrees apart; float Vector3.Angle read 0.
        double degrees = VectorAngle.Degrees(-0.6548339, 0.739106, -0.15784499, -0.655148, 0.7388074, -0.157939076);

        Assert.InRange(degrees, 0.024, 0.027);
    }

    [Theory]
    [InlineData(1d, 0d, 0d, 1d, 0d, 0d, 0d)]
    [InlineData(1d, 0d, 0d, 0d, 2d, 0d, 90d)]
    [InlineData(0d, 0d, 3d, 0d, 0d, -1d, 180d)]
    public void TheAngleBetweenDirectionsIgnoresTheirLength(double ax, double ay, double az, double bx, double by,
        double bz, double expected)
    {
        Assert.Equal(expected, VectorAngle.Degrees(ax, ay, az, bx, by, bz), 9);
    }

    private static IReadOnlyList<string> Problems(string tool, string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        return ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement);
    }
}
