#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>connections' thing form: the old StationApi.Connections shapes against the new views, from the same values.</summary>
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
}
