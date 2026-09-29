#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>landing_pads: the old StationApi.LandingPads shape against the new views; contact_id renamed.</summary>
public sealed class LandingPadsWireTests
{
    private static readonly Dictionary<string, string> Renames = new Dictionary<string, string>
    {
        ["pads[].contacts[].contact_id"] = "reference_id",
        ["pads[].contacts[].pad_size"] = "pad_size_tiles",
        ["pads[].fits_by_ship[].pad_size"] = "pad_size_tiles",
        ["pads[].fits_by_ship[].runway"] = "runway_tiles",
        ["pads[].largest_square"] = "largest_square_tiles",
        ["pads[].network"] = "piece_count"
    };

    [Fact]
    public void PadsSameWireAfterRenames()
    {
        var old = new
        {
            pads = new List<object>
            {
                new
                {
                    reference_id = "500", prefab_name = "LandingPadCenter", display_name = "Landing Pad Center",
                    position = new { x = 10.0, y = 1.0, z = -4.0 }, forward = "+z", is_network_center = true,
                    network = 25, extent = new { x_tiles = 5, z_tiles = 5 }, largest_square = 5,
                    runway_ok = (bool?)false,
                    fits_by_ship = new List<object>
                    {
                        new
                        {
                            shuttle_type = "Small", pad_size = new[] { 3, 3 }, runway = 0, needs_threshold = false,
                            fits = true
                        }
                    },
                    contacts = new List<object>
                    {
                        new
                        {
                            contact_id = "900", name = "Trader", shuttle_type = "Small", pad_size = new[] { 3, 3 },
                            fits = true, can_land = false, reason = "Power"
                        }
                    }
                }
            },
            count = 1
        };
        LandingPadView pad = new LandingPadView(
            new ThingView(new ThingId(500), "LandingPadCenter", "Landing Pad Center"),
            new PositionView(10.0, 1.0, -4.0),
            new PadNetworkFacts(true, 25, new ExtentView(5, 5), false),
            new PadMeasure("+z", 5),
            new List<ShipFitView> { new ShipFitView("Small", new[] { 3, 3 }, 0, false, true) },
            new List<ContactFitView>
            {
                new ContactFitView(new ThingId(900), "Trader", "Small", new[] { 3, 3 },
                    new PadVerdict(true, false, "Power"))
            });
        WireCheck.SameAfterRenames(old, new LandingPadsView(new List<LandingPadView> { pad }), Renames);
    }

    [Fact]
    public void PadWithoutNetworkSameWire()
    {
        var old = new
        {
            reference_id = "501", prefab_name = "LandingPadCenter", display_name = "Pad",
            position = new { x = 0.0, y = 0.0, z = 0.0 }, forward = "-x", is_network_center = false, network = 0,
            extent = (object?)null, largest_square = 0, runway_ok = (bool?)null, fits_by_ship = new List<object>(),
            contacts = new List<object>()
        };
        LandingPadView pad = new LandingPadView(new ThingView(new ThingId(501), "LandingPadCenter", "Pad"),
            new PositionView(0.0, 0.0, 0.0), new PadNetworkFacts(false, 0, null, null), new PadMeasure("-x", 0),
            new List<ShipFitView>(), new List<ContactFitView>());
        Dictionary<string, string> renames = new Dictionary<string, string>
        {
            ["largest_square"] = "largest_square_tiles",
            ["network"] = "piece_count"
        };
        WireCheck.SameAfterRenames(old, pad, renames);
    }
}
