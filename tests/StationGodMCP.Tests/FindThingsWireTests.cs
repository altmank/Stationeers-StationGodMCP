#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>find_things: the wire shape of FindThingsView.</summary>
public sealed class FindThingsWireTests
{
    [Fact]
    public void LabelledTankOnTheGroundAndStructure()
    {
        var expected = new
        {
            things = new object[]
            {
                new
                {
                    reference_id = "91234", prefab_name = "DynamicMKIILiquidCanisterEmpty", display_name = "T1",
                    custom_name = (string?)"T1", game_name = "Portable Liquid Tank Mk II", kind = "dynamic",
                    runtime_type = "DynamicGasCanister", labelable = true, location = "ground",
                    carried_by = (string?)null, held_in = new object[0],
                    position = new { x = 701.0, y = 202.0, z = 680.0 }, distance_m = (double?)1.4, is_device = false,
                    has_atmosphere = true
                },
                new
                {
                    reference_id = "86457", prefab_name = "StructurePipeStraight", display_name = "Pipe (Straight)",
                    custom_name = (string?)null, game_name = "Pipe (Straight)", kind = "structure",
                    runtime_type = "Pipe", labelable = false, location = "built", carried_by = (string?)null,
                    held_in = new object[0], position = new { x = 703.0, y = 202.0, z = 681.0 },
                    distance_m = (double?)2.3, is_device = false, has_atmosphere = false
                }
            },
            count = 2, total = 7, offset = 0, limit = 2, has_more = true, scanned = 48210,
            local_player = new
            {
                reference_id = "151", display_name = "xceled", position = new { x = 700.9, y = 202.0, z = 679.5 }
            }
        };
        List<FoundThingView> things = new List<FoundThingView>
        {
            new FoundThingView(new ThingView(new ThingId(91234), "DynamicMKIILiquidCanisterEmpty", "T1"), "T1",
                "Portable Liquid Tank Mk II", "dynamic", "DynamicGasCanister", true, "ground", null,
                new List<HeldInView>(), new PositionView(701.0, 202.0, 680.0), 1.4, false, true),
            new FoundThingView(new ThingView(new ThingId(86457), "StructurePipeStraight", "Pipe (Straight)"), null,
                "Pipe (Straight)", "structure", "Pipe", false, FoundThingView.Built, null, new List<HeldInView>(),
                new PositionView(703.0, 202.0, 681.0), 2.3, false, false)
        };
        FindThingsView view = new FindThingsView(Slice<FoundThingView>.Page(things, PageFor(0, 2), 7), 48210,
            new LocalPlayerView(new ThingId(151), "xceled", new PositionView(700.9, 202.0, 679.5)));
        WireCheck.Same(expected, view);
    }

    [Fact]
    public void CarriedCanisterHasItsHolders()
    {
        var expected = new
        {
            things = new object[]
            {
                new
                {
                    reference_id = "4452", prefab_name = "ItemGasCanisterNitrogen", display_name = "N2 spare",
                    custom_name = (string?)"N2 spare", game_name = "Canister (Nitrogen)", kind = "item",
                    runtime_type = "GasCanister", labelable = false, location = "player",
                    carried_by = (string?)"xceled",
                    held_in = new object[]
                    {
                        new
                        {
                            reference_id = "151", prefab_name = "Character", display_name = "xceled", slot_index = 0,
                            slot_name = (string?)"Hand"
                        }
                    },
                    position = new { x = 1.0, y = 2.0, z = 3.0 }, distance_m = (double?)null, is_device = false,
                    has_atmosphere = true
                }
            },
            count = 1, total = 1, offset = 0, limit = 100, has_more = false, scanned = 10,
            local_player = (object?)null
        };
        List<FoundThingView> things = new List<FoundThingView>
        {
            new FoundThingView(new ThingView(new ThingId(4452), "ItemGasCanisterNitrogen", "N2 spare"), "N2 spare",
                "Canister (Nitrogen)", "item", "GasCanister", false, "player", "xceled",
                new List<HeldInView>
                {
                    new HeldInView(new ThingView(new ThingId(151), "Character", "xceled"), 0, "Hand")
                },
                new PositionView(1.0, 2.0, 3.0), null, false, true)
        };
        WireCheck.Same(expected,
            new FindThingsView(Slice<FoundThingView>.Page(things, PageFor(0, 100), 1), 10, null));
    }

    private static PageRequest PageFor(int offset, int limit) =>
        PageRequest.From(new Args(Newtonsoft.Json.Linq.JObject.FromObject(new { offset, limit })), 100, 500);
}
