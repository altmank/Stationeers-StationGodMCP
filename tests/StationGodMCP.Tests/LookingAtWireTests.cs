#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>looking_at: the wire shape of LookingAtView.</summary>
public sealed class LookingAtWireTests
{
    [Fact]
    public void TargetAndInteractable()
    {
        var expected = new
        {
            player = new
            {
                reference_id = "151", display_name = "xceled", position = new { x = 700.9, y = 202.0, z = 679.5 }
            },
            target = new
            {
                reference_id = "86457", prefab_name = "StructureGasTankStorage", display_name = "Gas Tank Storage",
                custom_name = (string?)"Oxygen Filler", kind = "structure", runtime_type = "GasTankStorage",
                position = new { x = 701.0, y = 203.0, z = 681.0 }, distance_m = (double?)1.8, is_device = true,
                has_atmosphere = true, parent = (object?)null
            },
            interactable = new
            {
                action = "Slot1", display_name = (string?)"Slot1", contextual_name = (string?)"Gas Canister",
                state = 0,
                slot = new
                {
                    slot_index = 0, slot_name = (string?)"Gas Canister",
                    occupant = new
                    {
                        reference_id = "54807", prefab_name = "ItemGasCanisterEmpty", display_name = "Canister"
                    }
                }
            }
        };
        LookingAtView view = new LookingAtView(
            new LocalPlayerView(new ThingId(151), "xceled", new PositionView(700.9, 202.0, 679.5)),
            new LookingAtTargetView(new ThingView(new ThingId(86457), "StructureGasTankStorage", "Gas Tank Storage"),
                "Oxygen Filler", "structure", "GasTankStorage", new PositionView(701.0, 203.0, 681.0), 1.8, true,
                true, null),
            new LookingAtInteractableView("Slot1", "Slot1", "Gas Canister", 0,
                new InteractableSlotView(0, "Gas Canister",
                    new ThingView(new ThingId(54807), "ItemGasCanisterEmpty", "Canister"))));
        WireCheck.Same(expected, view);
    }

    [Fact]
    public void HeldItemAndNothing()
    {
        var held = new
        {
            player = (object?)null,
            target = new
            {
                reference_id = "4452", prefab_name = "ItemGasCanisterOxygen", display_name = "Canister",
                custom_name = (string?)null, kind = "item", runtime_type = "GasCanister",
                position = new { x = 1.0, y = 2.0, z = 3.0 }, distance_m = (double?)null, is_device = false,
                has_atmosphere = true,
                parent = new
                {
                    reference_id = "86457", prefab_name = "StructureGasTankStorage", display_name = "Gas Tank Storage",
                    slot_index = 0, slot_name = (string?)"Gas Canister"
                }
            },
            interactable = (object?)null
        };
        WireCheck.Same(held, new LookingAtView(null,
            new LookingAtTargetView(new ThingView(new ThingId(4452), "ItemGasCanisterOxygen", "Canister"), null,
                "item", "GasCanister", new PositionView(1.0, 2.0, 3.0), null, false, true,
                new HeldInView(new ThingView(new ThingId(86457), "StructureGasTankStorage", "Gas Tank Storage"), 0,
                    "Gas Canister")),
            null));

        var nothing = new { player = (object?)null, target = (object?)null, interactable = (object?)null };
        WireCheck.Same(nothing, new LookingAtView(null, null, null));
    }
}
