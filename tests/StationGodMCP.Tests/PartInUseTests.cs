#nullable enable

using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Parts a device is using (LU 2026-10-08: an agent's move_item took the running "Hangar Doors Chip" out of its IC
/// housing and the hangar door lever stopped working). A chip in a chip holder and a computer's motherboard are always
/// in use; a running machine's filter, battery or canister only while it is switched on. Refused as in_use, naming the
/// device, its label and the slot, unless allow_in_use; chargers, canister docks and carried gear are not guarded.
/// </summary>
public sealed class PartInUseTests
{
    private static readonly PartHolder HangarHousing =
        new("IC Housing", "Hangar Doors", 4755001, 0, switchedOn: true);

    [Fact]
    public void AChipInAnIcHousingIsRefusedNamingTheDeviceItsLabelAndTheSlot()
    {
        DevicePart part = PartInUseRule.PartOf(PartSlotClass.ProgrammableChip, PartHolderTraits.CircuitHolder);

        Assert.Equal(DevicePart.Chip, part);
        Assert.True(PartInUseRule.Refuses(part, switchedOn: true, allowInUse: false));
        string message = PartInUseRule.Refusal("Hangar Doors Chip", 4755685, part, HangarHousing);
        Assert.Contains("Hangar Doors Chip (4755685)", message);
        Assert.Contains("slot 0 of IC Housing \"Hangar Doors\" (4755001)", message);
        Assert.Contains("resets the chip", message);
        Assert.Contains("allow_in_use", message);
    }

    [Fact]
    public void AllowInUseLiftsTheRefusal()
    {
        Assert.False(PartInUseRule.Refuses(DevicePart.Chip, switchedOn: true, allowInUse: true));
        Assert.False(PartInUseRule.Refuses(DevicePart.Battery, switchedOn: true, allowInUse: true));
    }

    [Theory]
    [InlineData(DevicePart.Chip)]
    [InlineData(DevicePart.Motherboard)]
    public void ProgramPartsAreInUseEvenWhenTheDeviceIsOff(object part)
    {
        Assert.True(PartInUseRule.Refuses((DevicePart)part, switchedOn: false, allowInUse: false));
    }

    [Theory]
    [InlineData(DevicePart.Filter)]
    [InlineData(DevicePart.Battery)]
    [InlineData(DevicePart.Canister)]
    public void RunningPartsAreInUseOnlyWhileTheDeviceIsOn(object part)
    {
        Assert.True(PartInUseRule.Refuses((DevicePart)part, switchedOn: true, allowInUse: false));
        Assert.False(PartInUseRule.Refuses((DevicePart)part, switchedOn: false, allowInUse: false));
    }

    [Theory]
    [InlineData(PartSlotClass.ProgrammableChip, PartHolderTraits.CircuitHolder, DevicePart.Chip)]
    [InlineData(PartSlotClass.Motherboard, PartHolderTraits.Computer, DevicePart.Motherboard)]
    [InlineData(PartSlotClass.GasFilter, PartHolderTraits.FilterMachine, DevicePart.Filter)]
    [InlineData(PartSlotClass.Battery, PartHolderTraits.BatteryMachine, DevicePart.Battery)]
    [InlineData(PartSlotClass.Canister, PartHolderTraits.CanisterMachine, DevicePart.Canister)]
    [InlineData(PartSlotClass.ProgrammableChip, PartHolderTraits.CircuitHolder | PartHolderTraits.FilterMachine,
        DevicePart.Chip)]
    [InlineData(PartSlotClass.GasFilter, PartHolderTraits.CircuitHolder | PartHolderTraits.FilterMachine,
        DevicePart.Filter)]
    public void EachSlotClassIsAPartOnlyToTheHolderThatUsesIt(object slot, object holder, object expected)
    {
        Assert.Equal((DevicePart)expected, PartInUseRule.PartOf((PartSlotClass)slot, (PartHolderTraits)holder));
    }

    [Theory]
    [InlineData(PartSlotClass.Battery, PartHolderTraits.None)] // a battery charger hands its batteries back
    [InlineData(PartSlotClass.Canister, PartHolderTraits.None)] // a canister dock or storage
    [InlineData(PartSlotClass.GasFilter, PartHolderTraits.None)] // carried gear: a suit's filters
    [InlineData(PartSlotClass.ProgrammableChip, PartHolderTraits.Computer)] // not a chip holder
    [InlineData(PartSlotClass.Other, PartHolderTraits.CircuitHolder)] // a storage, import or export slot
    [InlineData(PartSlotClass.Battery, PartHolderTraits.CanisterMachine)]
    public void ASlotTheHolderDoesNotUseIsNeverGuarded(object slot, object holder)
    {
        DevicePart part = PartInUseRule.PartOf((PartSlotClass)slot, (PartHolderTraits)holder);

        Assert.Equal(DevicePart.None, part);
        Assert.False(PartInUseRule.Refuses(part, switchedOn: true, allowInUse: false));
    }

    [Fact]
    public void AnUnlabelledDeviceIsNamedByItsKindAndId()
    {
        string message = PartInUseRule.Refusal("Filter (Oxygen)", 12, DevicePart.Filter,
            new PartHolder("Filtration", null, 99, 1, switchedOn: true));

        Assert.Contains("slot 1 of Filtration 99", message);
        Assert.Contains("switched on", message);
    }

    [Fact]
    public void InUseIsRegisteredWithASeeThatToolInfoAnswers()
    {
        ErrorSee see = Assert.IsType<ErrorSee>(ErrorGuide.SeeOf("in_use"));
        Assert.Null(see.Tool);
        Assert.Equal("slots", see.Topic);
        Assert.Equal("in_use", see.Subtopic);

        JObject catalogue = JObject.Parse(File.ReadAllText(CatalogueFiles.AssembledPath));
        JToken? node = catalogue["help"]?["families"]?["slots"]?["subtopics"]?["in_use"];
        Assert.NotNull(node);
        Assert.Contains("allow_in_use", node!["text"]!.ToString());
    }

    [Theory]
    [InlineData("move_item")]
    [InlineData("silo_deposit")]
    public void TheOverrideIsDeclaredOnEveryToolThatTakesItemsOutOfSlots(string tool)
    {
        JObject catalogue = JObject.Parse(File.ReadAllText(CatalogueFiles.AssembledPath));
        JToken method = catalogue["methods"]!.First(entry => (string?)entry["name"] == tool);

        Assert.Equal("boolean", (string?)method["params"]!["properties"]!["allow_in_use"]!["type"]);
        if (tool == "move_item")
        {
            Assert.Equal("boolean",
                (string?)method["params"]!["properties"]!["moves"]!["items"]!["properties"]!["allow_in_use"]!["type"]);
        }
    }
}
