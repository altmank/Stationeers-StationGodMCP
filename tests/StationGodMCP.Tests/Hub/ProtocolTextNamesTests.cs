#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;
using static StationGodMCP.Tests.Hub.Verbs;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// The mod's rename map does not reach the texts of protocol-method replies and refusals (inspect_controllers,
/// sample_logic, subscribe, drop_job, stream_costs, hold_restore, catalogue): the hub says them in verb names itself,
/// on the direct face (the MCP text), the script face and the program face (verb replies and mod methods relayed).
/// </summary>
public sealed class ProtocolTextNamesTests
{
    // Texts as a mod would word them without the rename map: handler tools and handler-only arguments.
    private const string Refusal =
        "Argument 'reference_ids' of inspect_controllers takes 1 to 256 ids; for a network use network_id or list_devices with prefab_hash.";

    private const string Warning = "near_player_m was ignored: sample_logic reads targets, see read_logic_many and logic_type.";

    private static GameReply Refused() => new GameReply.Refused("invalid_argument", Refusal,
        JsonDocument.Parse("""{"problems":[{"path":"reference_ids","problem":"one of reference_id, reference_ids or network_id; see find_things"}]}""").RootElement.Clone());

    private static GameReply Warned() => FakeGameLink.Ok(JsonSerializer.Serialize(new
    {
        devices = Array.Empty<object>(),
        warnings = new[] { new { code = "ignored", message = Warning } },
        note = "use set_ic_pins for pins; to_slot defaults to auto",
        fix = "send stack_count with get_ic_status"
    }));

    [Theory]
    [InlineData("inspect", "controllers", """{"id":"5"}""")]
    [InlineData("read", "sample", """{"id":"5","logic":"On"}""")]
    public async Task TheMcpTextHasNoHandlerName(string verb, string what, string options)
    {
        Regex names = ReviewFixTests.HandlerNames();
        foreach ((Func<GameReply> answer, string phrase) in new (Func<GameReply>, string)[]
                 {
                     (Refused, "inspect {what: \"controllers\"}"), (Warned, "read {what: \"sample\"}")
                 })
        {
            FakeGameLink game = new() { Answer = _ => answer() };
            McpAdapter adapter = new(new SidecarOptions(new ClientOptions(new GameTarget.Pipe("x")),
                new OutputFolder(Path.GetTempPath()), 200 * 1024), game);
            JsonObject arguments = Options(options);
            arguments["what"] = what;

            string line = (await adapter.HandleAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = verb, arguments }
            })))!;

            string text = (string)JsonNode.Parse(line)!["result"]!["content"]![0]!["text"]!;
            Assert.False(names.IsMatch(text), $"{names.Match(text).Value} in {text}");
            Assert.Contains(phrase, Texts(JsonDocument.Parse(text).RootElement));
        }
    }

    [Theory]
    [InlineData(Face.Script)]
    [InlineData(Face.Program)]
    public async Task ScriptAndProgramVerbRepliesHaveNoHandlerName(Face face)
    {
        Regex names = ReviewFixTests.HandlerNames();
        foreach (Func<GameReply> answer in new Func<GameReply>[] { Refused, Warned })
        {
            FakeGameLink game = new() { Answer = _ => answer() };

            VerbReply reply = await game.Pipeline().CallAsync(new VerbCall("inspect", "controllers", Options("""{"id":["5"]}"""), face),
                CancellationToken.None);

            // Rows keep their keys (reference_id is the row shape's); every text in them is in verb names.
            string json = reply is VerbReply.Done done ? done.Reply!.ToJsonString() : ((VerbReply.Failed)reply).ToJson().ToJsonString();
            string text = Texts(JsonDocument.Parse(json).RootElement);
            Assert.NotEmpty(text);
            Assert.False(names.IsMatch(text), $"{names.Match(text).Value} in {text}");
        }
    }

    [Fact]
    public void ARunsHoldRefusalHasNoHandlerName()
    {
        VerbReply.Failed failed = VerbPipeline.Failure(new GameReply.Refused("too_many_restores",
            "hold_restore keeps 64 restores; clear one with reference_id and logic_type first (see read_logic_many).", null));

        Assert.False(ReviewFixTests.HandlerNames().IsMatch(failed.Message), failed.Message);
        Assert.Contains("id and logic", failed.Message);
    }

    [Theory]
    [InlineData("subscribe", """{"topic":"logic","targets":[{"reference_id":"5","logic_type":"On"}]}""")]
    [InlineData("drop_job", """{"job_id":"place-3"}""")]
    [InlineData("stream_costs", "{}")]
    [InlineData("catalogue", "{}")]
    public async Task ModMethodsRelayedToAProgramHaveNoHandlerNameInTheirTexts(string method, string parameters)
    {
        Regex names = ReviewFixTests.HandlerNames();
        foreach (Func<GameReply> answer in new Func<GameReply>[] { Refused, Warned })
        {
            FakeHubLink link = new() { Answer = (_, _) => Task.FromResult(answer()) };
            await using HubCaller caller = HubCaller.Start(link);
            await caller.HelloAsync();

            JsonElement reply = await caller.CallAsync("1", method, parameters);

            string text = reply.TryGetProperty("error", out JsonElement error)
                ? error.GetRawText()
                : reply.TryGetProperty("result", out JsonElement result) ? Texts(result) : string.Empty;
            Assert.False(names.IsMatch(text), $"{method}: {names.Match(text).Value} in {text}");
        }
    }

    // A relayed result keeps its data's keys (reference_id is a key of the mod's own reply); its texts are said in verb
    // names.
    private static string Texts(JsonElement result)
    {
        List<string> texts = new();
        Collect(result, false, texts);
        return string.Join(" | ", texts);
    }

    private static void Collect(JsonElement element, bool text, List<string> texts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Collect(property.Value, text || property.Name is "message" or "warnings" or "note" or "fix" or "problem", texts);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Collect(item, text, texts);
                }

                break;
            case JsonValueKind.String when text:
                texts.Add(element.GetString()!);
                break;
        }
    }
}
