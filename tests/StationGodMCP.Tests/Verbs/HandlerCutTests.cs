#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StationGodMCP.Pure.Shaping;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>A list a handler notes as cut: the list as the note writes it, and where the source writes it.</summary>
internal sealed record WrittenCut(string? Verb, string? What, string List, string At)
{
    internal string Describe() =>
        (Verb == null ? "list " : Verb + " " + What + " list ") + "'" + List + "' (" + At + ")";
}

/// <summary>
/// Every list a handler cuts is stated by the catalogue (x-default-limits, a limit option on the what's main list, or
/// x-handler-cuts), so cuts.json lists every list a reply can cut.
/// </summary>
public sealed class HandlerCutTests
{
    private static readonly Regex NoteCall = new(
        @"Truncations\.(?:Note|Capped|Page)\(\s*(?<list>[^,]+?)\s*,", RegexOptions.Compiled);

    private static readonly Regex Constant = new(
        @"const\s+string\s+(?<name>\w+)\s*=\s*""(?<value>[^""]*)""", RegexOptions.Compiled);

    /// <summary>The written cuts that no stated row covers: the row's path is the list, or ends with it after a dot.</summary>
    internal static List<WrittenCut> Unstated(IEnumerable<WrittenCut> written, IReadOnlyList<CutRow> rows) =>
        written.Where(cut => !rows.Any(row =>
                    (cut.Verb == null || (row.Verb == cut.Verb && row.What == cut.What))
                    && (row.Path == cut.List || row.Path.EndsWith("." + cut.List, StringComparison.Ordinal))))
            .ToList();

    [Fact]
    public void EveryListAHandlerNotesAsCutIsStatedInTheCatalogue()
    {
        List<WrittenCut> missing = Unstated(WrittenInSource(), VerbCuts.Collect());

        Assert.True(missing.Count == 0,
            "a handler notes a cut no what states (add it to the what's x-handler-cuts in catalogue/verbs, then " +
            "STATIONGOD_WRITE_CATALOGUE=1 and GeneratedFilesAreCurrent): " +
            string.Join("; ", missing.Select(cut => cut.Describe())));
    }

    [Fact]
    public void AHandlerCutNobodyStatedFailsNamingVerbWhatAndList()
    {
        List<CutRow> rows = VerbCuts.Collect();
        WrittenCut planted = new("inspect", "slots", "stowed_things", "planted");
        WrittenCut stated = new("inspect", "slots", "slots", "planted");

        List<WrittenCut> missing = Unstated(new[] { planted, stated }, rows);

        WrittenCut only = Assert.Single(missing);
        Assert.Equal("inspect slots list 'stowed_things' (planted)", only.Describe());
    }

    [Fact]
    public void ACutIsStatedByOneSourceOnly()
    {
        Assert.Empty(VerbCuts.StatedTwice(VerbCuts.Collect()));
    }

    [Fact]
    public void InspectSlotsRowStatesItsCapOf32AndThatListLimitsLiftsIt()
    {
        CutRow row = Assert.Single(VerbCuts.Collect(), r => r.Verb == "inspect" && r.What == "slots");

        Assert.Equal(new CutRow("inspect", "slots", "slots", 32, "list_limits.slots"), row);
    }

    [Fact]
    public void ASuitsTwentyOneSlotsComeWholeWithNoCutNote()
    {
        string json = Slots(21);

        Assert.DoesNotContain("\"list\":\"slots\"", json, StringComparison.Ordinal);
        Assert.Contains("\"count\":21", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FortySlotsAreCutAt32WithTheNote()
    {
        string json = Slots(40);

        Assert.Contains("\"list\":\"slots\",\"returned\":32,\"total\":40", json, StringComparison.Ordinal);
    }

    private static string Slots(int count)
    {
        ShapeRequest shape = ShapeRequest.WithDefaultLimits(null, InspectSlotsOptions.Spec.DefaultLimits)!;
        string slots = string.Join(",", Enumerable.Range(0, count));
        return ShapingChecks.Mod(ShapingChecks.Parse("{\"slots\":[" + slots + "],\"count\":" + count + "}"), shape).Json;
    }

    private static IEnumerable<WrittenCut> WrittenInSource()
    {
        string source = Path.Combine(CatalogueFiles.RepositoryRoot(), "src", "StationGodMCP.Mod");
        List<(string File, string Text)> files = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
            .Select(file => (file, File.ReadAllText(file)))
            .ToList();
        Dictionary<string, string> constants = new();
        foreach ((string _, string text) in files)
        {
            foreach (Match match in Constant.Matches(text))
            {
                constants[match.Groups["name"].Value] = match.Groups["value"].Value;
            }
        }

        foreach ((string file, string text) in files)
        {
            foreach (Match call in NoteCall.Matches(text))
            {
                string at = Path.GetFileName(file) + ":" +
                            (text.Take(call.Index).Count(c => c == '\n') + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string list = ListOf(call.Groups["list"].Value, constants, at);
                if (list != "list")
                {
                    yield return new WrittenCut(null, null, list, at);
                }
            }
        }
    }

    // The list as the note writes it after any path prefix: the last string literal, else a constant's value.
    private static string ListOf(string argument, Dictionary<string, string> constants, string at)
    {
        if (argument == "list")
        {
            return argument;
        }

        Match literal = Regex.Matches(argument, "\"([^\"]*)\"").Cast<Match>().LastOrDefault()!;
        if (literal != null)
        {
            return literal.Groups[1].Value;
        }

        string name = argument.Split('.', '+', ' ').Last(part => part.Length > 0);
        Assert.True(constants.TryGetValue(name, out string? value), "cannot resolve the list '" + argument + "' at " + at);
        return value!;
    }
}
