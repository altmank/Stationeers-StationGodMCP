#nullable enable

using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StationGodMCP.Api.Shared;
using StationGodMCP.Tests.Hub;
using StationGodMCP.Tests.Runs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// One reply shape on both faces: a script's call of a per-id what with one id answers the one-id shape a direct call
/// gets (inspect atmosphere, inspect connections, read logic), a list the rows; survey area with sections that leave
/// cells out reads every cell, so its pieces never depend on a first page of cells the caller cannot see.
/// </summary>
[Collection(PythonCollection.Name)]
public sealed class ScriptReplyShapeTests
{
    [PythonFact]
    public async Task AScriptGetsTheDirectShapeForOneIdAndRowsForAList()
    {
        await using RunBench bench = new();
        bench.World.Override = call =>
        {
            JsonNode? id = JsonNode.Parse(call.Parameters.GetRawText())!["id"];
            string key = RunWorld.KeyOf(call);
            if (key is not ("inspect.atmosphere" or "inspect.connections" or "read.logic"))
            {
                return null;
            }

            return id is JsonArray
                ? FakeGameLink.Ok("""{"results":[{"index":0,"ok":true,"reference_id":"5","payload":{"pressure_kpa":101.3}}],"count":1,"success_count":1,"error_count":0}""")
                : key == "read.logic"
                    ? FakeGameLink.Ok("""{"reference_id":"5","logic_type":{"id":28,"name":"On"},"value":1}""")
                    : FakeGameLink.Ok("""{"reference_id":"5","pressure_kpa":101.3,"ports":[]}""");
        };

        string text = await bench.RunAsync(
            "print(sg.inspect('atmosphere', id='5')['pressure_kpa'])\n" +
            "print(sg.inspect('connections', id='5')['reference_id'])\n" +
            "print(sg.read('logic', id='5', logic='On')['value'])\n" +
            "print(sg.inspect('atmosphere', id=['5'])['results'][0]['payload']['pressure_kpa'])\n");

        Assert.True(text.Contains("101.3\n5\n1\n101.3"), text);
    }

    [Fact]
    public void ASurveyThatLeavesCellsOutReadsEveryCell()
    {
        Assert.Equal(int.MaxValue, SurveySections.Of(new[] { "pieces" }).PageLimit(8));
        Assert.Equal(int.MaxValue, SurveySections.Of(new[] { "pieces", "devices" }).PageLimit(8));
        Assert.Equal(8, SurveySections.Of(new[] { "cells", "pieces" }).PageLimit(8));
        Assert.Equal(8, SurveySections.Of(null).PageLimit(8));
        Assert.Equal(int.MaxValue, SurveySections.Of(null).PageLimit(null));
    }
}
