#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// trader_contacts, dish_aim and trader_inventory: the old StationApi.Traders shapes against the new views, with the
/// renames each test lists.
/// </summary>
public sealed class TradersWireTests
{
    private static readonly DishReadiness Ready = new DishReadiness(finished: true, powered: true, on: true);

    private static ContactIdentity Identity() =>
        new ContactIdentity(new ThingId(900), "TraderGas", "Gas Trader", "Small", new[] { 3, 3 });

    [Fact]
    public void TraderContactsSameWireAfterRenames()
    {
        var old = new
        {
            game_time = 100.5f,
            contacts = new List<object>
            {
                new
                {
                    reference_id = "900", trader = "TraderGas", display_name = "Gas Trader", shuttle = "Small",
                    pad_size = new[] { 3, 3 }, direction = new[] { 0f, 1f, 0f }, elevation_deg = 90f,
                    min_watts_to_resolve = 50f, min_watts_to_contact = 180f, contacted = false,
                    seconds_left = (float?)300f
                }
            },
            dishes = new List<object>
            {
                new
                {
                    reference_id = "910", display_name = "Dish", prefab_name = "StructureSatelliteDish",
                    forward = new[] { 0f, 1f, 0f }, transform_up = (float[]?)null, horizontal = 90.0, vertical = 45.0,
                    min_wattage = 50, max_wattage = 200, field_of_view_deg = 360f
                }
            }
        };
        TraderContactsView view = new TraderContactsView(100.5f,
            new List<TraderContactView>
            {
                new TraderContactView(Identity(), new[] { 0f, 1f, 0f }, 90f, new ContactPower(50f, 180f), false, 300f)
            },
            new List<DishView>
            {
                new DishView(new ThingView(new ThingId(910), "StructureSatelliteDish", "Dish"),
                    new DishPose(new[] { 0f, 1f, 0f }, null, 90.0, 45.0), Ready, 50, 200, 360f)
            });
        Dictionary<string, string> renames = new Dictionary<string, string>
        {
            ["contacts[].min_watts_to_resolve"] = "min_power_to_resolve_w",
            ["contacts[].min_watts_to_contact"] = "min_power_to_contact_w",
            ["contacts[].seconds_left"] = "time_left_s",
            ["dishes[].min_wattage"] = "min_power_w",
            ["dishes[].max_wattage"] = "max_power_w",
            ["contacts[].pad_size"] = "pad_size_tiles",
            ["game_time"] = "game_time_s"
        };
        WireCheck.SameAfterRenames(old, view, renames, "dishes[].finished", "dishes[].powered", "dishes[].on",
            "dishes[].can_rotate");
    }

    [Fact]
    public void AnUnpoweredDishSaysItCannotRotate()
    {
        DishView view = new DishView(new ThingView(new ThingId(911), "StructureSatelliteDishSmall", "Dish"),
            new DishPose(new[] { 0f, 1f, 0f }, null, 0.0, 0.0), new DishReadiness(finished: true, powered: false, on: true),
            5, 50, 360f);
        Assert.Contains("\"finished\":true,\"powered\":false,\"on\":true,\"can_rotate\":false,",
            WireCheck.New(view));
    }

    [Fact]
    public void AnUnfinishedDishCannotRotateEvenPoweredAndOn()
    {
        Assert.False(new DishReadiness(finished: false, powered: true, on: true).CanRotate);
        Assert.False(new DishReadiness(finished: true, powered: true, on: false).CanRotate);
        Assert.True(Ready.CanRotate);
    }

    [Fact]
    public void DishAimSameWire()
    {
        var old = new
        {
            dish_id = "910", contact_id = "900", horizontal = 123.4, vertical = 45.6, error_deg = 0.01f,
            pointing = new[] { 0f, 1f, 0f }, target = new[] { 0f, 1f, 0f },
            current = new
            {
                horizontal = 90.0, vertical = 45.0, forward = new[] { 1f, 0f, 0f }, error_deg = (float?)90f
            },
            stale_pose_deg = 0f, samples = 250
        };
        DishAimView view = new DishAimView(new ThingId(910), new ThingId(900), Ready,
            new DishAimResult(123.4, 45.6, 0.01f, new[] { 0f, 1f, 0f }, new[] { 0f, 1f, 0f }),
            new DishNowView(90.0, 45.0, new[] { 1f, 0f, 0f }, 90f), 0f, 250);
        WireCheck.SameAfterRenames(old, view, new Dictionary<string, string>(), "finished", "powered", "on",
            "can_rotate");
        Assert.Contains("\"contact_id\":\"900\",\"finished\":true,\"powered\":true,\"on\":true,\"can_rotate\":true,",
            WireCheck.New(view));
    }

    [Fact]
    public void TraderInventorySameWire()
    {
        var old = new
        {
            contacts = new List<object>
            {
                new
                {
                    reference_id = "900", trader = "TraderGas", display_name = "Gas Trader", shuttle = "Small",
                    pad_size = new[] { 3, 3 }, contacted = true,
                    buys = new List<object>
                    {
                        new
                        {
                            name = "Iron", prefab_name = "ItemIronIngot", credits_each = 2f, wanted = 100, gas = false,
                            conditions = new List<string?> { "Quantity 50" }, have = (double?)250.0,
                            sellable = (int?)null
                        }
                    },
                    sells = new List<object>
                    {
                        new
                        {
                            name = "Oxygen", prefab_name = (string?)null, credits_each = 1.5f, stock = 1000,
                            gas = true, details = "Pure"
                        }
                    }
                }
            }
        };
        TraderInventoryView view = new TraderInventoryView(new List<TraderStockView>
        {
            new TraderStockView(Identity(), true,
                new List<TraderBuysView>
                {
                    new TraderBuysView(new TradeItem("Iron", "ItemIronIngot", 2f, false), 100,
                        new List<string?> { "Quantity 50" }, 250.0, null)
                },
                new List<TraderSellsView>
                {
                    new TraderSellsView(new TradeItem("Oxygen", null, 1.5f, true), 1000, "Pure")
                })
        });
        Dictionary<string, string> renames = new Dictionary<string, string>
        {
            ["contacts[].pad_size"] = "pad_size_tiles"
        };
        WireCheck.SameAfterRenames(old, view, renames);
    }
}
