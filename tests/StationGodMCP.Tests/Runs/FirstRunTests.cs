#nullable enable

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using StationGodMCP.Tests.Sidecar;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>The whole MCP server as it starts: run_script as its very first tool call.</summary>
[Collection(PythonCollection.Name)]
public sealed class FirstRunTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sg-first-run-" + Guid.NewGuid().ToString("N"));

    [PythonFact]
    public async Task RunScriptAsTheFirstToolCallAfterStartRuns()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Features = ["shape", "shape.paths", "shape.omit", "subscriptions", "cancel", "streams", "resume"];
        // The game is slow to answer at first, as a real one is on its first connection.
        game.WelcomeDelay = _ => TimeSpan.FromMilliseconds(800);
        game.CatalogueDelay = TimeSpan.FromMilliseconds(500);
        game.Answer = call => Task.FromResult<string?>(call.Method == "read"
            ? call.Ok("""{"results":[{"index":0,"ok":true,"reference_id":"1","logic_type":{"name":"On"},"value":1}]}""")
            : call.Ok("{}"));
        using AnonymousPipeServerStream toServer = new(PipeDirection.Out);
        using AnonymousPipeClientStream serverInput = new(PipeDirection.In, toServer.ClientSafePipeHandle);
        using AnonymousPipeServerStream serverOutput = new(PipeDirection.Out);
        using AnonymousPipeClientStream fromServer = new(PipeDirection.In, serverOutput.ClientSafePipeHandle);
        using StreamReader reader = new(fromServer, new UTF8Encoding(false));
        SidecarOptions options = new(new ClientOptions(game.Target), new OutputFolder(_folder), 200 * 1024)
        {
            JournalDirectory = Path.Combine(_folder, "journal")
        };
        Task serving = Task.Run(() => Program.ServeAsync(options, serverInput, serverOutput));

        byte[] call = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"run_script","arguments":{"code":"print(sg.read('logic', id='1', logic='On')['results'][0]['ok'])","confirm":true,"max_wait":20}}}""" + "\n");
        toServer.Write(call, 0, call.Length);
        toServer.Flush();
        string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        toServer.Dispose();
        await serving.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(line);
        using JsonDocument reply = JsonDocument.Parse(line!);
        string text = reply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.StartsWith("script-1 done", text);
    }

    [PythonFact]
    public async Task TheBuiltServerRunsAScriptAsItsFirstToolCall()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Features = ["shape", "shape.paths", "shape.omit", "subscriptions", "cancel", "streams", "resume"];
        game.Answer = call => Task.FromResult<string?>(call.Method == "read"
            ? call.Ok("""{"results":[{"index":0,"ok":true,"reference_id":"1","logic_type":{"name":"On"},"value":1}]}""")
            : call.Ok("{}"));
        System.Diagnostics.ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add(typeof(HubSession).Assembly.Location);
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(((GameTarget.Pipe)game.Target).Name);
        start.ArgumentList.Add("--journal-dir");
        start.ArgumentList.Add(Path.Combine(_folder, "journal"));
        using System.Diagnostics.Process server = System.Diagnostics.Process.Start(start)!;
        StringBuilder errors = new();
        server.ErrorDataReceived += (_, line) =>
        {
            lock (errors)
            {
                errors.AppendLine(line.Data);
            }
        };
        server.BeginErrorReadLine();
        try
        {
            StreamWriter input = server.StandardInput;
            input.AutoFlush = true;
            input.NewLine = "\n";
            await input.WriteLineAsync("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""");
            await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await input.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""");
            await input.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"run_script","arguments":{"code":"print(sg.read('logic', id='1', logic='On')['results'][0]['value'])","confirm":true,"max_wait":20}}}""");
            string? line = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(40));
            input.Close();

            Assert.NotNull(line);
            using JsonDocument reply = JsonDocument.Parse(line!);
            string text = reply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.True(text.StartsWith("script-1 done", StringComparison.Ordinal), text + "\n" + errors);
            Assert.True(server.WaitForExit(20000), "The server did not exit after stdin closed.");
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
