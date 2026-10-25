#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// Every what's catalogue example goes through the pipeline on the direct face and reaches the mod as one call of its
/// verb: params {what, ...the example's options}, without the hub's own options. The few the hub answers itself (help,
/// what a runless hub refuses, a ref no reply minted) send nothing, each named here with why.
/// </summary>
public sealed class VerbExampleTests
{
    // What the hub answers itself, by what, with the code it answers.
    private static readonly Dictionary<string, (string Code, string Why)> Hub = new()
    {
        ["server.stop"] = ("not_available", "serves no runs"),
        ["check.replaceable"] = ("invalid_argument", "no ref 'r12'")
    };

    private static readonly string[] HubOptions = { "fields", "omit", "list_limits", "output_file", "whole", "repeat", "max_wait" };

    public static IEnumerable<object[]> Whats() =>
        VerbCatalogue.Embedded.Verbs
            .Where(verb => verb.Name != "run_script")
            .SelectMany(verb => verb.WhatNames.Select(name => verb.Whats[name]))
            .Where(what => !what.Hub)
            .Select(what => new object[] { what.Key });

    [Fact]
    public void TheCatalogueHas105WhatsAndThreeWhatlessVerbs()
    {
        int whats = VerbCatalogue.Embedded.Verbs.Where(verb => verb.HasWhats).Sum(verb => verb.WhatNames.Count);
        Assert.Equal(105, whats);
        Assert.Equal(new[] { "undo", "help", "run_script" },
            VerbCatalogue.Embedded.Verbs.Where(verb => !verb.HasWhats).Select(verb => verb.Name));
    }

    [Theory]
    [MemberData(nameof(Whats))]
    public void TheExampleGoesToTheModAsItsVerb(string key)
    {
        WhatEntry what = WhatOf(key);
        FakeGameLink game = new();
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

        if (Hub.TryGetValue(key, out (string Code, string Why) own))
        {
            VerbReply.Failed failed = reply.Failed();
            Assert.Equal(own.Code, failed.Code);
            Assert.Contains(own.Why, failed.Message);
            Assert.Empty(game.Calls);
            return;
        }

        reply.Done();
        if (what.Verb == "help")
        {
            Assert.Empty(game.Calls);
            return;
        }

        GameCall sent = Assert.Single(game.Calls);
        JsonObject parameters = sent.Params();
        Assert.Equal(what.Verb, sent.Method);
        Assert.Equal(what.Name == "-" ? null : what.Name, (string?)parameters["what"]);
        foreach ((string name, JsonNode? value) in example)
        {
            if (HubOptions.Contains(name) || name is "confirm" or "code_file")
            {
                Assert.True(name is "confirm" || parameters[name] == null, $"{key}: {name} reached the mod");
                continue;
            }

            Assert.True(JsonNode.DeepEquals(value, parameters[name]), $"{key}: {name} sent as {parameters[name]?.ToJsonString()}");
        }

        if (file != null)
        {
            Assert.Equal("yield", (string?)parameters["code"]);
        }
    }

    internal static WhatEntry WhatOf(string key)
    {
        string[] parts = key.Split('.');
        VerbEntry verb = VerbCatalogue.Embedded.Verb(parts[0])!;
        return verb.What(parts.Length > 1 ? parts[1] : null)!;
    }
}
