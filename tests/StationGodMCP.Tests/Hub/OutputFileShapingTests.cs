#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Hub;

/// <summary>A reply written to a file is shaped as an inline one: fields and omit reach the game with output_file.</summary>
public sealed class OutputFileShapingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sg-outfile-" + Guid.NewGuid().ToString("N"));

    public OutputFileShapingTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task FieldsGoToTheGameWithOutputFile()
    {
        FakeGameLink game = new();
        SidecarOptions options = new(new ClientOptions(new GameTarget.Pipe("x")), new OutputFolder(_folder), 200 * 1024);
        await using McpAdapter adapter = new(options, game);

        await adapter.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find","arguments":{"what":"thing","fields":["reference_id","prefab_name"],"output_file":true}}}""");

        GameCall call = game.Calls.Single(sent => sent.Method == "find");
        Assert.NotNull(call.Shape);
        Assert.Contains("reference_id", call.Shape!.Value.GetRawText());
    }
}
