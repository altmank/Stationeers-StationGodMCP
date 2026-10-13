#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Hub;

/// <summary>The C2a review's findings on the adapter and the recording.</summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sgm-review-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task AnInternalErrorReachesTheAgentWithoutItsDetails()
    {
        McpAdapter adapter = new(new SidecarOptions(new ClientOptions(new GameTarget.Pipe("x")), new OutputFolder(_folder),
            200 * 1024), new ThrowingLink());

        string line = (await adapter.HandleAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read","arguments":{"what":"clock"}}}"""))!;

        Assert.Equal("Internal error in the MCP server; its log has the details.",
            (string)JsonNode.Parse(line)!["error"]!["message"]!);
    }

    [Fact]
    public async System.Threading.Tasks.Task TheAdapterRecordsCallsItRefusesItself()
    {
        string path = Path.Combine(_folder, "rec.jsonl");
        SidecarOptions options = new(new ClientOptions(new GameTarget.Pipe("x")), new OutputFolder(_folder), 200 * 1024, path);
        McpAdapter adapter = new(options, new FakeGameLink());

        await adapter.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"finde","arguments":{"x":1}}}""");
        await adapter.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"find","arguments":{"what":"thing","output_file":"../x"}}}""");
        await adapter.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"read","arguments":{"what":"clock"}}}""");
        await adapter.DisposeAsync();

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(new[] { "finde", "find", "read" }, lines.Select(line => (string)JsonNode.Parse(line)!["verb"]!));
        Assert.Equal("""["x"]""", JsonNode.Parse(lines[0])!["options"]!.ToJsonString());
    }

    [Fact]
    public void ARecordingThatCannotBeWrittenNeverFailsTheCall()
    {
        string path = Path.Combine(_folder, "rec.jsonl");
        CallRecorder recorder = new(path);
        recorder.Dispose();

        new FakeGameLink().Pipeline(recorder: recorder).Call("read", "clock", "{}").Done();
    }

    private sealed class ThrowingLink : IGameLink
    {
        public string? WorldId => null;

        public event Action<string?>? WorldChanged
        {
            add { }
            remove { }
        }

        public long NewStream() => throw new NotSupportedException();

        public System.Threading.Tasks.Task<GameReply> CallAsync(GameCall call, System.Threading.CancellationToken cancellation) =>
            throw new InvalidOperationException("game_clock exploded in describe_device");

        public System.Threading.Tasks.Task EndStreamAsync(long stream) => throw new NotSupportedException();

        public System.Threading.Tasks.Task SessionEndAsync() => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task<GameReply> HoldRestoreAsync(long stream, System.Text.Json.JsonElement change,
            System.Threading.CancellationToken cancellation) => throw new NotSupportedException();

        public System.Threading.Tasks.Task<StreamCost[]> StreamCostsAsync(IReadOnlyList<long> streams,
            System.Threading.CancellationToken cancellation) => throw new NotSupportedException();

        public System.Threading.Tasks.Task<System.Text.Json.JsonElement?> WaitJobAsync(string jobId, TimeSpan timeout,
            System.Threading.CancellationToken cancellation) => throw new NotSupportedException();
    }
}
