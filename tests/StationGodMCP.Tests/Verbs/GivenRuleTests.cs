#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// A rule that asks whether the caller GAVE an option (an exclusive pair, "X goes without Y", at-least-one-of, "only
/// with") must read what the caller gave, never the bound value: the generated options fill a catalogue default into
/// an option the call left out, so a default reads as given. These tests bind real JSON through the generated options
/// classes, as the verb handlers do, and check three things: a call that leaves every defaulted option out meets no
/// rule; the same rule still refuses when its options are given; and no file holds a rule the table does not know.
/// </summary>
public sealed class GivenRuleTests
{
    /// <summary>One rule family: where it lives, a call that must pass with defaults unfilled, and a call that must be refused.</summary>
    private sealed record Row(string File, Func<string, List<OptionProblem>> Check, string Left, string Given,
        string GivenPath);

    private static List<OptionProblem> Problems() => new List<OptionProblem>();

    private static OptionReader Reader(WhatSpec spec, string json) => new OptionReader(spec, JObject.Parse(json));

    // The rows: every rule family whose options include a catalogue default, driven through the real binder.
    private static readonly Row[] Rows =
    {
        new Row("ItemSpawnOrders.cs",
            json =>
            {
                BuildItemOptions o = BuildItemOptions.Bind(Reader(BuildItemOptions.Spec, json));
                List<OptionProblem> p = Problems();
                ItemSpawnOrder.Of(o.Selector.Prefab, o.Quantity, o.To, o.Slot, o.At, o.Items, p);
                return p;
            },
            """{"to":"5200100","items":[{"prefab":"ItemIronFrames","quantity":2},{"prefab":"ItemIronSheets"}]}""",
            """{"prefab":"ItemIronSheets","to":"5200100","quantity":3,"items":[{"prefab":"X"}]}""", "quantity"),
        new Row("HighlightOrders.cs",
            json =>
            {
                OptionReader r = Reader(ShowHighlightOptions.Spec, json);
                List<OptionProblem> p = Problems();
                HighlightOrder.Of(ShowHighlightOptions.Bind(r), r, p);
                return p;
            },
            """{"clear":true}""", """{"clear":true,"seconds":5}""", "seconds"),
        new Row("ProfilingOrders.cs",
            json =>
            {
                OptionReader r = Reader(ServerProfilingOptions.Spec, json);
                List<OptionProblem> p = Problems();
                ProfilingOrder.Of(ServerProfilingOptions.Bind(r), r, p);
                return p;
            },
            """{"action":"off"}""", """{"action":"off","slow_frame_ms":50}""", "slow_frame_ms"),
        new Row("ItemMoveOrders.cs",
            json =>
            {
                MoveItemOptions o = MoveItemOptions.Bind(Reader(MoveItemOptions.Spec, json));
                List<OptionProblem> p = Problems();
                SlotMoveOrders.Of(o, p, out _);
                return p;
            },
            """{"moves":[{"id":"5","to":"6"}]}""", """{"moves":[{"id":"5","to":"6"}],"id":"5"}""", "id"),
        new Row("ItemMoveOrders.cs",
            json =>
            {
                MoveItemOptions o = MoveItemOptions.Bind(Reader(MoveItemOptions.Spec, json));
                List<OptionProblem> p = Problems();
                StoreDepositOrder.Of(o, ItemMoveGiven.Of(o), p, out _);
                return p;
            },
            """{"items":[{"id":"5","quantity":1}],"to":"6"}""", """{"id":"5","items":[{"id":"5"}],"to":"6"}""",
            "items"),
        new Row("GasMoveOrders.cs",
            json =>
            {
                MoveGasOptions o = MoveGasOptions.Bind(Reader(MoveGasOptions.Spec, json));
                List<OptionProblem> p = Problems();
                GasMoveOrder.Of(o.Source, o.To, o.Delete == true, o.Gases, o.AmountMol,
                    Allow.Lifts(o.Allow, "burst"), o.Joined != false, p, o.TemperatureK);
                return p;
            },
            """{"source":"5","to":"6"}""", """{"source":"5","to":"6","delete":true}""", "to"),
        new Row("ForecastRequests.cs",
            json =>
            {
                PlanFlightOptions o = PlanFlightOptions.Bind(Reader(PlanFlightOptions.Spec, json));
                List<OptionProblem> p = Problems();
                ForecastRequest.Of(o, p);
                return p;
            },
            """{"id":"5"}""", """{"id":"5","transfer_mol":1,"transfer_with":"6"}""", "transfer_with"),
    };

    [Fact]
    public void ACallThatLeavesTheDefaultedOptionsOutMeetsNoRule()
    {
        foreach (Row row in Rows)
        {
            List<OptionProblem> problems = row.Check(row.Left);
            Assert.True(problems.Count == 0, $"{row.File}: {row.Left} was refused: " +
                string.Join("; ", problems.Select(problem => problem.Path + " " + problem.Problem)));
        }
    }

    [Fact]
    public void TheSameRuleStillRefusesWhenItsOptionsAreGiven()
    {
        foreach (Row row in Rows)
        {
            List<OptionProblem> problems = row.Check(row.Given);
            Assert.True(problems.Any(problem => problem.Path == row.GivenPath),
                $"{row.File}: {row.Given} was not refused at {row.GivenPath}");
        }
    }

    [Fact]
    public void AnAbsentOptionBindsAsAbsentWhereTheCatalogueGivesItADefault()
    {
        OptionReader reader = Reader(BuildItemOptions.Spec, """{"prefab":"ItemIronSheets","to":"5200100"}""");
        BuildItemOptions options = BuildItemOptions.Bind(reader);
        Assert.Null(options.Quantity);
        Assert.Null(options.Slot);
    }

    // ---- the rules no row drives: they read no option the catalogue defaults ----

    /// <summary>
    /// Rules by file (Pure/Verbs and Verbs): how many OptionProblem messages name a given-ness rule, and the options
    /// those rules read that the catalogue defaults (each with the row above that drives it, or why it is safe). A file
    /// with a new rule, or a rule count that moved, fails TheTableKnowsEveryRule, so the rule gets a row or a line here.
    /// </summary>
    private static readonly Dictionary<string, int> RuleCounts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["BatteryCharge.cs"] = 1,
        ["LogicReadRequest.cs"] = 6,
        ["FreeRectRequest.cs"] = 2,
        ["SampleRequest.cs"] = 3,
        ["ScreenPress.cs"] = 3,
        ["AimOrders.cs"] = 1,
        ["FabricateOrders.cs"] = 3,
        ["ForecastRequests.cs"] = 1,
        ["GasMoveOrders.cs"] = 6,
        ["GeneWrites.cs"] = 1,
        ["GhostOrders.cs"] = 1,
        ["HighlightOrders.cs"] = 1,
        ["ItemMoveOrders.cs"] = 12,
        ["ItemSpawnOrders.cs"] = 5,
        ["LabelWrites.cs"] = 4,
        ["LayoutChecks.cs"] = 2,
        ["LogicWrites.cs"] = 6,
        ["OptionCheck.cs"] = 1,
        ["PaintWrites.cs"] = 6,
        ["PlayerMoveOrders.cs"] = 3, // + stand with to_player / safe_ground: stand has no default, safe_ground reaches the order as `== true`
        ["PreviewOrders.cs"] = 1,
        ["ProfilingOrders.cs"] = 2,
        ["RouteOrders.cs"] = 4,
        ["RuleChecks.cs"] = 2,
        ["RunBuildOrders.cs"] = 1,
        ["Selector.cs"] = 5,
        ["SpotOrders.cs"] = 1,
        ["StructureBuildOrders.cs"] = 4,
        ["TradeOrders.cs"] = 1,
        ["BuildRuns.cs"] = 1,
        ["FindDamaged.cs"] = 2,
        ["FindItem.cs"] = 2,
        ["FindRoom.cs"] = 1,
        ["InspectGenes.cs"] = 1,
        ["InspectHealth.cs"] = 1,
        ["MoveItem.cs"] = 1,
        ["RemoveItem.cs"] = 1,
        ["RemoveStructure.cs"] = 2,
        ["SurveyArea.cs"] = 2,
        ["SurveyNetwork.cs"] = 4,
        ["SurveyWall.cs"] = 2,
        ["Undo.cs"] = 2,
        ["WriteBattery.cs"] = 1,
        ["WriteChip.cs"] = 2,
    };

    private static readonly Regex RuleWords = new Regex(
        @"goes with(out)?\b|goes with |needs\b|give one|at least one|not both|is needed|one of them|is for one|does not go with|go together",
        RegexOptions.Compiled);

    private static string SourceRoot() => Path.Combine(CatalogueFiles.RepositoryRoot(), "src", "StationGodMCP.Mod");

    private static IEnumerable<string> RuleFiles() =>
        new[] { "Pure", "Verbs" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(SourceRoot(), dir), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "Generated" + Path.DirectorySeparatorChar));

    private static Dictionary<string, int> Counted()
    {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string path in RuleFiles())
        {
            int n = Regex.Matches(File.ReadAllText(path), @"new OptionProblem\((?:[^;]*?)\);", RegexOptions.Singleline)
                .Count(m => RuleWords.IsMatch(m.Value));
            if (n > 0)
            {
                counts[Path.GetFileName(path)] = n;
            }
        }

        return counts;
    }

    [Fact]
    public void TheTableKnowsEveryRule()
    {
        Dictionary<string, int> found = Counted();
        List<string> moved = found
            .Where(pair => !RuleCounts.TryGetValue(pair.Key, out int known) || known != pair.Value)
            .Select(pair => $"{pair.Key}: {pair.Value} rules, table says {(RuleCounts.TryGetValue(pair.Key, out int k) ? k : 0)}")
            .Concat(RuleCounts.Keys.Where(key => !found.ContainsKey(key)).Select(key => key + ": no rules now"))
            .ToList();
        Assert.True(moved.Count == 0,
            "rules changed: give each a row in Rows (or a RuleCounts line saying why it reads no defaulted option): " +
            string.Join(" | ", moved));
    }

    // A defaulted option's bound value reads as given when a rule asks "is it there": a null test, HasValue, a type
    // pattern or a comparison with the default itself. A rule file may not do that to a property its options class
    // fills from a catalogue default (the `?? options.Default...` lines of Generated/*Options.cs).
    private static IEnumerable<(string Class, string Property, string? Flag)> BoundDefaults()
    {
        string generated = Path.Combine(SourceRoot(), "Pure", "Verbs", "Generated");
        Regex bound = new Regex(@"(?<prop>[A-Z]\w*) = options\.\w+\(""(?<name>\w+)""\) \?\? options\.Default", RegexOptions.Compiled);
        foreach (WhatSpec what in VerbSurface.Verbs.SelectMany(verb => verb.Whats))
        {
            string type = string.Concat(Pascal(what.Verb), what.Name == VerbSurface.WhatlessKey ? "" : Pascal(what.Name), "Options");
            string path = Path.Combine(generated, type + ".cs");
            Assert.True(File.Exists(path), "no generated options class " + type);
            foreach (Match match in bound.Matches(File.ReadAllText(path)))
            {
                yield return (type, match.Groups["prop"].Value,
                    what.Option(match.Groups["name"].Value)!.DefaultValue!.Flag?.ToString().ToLowerInvariant());
            }
        }
    }

    private static string Pascal(string words) =>
        string.Concat(words.Split('_').Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1)));

    [Fact]
    public void NoRuleFileTestsADefaultedPropertyForPresence()
    {
        List<(string Class, string Property, string? Flag)> defaulted = BoundDefaults().ToList();
        Assert.NotEmpty(defaulted);
        List<string> hits = new List<string>();
        foreach (string path in RuleFiles().Where(p => Counted().ContainsKey(Path.GetFileName(p))))
        {
            string text = File.ReadAllText(path);
            string[] lines = text.Split('\n');
            foreach ((string type, string property, string? flag) in defaulted.Where(d => text.Contains(d.Class)))
            {
                string pattern = $@"\boptions\.{property}\s*(!= null|== null|is null|is not null|\.HasValue|is \{{|is not \{{)";
                if (flag != null)
                {
                    pattern += $@"|\boptions\.{property}\s*== {flag}\b";
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    if (Regex.IsMatch(lines[i], pattern))
                    {
                        hits.Add($"{Path.GetFileName(path)}:{i + 1} {type}.{property}: {lines[i].Trim()}");
                    }
                }
            }
        }

        Assert.True(hits.Count == 0, "a defaulted option read as given: " + string.Join(" | ", hits));
    }
}
