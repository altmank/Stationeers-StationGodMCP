#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>build item's slot check: a prefab with no slot class is judged on the made item, as move item judges it.</summary>
public sealed class MadeItemSlotRuleTests
{
    [Fact]
    public void APrefabTheSlotTakesGoesIn()
    {
        Assert.Equal(MadeItemSlot.Takes, MadeItemSlotRule.Judge(prefabTaken: true, prefabHasClass: true, canEnter: true, draggable: false));
    }

    [Fact]
    public void APrefabWithNoClassIsCheckedOnTheMadeItem()
    {
        // ScriptedScreens' Circuitboard (Lua Chip): the prefab says None; a made board enters a Console's Circuitboard slot.
        Assert.Equal(MadeItemSlot.CheckMade,
            MadeItemSlotRule.Judge(prefabTaken: false, prefabHasClass: false, canEnter: true, draggable: false));
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void AnyOtherRefusalStands(bool prefabHasClass, bool canEnter, bool draggable)
    {
        Assert.Equal(MadeItemSlot.Refuses, MadeItemSlotRule.Judge(prefabTaken: false, prefabHasClass, canEnter, draggable));
    }
}
