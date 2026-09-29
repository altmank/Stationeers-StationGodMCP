using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The name a message shows: the game's placeholder for a missing localisation gives way to the prefab name.</summary>
public sealed class ThingNameTests
{
    [Fact]
    public void AMissingLocalisationShowsThePrefabName() =>
        Assert.Equal("StructureCrewUmbilicalDoor",
            ThingName.Shown("<N:EN:StructureCrewUmbilicalDoor>", "StructureCrewUmbilicalDoor"));

    [Fact]
    public void ALocalisedNameOrLabelIsKept() =>
        Assert.Equal("Station Battery", ThingName.Shown("Station Battery", "StructureBattery"));

    [Fact]
    public void AnEmptyNameShowsThePrefabName() =>
        Assert.Equal("StructureBattery", ThingName.Shown(string.Empty, "StructureBattery"));

    [Fact]
    public void ALabelOnlyLookingLikeThePlaceholderAtOneEndIsKept() =>
        Assert.Equal("<N:EN: my tank", ThingName.Shown("<N:EN: my tank", "StructureTankSmall"));

    [Fact]
    public void GameTextKeepsTheNameOfAnUnlocalisedBlocker() =>
        Assert.Equal("Placement is blocked by StructureCrewUmbilicalDoor.",
            Text.Plain("Placement is blocked by <color=red><N:EN:StructureCrewUmbilicalDoor></color>."));

    [Fact]
    public void GameTextStillLosesItsRichTextTags() =>
        Assert.Equal("Placement is blocked by Wall.", Text.Plain("<b>Placement is blocked by <color=red>Wall</color>.</b>"));

    [Fact]
    public void ADisplayNameFieldFallsBackToThePrefabName() =>
        Assert.Equal("StructureCrewUmbilicalDoor",
            new ThingView(new ThingId(275), "StructureCrewUmbilicalDoor", "<N:EN:StructureCrewUmbilicalDoor>")
                .DisplayName);

    [Fact]
    public void ADisplayNameFieldKeepsALocalisedName() =>
        Assert.Equal("Station Battery", ThingName.Displayed("Station Battery", "StructureBattery"));

    [Fact]
    public void ADisplayNameFieldStaysAbsentWhenTheGameHasNone() =>
        Assert.Null(ThingName.Displayed(null, "StructureBattery"));

    [Fact]
    public void APlaceholderInsideALongerNameGivesWayToItsKey() =>
        Assert.Equal("SeedBag_Potato 3", ThingName.Displayed("<N:EN:SeedBag_Potato> 3", "ItemPotato"));

    [Fact]
    public void APlaceholderWithNoPrefabNameGivesWayToItsKey() =>
        Assert.Equal("ContainedGas", ThingName.Displayed("<N:EN:ContainedGas>", null));
}
