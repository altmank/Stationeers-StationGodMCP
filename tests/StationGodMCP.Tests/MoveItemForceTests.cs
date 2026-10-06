#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Server;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;
using ModCatalogue = StationGodMCP.Pure.Catalogue.Catalogue;

namespace StationGodMCP.Tests;

/// <summary>
/// move_item force (1.28.1): the cheat puts a whole item into an empty hidden slot whose class takes it, as
/// RocketPayload.AttackWith mounts a payload in a payload bay's hidden slot with OnServer.MoveToSlot.
/// </summary>
public sealed class MoveItemForceTests
{
    private static readonly ModCatalogue Catalogue = ModCatalogue.Load(File.ReadAllText(CatalogueFiles.AssembledPath));

    [Fact]
    public void AnEmptySlotWhoseClassTakesTheWholeItemIsFilled()
    {
        // A rocket payload into a payload bay's hidden RocketPayload slot: hidden and draggable do not count here.
        Assert.Equal(ForcedRefusal.None,
            ForcedSlotRule.Into(heldByStack: false, occupied: false, wholeItem: true, canEnter: true, classFits: true));
    }

    [Fact]
    public void AnOccupiedSlotIsNeverFilled()
    {
        Assert.Equal(ForcedRefusal.Occupied,
            ForcedSlotRule.Into(heldByStack: false, occupied: true, wholeItem: true, canEnter: true, classFits: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TheSlotClassAndTheItemsOwnRuleStillApply(bool canEnter, bool classFits)
    {
        Assert.Equal(ForcedRefusal.Refuses,
            ForcedSlotRule.Into(heldByStack: false, occupied: false, wholeItem: true, canEnter, classFits));
    }

    [Fact]
    public void AStacksOwnSlotStaysRefused()
    {
        // A cable coil's slot: whatever is put there goes with the stack.
        Assert.Equal(ForcedRefusal.StackSlot,
            ForcedSlotRule.Into(heldByStack: true, occupied: false, wholeItem: true, canEnter: true, classFits: true));
    }

    [Fact]
    public void PartOfAStackIsRefused()
    {
        Assert.Equal(ForcedRefusal.PartOfStack,
            ForcedSlotRule.Into(heldByStack: false, occupied: false, wholeItem: false, canEnter: true, classFits: true));
    }

    [Fact]
    public void AHiddenSlotRefusalNamesForceAsACheat()
    {
        Assert.Contains("force true (a cheat", ForcedSlotRule.HiddenSlotHint);
    }

    [Fact]
    public void ForceIsAnArgumentOfOneMove()
    {
        Assert.Empty(Problems("""{"reference_id":"3698314","to_id":"3622964","to_slot":0,"force":true}"""));
        Assert.Single(Problems("""{"reference_id":"1","to_id":"2","to_slot":0,"force":"yes"}"""));
    }

    [Fact]
    public void ForceIsNotAnArgumentOfAMovesEntry()
    {
        string problem = Assert.Single(Problems("""{"moves":[{"reference_id":"1","to_id":"2","to_slot":0,"force":true}]}"""));

        Assert.StartsWith("Unknown argument 'moves[0].force'", problem);
    }

    [Fact]
    public void ForceMakesTheCallACheat()
    {
        Assert.True(Catalogue.TryGet("move_item", out CatalogueMethod? move));
        Assert.Equal(MethodClass.Cheat, move!.ClassAt(JObject.Parse("""{"to_slot":0,"force":true}""")));
        Assert.Equal(MethodClass.Write, move.ClassAt(JObject.Parse("""{"to_slot":0,"force":false}""")));
        Assert.Equal(MethodClass.Write, move.ClassAt(JObject.Parse("""{"to_slot":0}""")));
    }

    private static IReadOnlyList<string> Problems(string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        return ArgumentCheck.Problems(Program.InputSchemas["move_item"], document.RootElement);
    }
}
