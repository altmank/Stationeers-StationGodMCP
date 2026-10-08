#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>A lint world built from JSON records: sets of subjects, cells by position, planned things.</summary>
internal sealed class TestLintWorld : ILintWorld
{
    private readonly Dictionary<string, List<ILintObject>> _sets = new Dictionary<string, List<ILintObject>>(StringComparer.Ordinal);

    internal TestLintWorld(ILintObject? world = null)
    {
        World = world ?? new LintRecord(LintModel.World, "world", new Dictionary<string, LintValue>(StringComparer.Ordinal));
        foreach (string set in LintSets.All)
        {
            _sets[set] = new List<ILintObject>();
        }
    }

    public ILintObject World { get; }

    internal TestLintWorld Add(string set, ILintObject subject)
    {
        _sets[set].Add(subject);
        if (set == "pieces" || set == "devices" || set == "structures")
        {
            _sets["things"].Add(subject);
        }

        return this;
    }

    public IReadOnlyList<ILintObject> Subjects(string set) => _sets[set];

    public ILintObject? CellAt(Vec3 point, double size)
    {
        foreach (ILintObject cell in _sets["cells"])
        {
            if (cell.Position is Vec3 at && (at - point).Length < 0.01)
            {
                return cell;
            }
        }

        return null;
    }

    public bool IsPlanned(ILintObject subject)
    {
        LintField? planned = subject.Type.Find("planned");
        return planned != null && subject.Get(planned) is LintValue value && value.Kind == LintKind.Bool && value.AsBool;
    }
}

/// <summary>Shared helpers: the library with the game's functions stood in for, the shipped rules, JSON records.</summary>
internal static class LintTestKit
{
    internal static LintLibrary Library()
    {
        LintLibrary library = LintLibrary.Standard();
        library
            .Add(new LintFunction("replaceable", "(x: thing) -> placement", "test", _ => throw new LintEvaluationException("no game in tests"), cached: true))
            .Add(new LintFunction("controls_side", "(x: thing) -> controls?", "test", _ => LintValue.Null))
            .Add(new LintFunction("controls_blocked", "(x: thing) -> string?", "test", _ => LintValue.Null))
            .Add(new LintFunction("clips_surface", "(x: thing) -> string?", "test", _ => LintValue.Null))
            .Add(new LintFunction("port_stub", "(x: thing) -> bool", "test", _ => LintValue.False))
            .Add(new LintFunction("sun_blocked", "(x: thing) -> bool", "test", _ => LintValue.False, cached: true))
            .Add(new LintFunction("weather_exposed", "(x: thing) -> bool", "test",
                call => call[0].AsObject.Get(LintModel.Thing["outdoors"])))
            .Add(new LintFunction("logic", "(x: thing, type: string) -> number?", "test", _ => LintValue.Null))
            .Add(new LintFunction("freezing_point", "(gas: string) -> number?", "test",
                call => call[0].AsString == "Water" ? LintValue.Of(273.15) : call[0].AsString == "Oxygen" ? LintValue.Of(56.4) : LintValue.Null))
            .Add(new LintFunction("auto_ignition_temperature", "(gases: map<number>) -> number?", "test", AutoIgnition))
            .Add(new LintFunction("drill_column", "(x: thing) -> list<cell>", "test", _ => LintValue.EmptyList));
        return library;
    }

    // GasMixture.IsAutoIgnition's thresholds, hydrazine's critical temperature rounded.
    private static LintValue AutoIgnition(LintCall call)
    {
        IReadOnlyDictionary<string, LintValue> gases = call[0].AsMap;
        double Mol(string name) => gases.TryGetValue(name, out LintValue mol) ? mol.AsNumber : 0;
        double offset = Math.Min(Mol("NitrousOxide") + Mol("LiquidNitrousOxide") > 1 ? -250 : 0,
            Mol("Ozone") + Mol("LiquidOzone") > 1 ? -150 : 0);
        double? limit = null;
        if (Mol("Methane") + Mol("LiquidMethane") > 1 || Mol("Hydrogen") + Mol("LiquidHydrogen") > 1)
        {
            limit = 573.15 + offset;
        }

        if (Mol("LiquidAlcohol") > 1)
        {
            limit = Math.Min(limit ?? double.MaxValue, 673.15 + offset);
        }

        if (Mol("Hydrazine") + Mol("LiquidHydrazine") > 1)
        {
            limit = Math.Min(limit ?? double.MaxValue, 520.8);
        }

        return LintValue.Of(limit);
    }

    internal static string DefaultText() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "lint-rules.json"));

    internal static LintRuleSet Defaults(LintLibrary? library = null, string? save = null) =>
        LintRuleLoader.Load(library ?? Library(), new LintRuleText("mod/lint-rules.json", DefaultText(), LintRuleOrigin.Mod),
            save != null ? new LintRuleText("save/lint-rules.json", save, LintRuleOrigin.Save) : null);

    internal static LintRuleSet Rules(string json, LintLibrary? library = null) =>
        LintRuleLoader.Load(library ?? Library(), new LintRuleText("test.json", json, LintRuleOrigin.Mod), null);

    internal static LintRecord Thing(string json) => LintExamples.Record(LintModel.Thing, JObject.Parse(json), Library(), "thing");

    internal static LintRecord Of(ObjectType type, string json) => LintExamples.Record(type, JObject.Parse(json), Library(), type.Name);

    internal static string File1(string rules) => "{\"schema_version\": 1, \"rules\": [" + rules + "]}";
}

/// <summary>The rule file format, the default rules, overrides, examples, errors with positions.</summary>
public sealed class LintRuleFileTests
{
    [Fact]
    public void TheShippedRulesLoadWithoutErrors()
    {
        LintRuleSet set = LintTestKit.Defaults();
        Assert.Empty(set.Errors);
        Assert.Equal(25, set.Rules.Count);
    }

    [Fact]
    public void TheShippedRulesKeepEveryBuiltInRuleItsLevelAndOrder()
    {
        LintRuleSet set = LintTestKit.Defaults();
        int last = -1;
        foreach ((string code, ConflictLevel level) in LintCodes.Rules)
        {
            LintRule? rule = set.Find(code);
            Assert.NotNull(rule);
            Assert.Equal(level, rule!.Level);
            Assert.True(set.IndexOf(code) > last, $"{code} is out of order");
            last = set.IndexOf(code);
        }
    }

    [Theory]
    [InlineData("solar_outdoor_reinforced", "problem")]
    [InlineData("no_oxidiser_vented_outdoors", "problem")]
    [InlineData("cable_grade_matches_network", "warning")]
    [InlineData("controller_labels", "warning")]
    [InlineData("solar_unshaded", "warning")]
    [InlineData("filter_output_capped", "warning")]
    [InlineData("fuel_oxidiser_mix_temperature", "warning")]
    [InlineData("outdoor_liquid_insulated", "warning")]
    [InlineData("deep_miner_column_clear", "warning")]
    [InlineData("cable_on_frames", "warning")]
    public void TheGeneralRulesShipAtTheirLevels(string id, string level)
    {
        Assert.Equal(level, LintRule.NameOf(LintTestKit.Defaults().Find(id)!.Level));
    }

    [Fact]
    public void EveryShippedRuleHasExamplesAndTheyHold()
    {
        LintLibrary library = LintTestKit.Library();
        foreach (LintRule rule in LintTestKit.Defaults(library).Rules)
        {
            List<LintExampleResult> results = LintExamples.Run(rule, library);
            Assert.Contains(results, result => result.Expected == "pass");
            Assert.Contains(results, result => result.Expected == "fail");
            foreach (LintExampleResult result in results)
            {
                Assert.True(result.Ok,
                    $"{rule.Id} {result.Expected}[{result.Index}] was {result.Actual}: {result.Error ?? result.Message}");
            }
        }
    }

    [Fact]
    public void AFailingExampleRendersTheMessage()
    {
        LintLibrary library = LintTestKit.Library();
        LintRule rule = LintTestKit.Defaults(library).Find("floating_run")!;
        LintExampleResult fail = LintExamples.Run(rule, library).Find(result => result.Expected == "fail")!;
        Assert.Equal("StructurePipeStraight 3 floats in air (1 of 2 cells on no frame and no wall plane).", fail.Message);
    }

    [Fact]
    public void TheSavesFileDisablesChangesLevelReplacesAndAdds()
    {
        string save = LintTestKit.File1(
            "{\"id\": \"floating_run\", \"enabled\": false}," +
            "{\"id\": \"run_along_door\", \"level\": \"warning\"}," +
            "{\"id\": \"cable_on_frames\", \"level\": \"info\", \"select\": \"pieces where kind == 'cable'\", \"assert\": \"true\", \"message\": \"m\"}," +
            "{\"id\": \"my_rule\", \"level\": \"info\", \"select\": \"devices\", \"assert\": \"has(room)\", \"message\": \"{prefab} outdoors\"}");
        LintRuleSet set = LintTestKit.Defaults(save: save);
        Assert.Empty(set.Errors);
        Assert.Null(set.Find("floating_run"));
        Assert.Contains("floating_run", set.Disabled);
        Assert.Equal(ConflictLevel.Warning, set.Find("run_along_door")!.Level);
        Assert.Equal(LintRuleOrigin.Mod, set.Find("run_along_door")!.Origin);
        Assert.Equal(LintRuleOrigin.Save, set.Find("cable_on_frames")!.Origin);
        Assert.Equal("true", set.Find("cable_on_frames")!.AssertText);
        Assert.Equal(set.Rules.Count - 1, set.IndexOf("my_rule"));
        Assert.Equal(new[] { "mod/lint-rules.json", "save/lint-rules.json" }, set.Files);
    }

    [Fact]
    public void APatchForAnUnknownRuleIsAnError()
    {
        LintRuleSet set = LintTestKit.Defaults(save: LintTestKit.File1("{\"id\": \"nope\", \"level\": \"info\"}"));
        LintRuleError error = Assert.Single(set.Errors);
        Assert.Equal("save/lint-rules.json", error.File);
        Assert.Equal("nope", error.RuleId);
    }

    [Fact]
    public void ExpressionErrorsNameTheRuleFieldLineAndCharacter()
    {
        string json = "{\n  \"schema_version\": 1,\n  \"rules\": [\n    {\"id\": \"r\", \"level\": \"info\", \"select\": \"devices\",\n     \"assert\": \"room.id > 0\", \"message\": \"m\"}\n  ]\n}";
        LintRuleSet set = LintTestKit.Rules(json);
        LintRuleError error = Assert.Single(set.Errors);
        Assert.Equal("r", error.RuleId);
        Assert.Equal("assert", error.Field);
        Assert.Equal(5, error.Line);
        Assert.Equal(1, error.Position);
        Assert.Contains("room may be null", error.Message);
        Assert.Empty(set.Rules);
    }

    [Theory]
    [InlineData("devices", "nope > 1", "no name or thing field 'nope'")]
    [InlineData("devices", "frobnicate(x)", "no function 'frobnicate'")]
    [InlineData("devices", "prefab + 1 > 2", "is a string, not a number")]
    [InlineData("devices", "count(cells) > 'a'", "is a string, not a number")]
    [InlineData("devices", "any(cells, c => c.nope)", "cell has no field 'nope'")]
    [InlineData("pairs(devices, within: 1)", "prefab == 'x'", "no name 'prefab' (pair rules read a and b)")]
    [InlineData("gizmos", "true", "no set gizmos")]
    [InlineData("devices", "kind == ", "expected a value")]
    [InlineData("devices", "prefab matches_regex '('", "bad pattern")]
    [InlineData("devices", "network.id > 0", "network may be null")]
    public void BadExpressionsAreRefusedAtLoad(string select, string assert, string expected)
    {
        LintRuleSet set = LintTestKit.Rules(LintTestKit.File1(
            $"{{\"id\": \"r\", \"level\": \"info\", \"select\": {Quote(select)}, \"assert\": {Quote(assert)}, \"message\": \"m\"}}"));
        Assert.Contains(set.Errors, error => error.Message.Contains(expected));
    }

    [Theory]
    [InlineData("{\"schema_version\": 2, \"rules\": []}", "schema_version must be 1")]
    [InlineData("{\"schema_version\": 1, \"rules\": [ {\"id\": \"Bad Id\"} ]}", "id must be")]
    [InlineData("{\"schema_version\": 1, \"rules\": [ {\"id\": \"r\", \"level\": \"loud\", \"select\": \"devices\", \"assert\": \"true\", \"message\": \"m\"} ]}", "level is problem")]
    [InlineData("{\"schema_version\": 1, \"rules\": [ {\"id\": \"r\", \"level\": \"info\", \"select\": \"devices\", \"assert\": \"true\", \"message\": \"m\", \"colour\": 1} ]}", "unknown field colour")]
    [InlineData("{\"schema_version\": 1, \"rules\": [ {\"id\": \"r\", \"level\": \"info\", \"select\": \"devices\", \"assert\": \"true\", \"message\": \"{nope}\"} ]}", "no name or thing field 'nope'")]
    [InlineData("{\"schema_version\": 1, \"rules\": [ {\"id\": \"r\", \"level\": \"info\", \"select\": \"devices\", \"assert\": \"1\", \"message\": \"m\"} ]}", "is a number, not a bool")]
    [InlineData("{\"schema_version\": 1, \"rules\": [ {\"id\": \"r\", \"level\": \"info\", \"on\": [\"later\"], \"select\": \"devices\", \"assert\": \"true\", \"message\": \"m\"} ]}", "on lists audit")]
    [InlineData("{\"schema_version\": 1, \"rules\": [", "not valid JSON")]
    public void BadFilesAreReported(string json, string expected)
    {
        Assert.Contains(LintTestKit.Rules(json).Errors, error => error.Message.Contains(expected));
    }

    private static string Quote(string text) => Newtonsoft.Json.JsonConvert.ToString(text);
}

/// <summary>The expression language: types, nulls, collections, text, narrowing.</summary>
public sealed class LintExpressionTests
{
    private static LintValue Eval(string expression, string thing = "{\"prefab\": \"StructureConsole\", \"reference_id\": 5}")
    {
        LintCompiler compiler = new LintCompiler(LintTestKit.Library());
        int x = compiler.NewSlot();
        LintScope scope = new LintScope(null, x, LintModel.Thing).With("x", x, LintModel.Thing);
        LintCompiled compiled = compiler.Compile(LintParser.Expression(expression), scope);
        LintFrame frame = new LintFrame(new LintContext(new TestLintWorld()), compiler.SlotCount, compiler.LazySlots);
        frame.Set(x, LintValue.Of(LintTestKit.Thing(thing)));
        return compiled.Eval(frame);
    }

    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("-2 + 5 % 3", "0")]
    [InlineData("'a' + \"b\"", "ab")]
    [InlineData("prefab matches 'structure*'", "true")]
    [InlineData("prefab matches 'Structure?onsole'", "true")]
    [InlineData("prefab matches_regex '^Struct.*e$'", "true")]
    [InlineData("'Con' in prefab", "true")]
    [InlineData("3 in [1, 2, 3]", "true")]
    [InlineData("3 not in [1, 2]", "true")]
    [InlineData("count([1, 2, 3], n => n > 1)", "2")]
    [InlineData("sum([1, 2, 3])", "6")]
    [InlineData("sum([1, 2, 3], n => n * n)", "14")]
    [InlineData("min([4, 2, 3])", "2")]
    [InlineData("max([4, 2, 3], n => -n)", "-2")]
    [InlineData("max(1, 7, 3)", "7")]
    [InlineData("min([], n => n) ?? -1", "-1")]
    [InlineData("first([4, 2, 3], n => n < 4)", "2")]
    [InlineData("first([], n => true) ?? 9", "9")]
    [InlineData("map([1, 2], n => n * 10)", "[10, 20]")]
    [InlineData("filter([1, 2, 3], n => n != 2)", "[1, 3]")]
    [InlineData("flat_map([1, 2], n => [n, n])", "[1, 1, 2, 2]")]
    [InlineData("sort_by([3, 1, 2], n => n)", "[1, 2, 3]")]
    [InlineData("distinct(['a', 'b', 'a'])", "[a, b]")]
    [InlineData("[1] + [2, 3]", "[1, 2, 3]")]
    [InlineData("all([], n => false)", "true")]
    [InlineData("any([1, 2], n => all([1, 2], m => m <= n))", "true")]
    [InlineData("let k = 4 in k * k", "16")]
    [InlineData("if(reference_id > 3, 'big', 'small')", "big")]
    [InlineData("label ?? 'none'", "none")]
    [InlineData("room?.id ?? 0", "0")]
    [InlineData("has(room) and room.id > 0", "false")]
    [InlineData("not has(room) or room.id > 0", "true")]
    [InlineData("if(has(room), room.id, -1)", "-1")]
    [InlineData("format(1.23456, '0.00')", "1.23")]
    [InlineData("join(['a', 'b'], '-')", "a-b")]
    [InlineData("trim_end('x...', '.')", "x")]
    [InlineData("hash('StructureSolarPanel')", "-2045627372")]
    [InlineData("is_infinite(1 / 0)", "true")]
    [InlineData("x.prefab == prefab", "true")]
    [InlineData("str(position)", "(1, 2, 3)")]
    [InlineData("position.y + 1", "3")]
    [InlineData("room.id", null)]
    public void ExpressionsEvaluate(string expression, string? expected)
    {
        if (expected == null)
        {
            Assert.Throws<LintSyntaxException>(() => Eval(expression));
            return;
        }

        Assert.Equal(expected, Eval(expression,
            "{\"prefab\": \"StructureConsole\", \"reference_id\": 5, \"position\": [1, 2, 3]}").ToText());
    }

    [Fact]
    public void ANullReadAtRunTimeIsAnEvaluationErrorNotACrash()
    {
        // The model says rotation may be null; a record that omits a non-null field fails only when read.
        Assert.Throws<LintEvaluationException>(() => Eval("kind == 'x'"));
    }

    [Fact]
    public void ExplainRecordsTheValuesRead()
    {
        LintRuleSet set = LintTestKit.Rules(LintTestKit.File1(
            "{\"id\": \"r\", \"level\": \"info\", \"select\": \"pieces\", \"let\": {\"air\": \"count(cells, c => c.support == 'air')\"}, \"assert\": \"air == 0\", \"message\": \"{air}\"}"));
        LintRule rule = Assert.Single(set.Rules);
        LintRecord piece = LintTestKit.Thing("{\"reference_id\": 1, \"cells\": [{\"support\": \"air\"}, {\"support\": \"frame_face\"}]}");
        LintVerdict verdict = LintEngine.Judge(rule, new LintSubjectRef(piece), new LintContext(new TestLintWorld()), true, true);
        Assert.Equal(LintOutcome.Failed, verdict.Outcome);
        Assert.Equal("1", verdict.Message);
        Assert.Contains("air", verdict.Trace!.Order);
        Assert.Contains("c.support", verdict.Trace.Order);
        Assert.Equal(1.0, verdict.Trace.ValuesOf("air")[0].AsNumber);
    }
}

/// <summary>Chip programs: the batch operations IC10 and Lua chips make, HASH.</summary>
public sealed class LintChipProgramTests
{
    [Fact]
    public void HashIsTheGamesCrc32()
    {
        Assert.Equal(-2045627372, LintChipPrograms.HashOf("StructureSolarPanel"));
        Assert.Equal(1675498291, LintChipPrograms.HashOf("Tracker"));
    }

    [Fact]
    public void Ic10BatchOperationsResolveDefinesAndHashes()
    {
        string source = "define Panel HASH(\"StructureSolarPanel\")\n" +
                        "define Name HASH(\"Solar Tracker\") # the trackers\n" +
                        "alias Sensor d0\n" +
                        "lbn r0 Panel Name Vertical Average\n" +
                        "sbn Panel Name Horizontal r1\n" +
                        "sb HASH(\"StructureGasSensor\") On 1\n" +
                        "s Sensor On 1\n" +
                        "sd 123 Setting r2\n" +
                        "lb r3 -2045627372 Ratio Sum\n";
        List<ChipOperation> ops = LintChipPrograms.Ic10(source);
        Assert.Equal(6, ops.Count);
        Assert.Equal("lbn", ops[0].Op);
        Assert.Equal(-2045627372, ops[0].PrefabHash);
        Assert.Equal("StructureSolarPanel", ops[0].Prefab);
        Assert.Equal("Solar Tracker", ops[0].Name);
        Assert.Equal(LintChipPrograms.HashOf("Solar Tracker"), ops[0].NameHash);
        Assert.Equal("Vertical", ops[0].Logic);
        Assert.False(ops[0].Writes);
        Assert.True(ops[1].Writes);
        Assert.Equal("Horizontal", ops[1].Logic);
        Assert.Null(ops[2].NameHash);
        Assert.Equal(0, ops[3].Pin);
        Assert.Equal("On", ops[3].Logic);
        Assert.Equal(123L, ops[4].ReferenceId);
        Assert.Equal(-2045627372, ops[5].PrefabHash);
        Assert.Null(ops[5].Prefab);
    }

    [Fact]
    public void LuaBatchCallsAreRead()
    {
        string source = "local PANEL = hash(\"StructureSolarPanel\")\n" +
                        "ic.batch_write_name(hash(\"StructureSolarPanel\"), hash(\"Tracker\"), LogicType.Horizontal, h)\n" +
                        "local v = batch_read_slot_name(-2045627372, ic.hash('Tracker'), 0, 'Occupied', 0)\n" +
                        "batch_write(hash(\"StructureGasSensor\"), \"On\", 1)";
        List<ChipOperation> ops = LintChipPrograms.Lua(source);
        Assert.Equal(3, ops.Count);
        Assert.Equal("batch_write_name", ops[0].Op);
        Assert.Equal("Tracker", ops[0].Name);
        Assert.Equal("Horizontal", ops[0].Logic);
        Assert.True(ops[0].Writes);
        Assert.Equal("batch_read_slot_name", ops[1].Op);
        Assert.Equal(LintChipPrograms.HashOf("Tracker"), ops[1].NameHash);
        Assert.Equal("Occupied", ops[1].Logic);
        Assert.Equal("On", ops[2].Logic);
        Assert.Null(ops[2].NameHash);
    }
}

/// <summary>docs/lint-rules.md names every field of the model and every library function and form.</summary>
public sealed class LintDocsTests
{
    private static string Docs() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "lint-rules.md"));

    [Fact]
    public void EveryFieldIsDocumented()
    {
        string docs = Docs();
        foreach (ObjectType type in LintModel.Types)
        {
            foreach (LintField field in type.Fields)
            {
                Assert.True(docs.Contains($"`{field.Name}`") || docs.Contains($"`{field.Name},"),
                    $"{type.Name}.{field.Name} is not in docs/lint-rules.md");
            }
        }
    }

    [Fact]
    public void EveryFunctionAndFormIsDocumented()
    {
        string docs = Docs();
        foreach (LintFunction function in LintTestKit.Library().Functions)
        {
            Assert.True(docs.Contains($"`{function.Name}(") || docs.Contains($"`{function.Name}`"),
                $"{function.Name} is not in docs/lint-rules.md");
        }

        foreach (string form in LintCompiler.FormNames)
        {
            Assert.Contains($"`{form}`", docs);
        }
    }

    [Fact]
    public void EveryShippedRuleIsDocumented()
    {
        string docs = Docs();
        foreach (LintRule rule in LintTestKit.Defaults().Rules)
        {
            Assert.Contains($"| `{rule.Id}` | {LintRule.NameOf(rule.Level)}", docs);
        }
    }
}
