#nullable enable

using System.Threading.Tasks;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Layout;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Hub;
using StationGodMCP.Tests.Runs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// find room takes near (with radius): the rooms whose bounds come within radius of a point, nearest first; and a
/// script's inspect connections of one labelled device answers its label where a direct call does.
/// </summary>
public sealed class RoomNearTests
{
    private static readonly Vec3 Min = new(0, 236, 0);
    private static readonly Vec3 Max = new(8, 240, 6);

    [Fact]
    public void ARoomHoldsAPointInItsBoundsAndIsNearOneWithinRadius()
    {
        Assert.True(RoomNear.Keeps(Min, Max, new Vec3(4, 238, 3), 0));
        Assert.Equal(0, RoomNear.Distance(Min, Max, new Vec3(4, 238, 3)));
        Assert.False(RoomNear.Keeps(Min, Max, new Vec3(11, 238, 3), 0));
        Assert.Equal(3, RoomNear.Distance(Min, Max, new Vec3(11, 238, 3)), 6);
        Assert.True(RoomNear.Keeps(Min, Max, new Vec3(11, 238, 3), 3));
        Assert.Equal(5, RoomNear.Distance(Min, Max, new Vec3(11, 244, 3)), 6);
    }

    [Fact]
    public void FindRoomTakesNearAndRadius()
    {
        Assert.Contains("near", FindRoomOptions.Spec.OptionNames);
        Assert.Contains("radius", FindRoomOptions.Spec.OptionNames);
    }
}

/// <summary>A labelled device's name reaches a script as it reaches a direct call: thing.display_name.</summary>
[Collection(PythonCollection.Name)]
public sealed class LabelledConnectionsRunTests
{
    [PythonFact]
    public async Task AScriptReadsALabelledDevicesNameFromInspectConnections()
    {
        await using RunBench bench = new();
        bench.World.Override = call => RunWorld.KeyOf(call) == "inspect.connections"
            ? FakeGameLink.Ok("""{"thing":{"reference_id":"5","prefab_name":"StructureVolumePump","display_name":"Fuel Pump"},"ends":[],"count":0}""")
            : null;

        string text = await bench.RunAsync("print(sg.inspect('connections', id='5')['thing']['display_name'])");

        Assert.True(text.Contains("Fuel Pump"), text);
    }
}
