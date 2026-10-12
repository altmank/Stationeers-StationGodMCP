#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// Every what's catalogue example goes through the pipeline on the direct face and becomes calls to its own handlers
/// (or the hub's helper reads), each with params the real handler schema takes (required arguments, types, enums:
/// ArgumentCheck against the method catalogue). The few whose example needs what the hub does not bring yet answer
/// not_available or invalid_argument, each named here with why.
/// </summary>
public sealed class VerbExampleTests
{
    // What the example needs that the hub does not bring yet, by what, with the code it answers.
    private static readonly Dictionary<string, (string Code, string Why)> Later = new()
    {
        ["check.ignition"] = ("not_available", "id"),
        ["server.stop"] = ("not_available", "runs piece"),
        ["check.replaceable"] = ("invalid_argument", "no ref 'r12'")
    };

    // The hub's own reads around a call, and the protocol method controllers go to: not a what's handler.
    internal static readonly string[] Helpers = { "thing_health", "player_vitals", "read_logic_many", "inspect_controllers", "connections" };

    public static IEnumerable<object[]> Whats() =>
        VerbCatalogue.Embedded.Verbs
            .Where(verb => verb.Name != "run_script")
            .SelectMany(verb => verb.WhatNames.Select(name => new object[] { verb.Whats[name].Key }));

    [Fact]
    public void TheCatalogueHasNinetyOneWhatsAndThreeWhatlessVerbs()
    {
        int whats = VerbCatalogue.Embedded.Verbs.Where(verb => verb.HasWhats).Sum(verb => verb.WhatNames.Count);
        Assert.Equal(91, whats);
        Assert.Equal(new[] { "undo", "help", "run_script" },
            VerbCatalogue.Embedded.Verbs.Where(verb => !verb.HasWhats).Select(verb => verb.Name));
    }

    [Theory]
    [MemberData(nameof(Whats))]
    public void TheExampleTranslatesIntoHandlerCallsTheHandlersTake(string key)
    {
        WhatEntry what = WhatOf(key);
        FakeGameLink game = new();
        game.Answer = call => FakeGameLink.Ok(call.Method == "player_vitals" ? """{"reference_id":"77"}""" : "{}");
        JsonObject example = (JsonObject)(what.Example ?? new JsonObject()).DeepClone();
        string? file = null;
        if (example["code_file"] != null)
        {
            // The example's path is on the user's machine: a file of this one stands in for it.
            file = System.IO.Path.GetTempFileName();
            System.IO.File.WriteAllText(file, "yield");
            example["code_file"] = file;
        }

        VerbReply reply = game.Pipeline().Call(what.Verb, what.Name == "-" ? null : what.Name, example.ToJsonString());
        if (file != null)
        {
            System.IO.File.Delete(file);
        }

        if (Later.TryGetValue(key, out (string Code, string Why) later))
        {
            VerbReply.Failed failed = reply.Failed();
            Assert.Equal(later.Code, failed.Code);
            Assert.Contains(later.Why, failed.Message);
            Assert.Empty(game.Calls);
            return;
        }

        reply.Done();
        if (what.Verb == "help")
        {
            Assert.Empty(game.Calls);
            return;
        }

        // write paint's example paints a network: it reports, listing the pieces it reads (no paint is sent).
        List<string> handlers = what.Handlers.Select(handler => handler.Name).ToList();
        Assert.Contains(game.Calls, call => handlers.Contains(call.Method) || key is "inspect.controllers" or "write.paint" ||
                                            LegacyVerbMap.JobPoller((string?)call.Params()["job_id"] ?? "") == call.Method);
        foreach (GameCall sent in game.Calls)
        {
            Assert.True(handlers.Contains(sent.Method) || Helpers.Contains(sent.Method) ||
                        LegacyVerbMap.JobPoller((string?)sent.Params()["job_id"] ?? "") == sent.Method,
                $"{key} sent {sent.Method}");
            Assert.NotNull(sent.Rename);
            if (Program.InputSchemas.TryGetValue(sent.Method, out JsonElement schema))
            {
                Assert.True(ArgumentCheck.Problems(schema, sent.Parameters).Count == 0,
                    $"{key} -> {sent.Method} {sent.Parameters.GetRawText()}: " +
                    string.Join(" ", ArgumentCheck.Problems(schema, sent.Parameters)));
            }
        }
    }

    internal static WhatEntry WhatOf(string key)
    {
        string[] parts = key.Split('.');
        VerbEntry verb = VerbCatalogue.Embedded.Verb(parts[0])!;
        return verb.What(parts.Length > 1 ? parts[1] : null)!;
    }
}
