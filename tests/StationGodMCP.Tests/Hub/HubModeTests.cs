#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using StationGodMCP.Tests.Sidecar;
using Xunit;

namespace StationGodMCP.Tests.Hub;

public sealed class HubModeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task TheBuiltServerWithHubServesTheHubProtocolOnStdioAndExitsWhenStdinCloses()
    {
        await using FakeGame game = FakeGame.OnPipe();
        game.Features = ["shape", "subscriptions", "cancel", "streams"];
        game.Answer = call => Task.FromResult<string?>(call.Method switch
        {
            "thing_health" => call.Ok("""{"results":[{"reference_id":"5","damage_ratio":0.0}]}"""),
            _ => call.Ok("""{"value":1}""")
        });
        StringBuilder errors = new();
        using Process hub = StartHub(((GameTarget.Pipe)game.Target).Name, errors);
        try
        {
            StreamWriter input = hub.StandardInput;
            input.AutoFlush = true;
            input.NewLine = "\n";

            await input.WriteLineAsync(HubCaller.Hello);
            JsonElement welcome = await NextAsync(hub, errors);
            await input.WriteLineAsync(HubCaller.Call("1", "verb", """{"verb":"read","what":"logic","args":{"id":"5","logic":"On"}}"""));
            JsonElement verb = await NextAsync(hub, errors);
            await input.WriteLineAsync(HubCaller.Call("2", "thing_health", """{"reference_ids":["5"]}"""));
            JsonElement passed = await NextAsync(hub, errors);
            input.Close();

            Assert.True(hub.WaitForExit((int)Patience.TotalMilliseconds), "The hub did not exit after stdin closed.");
            Assert.Equal(0, hub.ExitCode);
            Assert.Equal("welcome", welcome.GetProperty("type").GetString());
            Assert.Contains("pipeline", welcome.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
            Assert.Equal("Running", welcome.GetProperty("server").GetProperty("game_state").GetString());
            Assert.True(verb.GetProperty("ok").GetBoolean(), verb.GetRawText());
            Assert.Equal("2", passed.GetProperty("id").GetString());
            Assert.Equal("5", passed.GetProperty("result").GetProperty("results")[0].GetProperty("reference_id").GetString());
            FakeCall health = Assert.Single(game.CallsTo("thing_health"));
            Assert.True(health.Message.GetProperty("stream").GetInt64() > 0);
            // No session_end was sent, so the link closes without bye: the mod keeps the held restores to apply.
            Assert.DoesNotContain(game.Received, message =>
                message.TryGetProperty("type", out JsonElement type) && type.GetString() == "bye");
        }
        finally
        {
            if (!hub.HasExited)
            {
                hub.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task WithTheGameDownTheHubExitsAtOnceWhenStdinCloses()
    {
        StringBuilder errors = new();
        using Process hub = StartHub("StationGodMCP-absent-" + Guid.NewGuid().ToString("N"), errors);
        try
        {
            StreamWriter input = hub.StandardInput;
            input.AutoFlush = true;
            input.NewLine = "\n";
            await input.WriteLineAsync(HubCaller.Hello);
            JsonElement welcome = await NextAsync(hub, errors);
            await input.WriteLineAsync(HubCaller.Call("1", "thing_health", """{"reference_ids":["5"]}"""));
            JsonElement unreachable = await NextAsync(hub, errors);

            Stopwatch exiting = Stopwatch.StartNew();
            input.Close();
            bool exited = hub.WaitForExit((int)Patience.TotalMilliseconds);
            exiting.Stop();

            Assert.True(exited, "The hub did not exit after stdin closed.");
            Assert.True(exiting.Elapsed < TimeSpan.FromSeconds(1.5), $"The hub took {exiting.Elapsed} to exit.");
            Assert.Equal("Unreachable", welcome.GetProperty("server").GetProperty("game_state").GetString());
            Assert.Equal("game_unreachable", unreachable.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            if (!hub.HasExited)
            {
                hub.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public void TheHubFlagIsNotTakenFromAnOptionsValue()
    {
        SidecarOptions named = Assert.IsType<SidecarOptions.Parsed.Valid>(SidecarOptions.Parse(["--client", "--hub"], _ => null)).Options;
        SidecarOptions after = Assert.IsType<SidecarOptions.Parsed.Valid>(SidecarOptions.Parse(["--client", "x", "--hub"], _ => null)).Options;

        Assert.False(named.Hub);
        Assert.Equal("--hub", named.Client.ClientName);
        Assert.True(after.Hub);
    }

    [Fact]
    public void TheHubOptionsParseWithTheirDefaults()
    {
        SidecarOptions defaults = Assert.IsType<SidecarOptions.Parsed.Valid>(SidecarOptions.Parse([], _ => null)).Options;
        SidecarOptions given = Assert.IsType<SidecarOptions.Parsed.Valid>(SidecarOptions.Parse(
            ["--hub", "--confirmed-ceiling-s", "0", "--journal-dir", "C:\\j", "--python", "C:\\py\\python.exe", "--pipe", "P"],
            _ => null)).Options;

        Assert.False(defaults.Hub);
        Assert.True(defaults.AdoptsJournals);
        Assert.Equal(30, defaults.ConfirmedCeilingSeconds);
        Assert.EndsWith(Path.Combine("journal"), defaults.JournalDirectory);
        Assert.NotEmpty(defaults.Python);
        Assert.True(given.Hub);
        Assert.False(given.AdoptsJournals);
        Assert.Equal(0, given.ConfirmedCeilingSeconds);
        Assert.Equal(Path.GetFullPath("C:\\j"), given.JournalDirectory);
        Assert.Equal(new[] { "C:\\py\\python.exe" }, given.Python);
        Assert.Equal(new GameTarget.Pipe("P"), given.Client.Target);
        Assert.IsType<SidecarOptions.Parsed.Invalid>(SidecarOptions.Parse(["--confirmed-ceiling-s", "-1"], _ => null));
        Assert.IsType<SidecarOptions.Parsed.Invalid>(SidecarOptions.Parse(["--confirmed-ceiling-s", "soon"], _ => null));
        Assert.IsType<SidecarOptions.Parsed.Invalid>(SidecarOptions.Parse(["--journal-dir", " "], _ => null));
        Assert.IsType<SidecarOptions.Parsed.Invalid>(SidecarOptions.Parse(["--python", ""], _ => null));
    }

    // The built server, run with --hub against the pipe, its stderr gathered.
    private static Process StartHub(string pipe, StringBuilder errors)
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add(typeof(HubSession).Assembly.Location);
        start.ArgumentList.Add("--hub");
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipe);
        Process hub = Process.Start(start)!;
        hub.ErrorDataReceived += (_, line) =>
        {
            lock (errors)
            {
                errors.AppendLine(line.Data);
            }
        };
        hub.BeginErrorReadLine();
        return hub;
    }

    private static async Task<JsonElement> NextAsync(Process hub, StringBuilder errors)
    {
        string? line = await hub.StandardOutput.ReadLineAsync().WaitAsync(Patience);
        Assert.True(line != null, "The hub closed stdout: " + errors);
        using JsonDocument document = JsonDocument.Parse(line!);
        return document.RootElement.Clone();
    }
}
