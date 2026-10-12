#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// Every what's catalogue example goes through the pipeline on the direct face and becomes a call to one of its own
/// handlers (or the job poller), checked and translated without error. The few whose example needs a later piece or a
/// handler the mod does not have yet answer not_available, each named here with why.
/// </summary>
public sealed class VerbExampleTests
{
    // What the example needs that C2a does not bring, by what.
    private static readonly Dictionary<string, string> Later = new()
    {
        ["inspect.controllers"] = "is not in the mod yet",
        ["check.ignition"] = "id",
        ["check.replaceable"] = "refs",
        ["write.paint"] = "network",
        ["server.stop"] = "runs piece"
    };

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
    public void TheExampleTranslatesIntoAHandlerCall(string key)
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

        if (Later.TryGetValue(key, out string? why))
        {
            VerbReply.Failed failed = reply.Failed();
            Assert.Equal("not_available", failed.Code);
            Assert.Contains(why, failed.Message);
            Assert.Empty(game.Calls);
            return;
        }

        reply.Done();
        if (what.Verb == "help")
        {
            Assert.Empty(game.Calls);
            return;
        }

        Assert.NotEmpty(game.Calls);
        List<string> handlers = what.Handlers.Select(handler => handler.Name).ToList();
        foreach (GameCall sent in game.Calls)
        {
            Assert.True(handlers.Contains(sent.Method) ||
                        LegacyVerbMap.JobPoller((string?)sent.Params()["job_id"] ?? "") == sent.Method,
                $"{key} sent {sent.Method}");
            Assert.NotNull(sent.Rename);
        }
    }

    internal static WhatEntry WhatOf(string key)
    {
        string[] parts = key.Split('.');
        VerbEntry verb = VerbCatalogue.Embedded.Verb(parts[0])!;
        return verb.What(parts.Length > 1 ? parts[1] : null)!;
    }
}
