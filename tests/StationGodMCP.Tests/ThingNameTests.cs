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
}
