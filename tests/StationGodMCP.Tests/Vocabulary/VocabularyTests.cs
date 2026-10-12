#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Protocol;
using Xunit;

namespace StationGodMCP.Tests.VocabularyChecks;

/// <summary>
/// A call's rename map changes only the words the mod prints for argument and tool names. Without one every name, path
/// and template prints as today, as the same string and without allocating.
/// </summary>
public sealed class VocabularyTests
{
    internal static RenameMap Map(string json)
    {
        RenameMap? map = RenameMap.Read(JToken.Parse(json), out string? problem);
        Assert.Null(problem);
        return Assert.IsType<RenameMap>(map);
    }

    internal static RenameMap VerbMap() => Map("""
        {"args": {"reference_id": "id", "reference_ids": "id", "min": "box", "max": "box", "dry_run": "confirm",
                  "near_player_m": "near: \"player\" + radius", "items": "entries", "limit": "count"},
         "tools": {"find_items": "find {what: \"items\"}", "rooms": "survey {what: \"rooms\"}"}}
        """);

    [Fact]
    public void WithoutAMapEveryNameIsTheSameString()
    {
        const string name = "reference_id";

        Assert.Null(Vocabulary.Current);
        Assert.Same(name, Vocabulary.Arg(name));
        Assert.Same(name, Vocabulary.Tool(name));
        Assert.Same("items[3].reference_id", Vocabulary.Path("items[3].reference_id"));
    }

    [Fact]
    public void WithAMapNamesPrintAsTheCallerSaysThem()
    {
        using (Vocabulary.Enter(VerbMap()))
        {
            Assert.Equal("id", Vocabulary.Arg("reference_id"));
            Assert.Equal("box", Vocabulary.Arg("min"));
            Assert.Equal("box", Vocabulary.Arg("max"));
            Assert.Equal("kind", Vocabulary.Arg("kind"));
            Assert.Equal("find {what: \"items\"}", Vocabulary.Tool("find_items"));
            Assert.Equal("place_structure", Vocabulary.Tool("place_structure"));
            Assert.Equal("entries[3].id", Vocabulary.Path("items[3].reference_id"));
            Assert.Equal("labels[2].name", Vocabulary.Path("labels[2].name"));
        }

        Assert.Null(Vocabulary.Current);
    }

    [Fact]
    public void ArgumentsAndToolsAreApart()
    {
        using (Vocabulary.Enter(Map("""{"tools": {"rooms": "survey rooms"}}""")))
        {
            Assert.Equal("rooms", Vocabulary.Arg("rooms"));
            Assert.Equal("survey rooms", Vocabulary.Tool("rooms"));
        }
    }

    [Fact]
    public void ATemplateWithoutAMapIsTodaysText()
    {
        Assert.Equal("Pass reference_id, network_id with kind, or min and max (open ends in a box).",
            Vocabulary.Text("Pass {arg:reference_id}, {arg:network_id} with {arg:kind}, or {arg:min} and {arg:max} " +
                            "(open ends in a box)."));
        Assert.Equal("room_id must be a room id as rooms reports it.",
            Vocabulary.Text("{arg:room_id} must be a room id as {tool:rooms} reports it."));
        Assert.Equal("from: {reference_id, ports: [...]}", Vocabulary.Text("from: {{arg:reference_id}, ports: [...]}"));
        Assert.Equal("no marks {0:0.##} here", Vocabulary.Text("no marks {0:0.##} here"));
        Assert.Equal("an {arg:open mark", Vocabulary.Text("an {arg:open mark"));
    }

    [Fact]
    public void ATemplateWithAMapNamesWhatTheCallerSays()
    {
        using (Vocabulary.Enter(VerbMap()))
        {
            Assert.Equal("Pass id, network_id with kind, or box and box (open ends in a box).",
                Vocabulary.Text("Pass {arg:reference_id}, {arg:network_id} with {arg:kind}, or {arg:min} and " +
                                "{arg:max} (open ends in a box)."));
            Assert.Equal("Call find {what: \"items\"} first.", Vocabulary.Text("Call {tool:find_items} first."));
        }
    }

    [Fact]
    public void AFormatTemplateKeepsAPhrasesBracesOutOfTheFormat()
    {
        const string template = "{arg:above_floor_m}: no floor below {0:0.#}; see {tool:find_items}.";

        Assert.Equal("above_floor_m: no floor below 2.5; see find_items.",
            string.Format(System.Globalization.CultureInfo.InvariantCulture, Vocabulary.Format(template), 2.5));
        using (Vocabulary.Enter(VerbMap()))
        {
            Assert.Equal("above_floor_m: no floor below 2.5; see find {what: \"items\"}.",
                string.Format(System.Globalization.CultureInfo.InvariantCulture, Vocabulary.Format(template), 2.5));
        }
    }

    [Fact]
    public void ATemplateWithoutAMapAllocatesNothingAfterItsFirstUse()
    {
        const string template = "Pass {arg:job_id}, or {arg:reference_ids}.";
        string first = Vocabulary.Text(template);
        string again = Vocabulary.Text(template);
        Vocabulary.Arg("reference_id");
        Vocabulary.Path("items[0].reference_id");

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100; index++)
        {
            again = Vocabulary.Text(template);
            Vocabulary.Arg("reference_id");
            Vocabulary.Tool("find_items");
            Vocabulary.Path("items[0].reference_id");
            using (Vocabulary.Enter(null))
            {
            }
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Same(first, again);
        Assert.Equal("Pass job_id, or reference_ids.", again);
    }

    [Fact]
    public void AFixedListIsTheSameListWithoutAMapAndTheCallersWordsWithOne()
    {
        TextList notes = new TextList("A real run needs {arg:dry_run}: false.", "Nothing to name.");

        List<string> plain = notes.Said();
        Assert.Equal(new[] { "A real run needs dry_run: false.", "Nothing to name." }, plain);
        Assert.Same(plain, notes.Said());
        using (Vocabulary.Enter(VerbMap()))
        {
            Assert.Equal(new[] { "A real run needs confirm: false.", "Nothing to name." }, notes.Said());
        }
    }

    [Fact]
    public void ScopesNestAndRestore()
    {
        RenameMap outer = Map("""{"args": {"min": "box"}}""");
        RenameMap inner = Map("""{"args": {"min": "lowest"}}""");
        using (Vocabulary.Enter(outer))
        {
            using (Vocabulary.Enter(inner))
            {
                Assert.Equal("lowest", Vocabulary.Arg("min"));
            }

            Assert.Equal("box", Vocabulary.Arg("min"));
        }

        Assert.Equal("min", Vocabulary.Arg("min"));
    }

    [Fact]
    public void AnotherThreadNeverSeesTheMap()
    {
        string seen = string.Empty;
        using (Vocabulary.Enter(VerbMap()))
        {
            System.Threading.Thread other = new System.Threading.Thread(() => seen = Vocabulary.Arg("reference_id"));
            other.Start();
            other.Join();
        }

        Assert.Equal("reference_id", seen);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"args": {}, "tools": null}""")]
    public void AnEmptyMapIsNoMap(string json)
    {
        Assert.Null(RenameMap.Read(JToken.Parse(json), out string? problem));
        Assert.Null(problem);
    }

    public static IEnumerable<object[]> Malformed() => new[]
    {
        new object[] { "[]", "rename must be an object" },
        new object[] { "\"id\"", "rename must be an object" },
        new object[] { """{"names": {}}""", "rename takes args and tools only; 'names' is neither." },
        new object[] { """{"args": ["min"]}""", "rename.args must be an object of name: phrase." },
        new object[] { """{"args": {"Min": "box"}}""", "rename.args key 'Min' is not a catalogue name" },
        new object[] { """{"args": {"9lives": "box"}}""", "rename.args key '9lives' is not a catalogue name" },
        new object[] { """{"tools": {"find-items": "find"}}""", "rename.tools key 'find-items' is not a catalogue name" },
        new object[] { """{"args": {"min": 3}}""", "rename.args.min must be a string of 1 to 128 characters" },
        new object[] { """{"args": {"min": ""}}""", "rename.args.min must be a string of 1 to 128 characters" },
        new object[] { """{"args": {"min": "a\nb"}}""", "rename.args.min must be a string of 1 to 128 characters" },
        new object[] { "{\"args\": {\"min\": \"" + new string('x', 129) + "\"}}", "rename.args.min must be a string" },
        new object[] { "{\"args\": {\"" + new string('a', 65) + "\": \"x\"}}", "is not a catalogue name" }
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void AMalformedMapIsRefusedSayingWhy(string json, string expected)
    {
        Assert.Null(RenameMap.Read(JToken.Parse(json), out string? problem));
        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void AMapHoldsAtMost256Entries()
    {
        JObject arguments = new JObject();
        JObject tools = new JObject();
        for (int index = 0; index < 200; index++)
        {
            arguments["a" + index] = "x";
        }

        for (int index = 0; index < 56; index++)
        {
            tools["t" + index] = "y";
        }

        Assert.NotNull(RenameMap.Read(new JObject { ["args"] = arguments, ["tools"] = tools }, out string? fits));
        Assert.Null(fits);

        tools["t56"] = "y";
        Assert.Null(RenameMap.Read(new JObject { ["args"] = arguments, ["tools"] = tools }, out string? problem));
        Assert.Equal("rename holds more than 256 entries.", problem);
    }
}
