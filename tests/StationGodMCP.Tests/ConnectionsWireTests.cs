#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>connections: the old StationApi.Connections shapes against the new views, from the same values.</summary>
public sealed class ConnectionsWireTests
{
    private static readonly PageRequest FirstPage =
        PageRequest.From(new Args(JObject.Parse("{\"limit\": 1}")), 200, 1000);

    [Fact]
    public void ThingEndsSameWire()
    {
        var old = new
        {
            thing = new { reference_id = "50", prefab_name = "StructurePipeStraight", display_name = "Pipe" },
            position = new { x = 1.0, y = 2.0, z = 3.0 },
            own_network = new { kind = "pipe", id = "900" },
            ends = new List<object>
            {
                new
                {
                    index = 0, type = "Pipe", type_name = "Pipe", role = "None", role_name = (string?)null,
                    position = new { x = 1.0, y = 2.0, z = 3.5 }, network = new { kind = "pipe", id = "900" },
                    connected = new List<object>
                    {
                        new { reference_id = "51", prefab_name = "StructurePipeStraight", display_name = "Pipe" }
                    }
                },
                new
                {
                    index = 1, type = "Pipe", type_name = "Pipe", role = "None", role_name = (string?)null,
                    position = (object?)null, network = (object?)null, connected = new List<object>()
                }
            },
            count = 2
        };
        NetworkRefView pipes = new NetworkRefView("pipe", new ThingId(900));
        ConnectionKind kind = new ConnectionKind("Pipe", "Pipe", "None", null);
        List<ConnectionEndView> ends = new List<ConnectionEndView>
        {
            new ConnectionEndView(0, kind, new PositionView(1.0, 2.0, 3.5), pipes,
                new List<ThingView> { new ThingView(new ThingId(51), "StructurePipeStraight", "Pipe") }),
            new ConnectionEndView(1, kind, null, null, new List<ThingView>())
        };
        ConnectionsView view = new ConnectionsView(
            new ThingView(new ThingId(50), "StructurePipeStraight", "Pipe"), new PositionView(1.0, 2.0, 3.0), pipes,
            ends);
        WireCheck.Same(old, view);
    }

    [Fact]
    public void CableNetworkSameWire()
    {
        var old = new
        {
            network = new { kind = "cable", id = "77" },
            summary = new
            {
                required_w = 1200f, potential_w = 900f, actual_w = 900f, shortfall_w = 300f,
                lowest_cable_max_w = (float?)5000f, lowest_fuse_break_w = (float?)null, overloaded = false,
                fuse_overloaded = false, cable_count = 12, fuse_count = 0
            },
            members = new List<object>
            {
                new
                {
                    reference_id = "80", prefab_name = "StructureCableStraight", display_name = "Cable",
                    member = "cable", position = new { x = 0.0, y = 0.0, z = 0.0 }
                }
            },
            count = 1, structure_count = 12, device_count = 3, offset = 0, limit = 1, total = 15, has_more = true
        };
        CableSummaryView summary = new CableSummaryView(new CableLoads(1200f, 900f, 900f, 300f), 5000f, null, 12, 0);
        NetworkMembersView view = new NetworkMembersView(
            new NetworkRefView("cable", new ThingId(77)), summary,
            Slice<NetworkMemberView>.Page(
                new List<NetworkMemberView>
                {
                    new NetworkMemberView(
                        new ThingView(new ThingId(80), "StructureCableStraight", "Cable"), "cable",
                        new PositionView(0.0, 0.0, 0.0))
                },
                FirstPage,
                15),
            12,
            3);
        WireCheck.Same(old, view);
    }

    [Fact]
    public void PipeAndChuteSummariesSameWire()
    {
        WireCheck.Same(
            new
            {
                content = "Gas", volume_l = 100.0, pressure_kpa = 101.3, temperature_k = 293.15, total_mol = 4.2,
                liquid_volume_l = 0.0,
                gases = new List<object> { new { gas = "Oxygen", state = "gas", amount_mol = 4.2 } }
            },
            new PipeSummaryView("Gas", 100.0, 101.3, 293.15, 4.2, 0.0,
                new List<NetworkGasView> { new NetworkGasView("Oxygen", "gas", 4.2) }));
        WireCheck.Same(new { member_count = 9 }, new ChuteSummaryView(9));
    }
}
