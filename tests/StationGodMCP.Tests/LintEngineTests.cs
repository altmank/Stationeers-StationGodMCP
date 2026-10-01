#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Objects with links in both directions (a network's devices, a device's ports' networks), built by hand.</summary>
internal sealed class LintGraph
{
    private readonly Dictionary<string, Dictionary<string, LintValue>> _values =
        new Dictionary<string, Dictionary<string, LintValue>>(StringComparer.Ordinal);

    internal Dictionary<string, LintRecord> Objects { get; } = new Dictionary<string, LintRecord>(StringComparer.Ordinal);

    internal LintRecord Make(ObjectType type, string key, params (string Field, object? Value)[] fields)
    {
        Dictionary<string, LintValue> values = new Dictionary<string, LintValue>(StringComparer.Ordinal);
        LintRecord record = new LintRecord(type, key, values, describe: key);
        _values[key] = values;
        Objects[key] = record;
        Set(key, fields);
        return record;
    }

    internal void Set(string key, params (string Field, object? Value)[] fields)
    {
        foreach ((string field, object? value) in fields)
        {
            _values[key][field] = ValueOf(value);
        }
    }

    private static LintValue ValueOf(object? value) => value switch
    {
        null => LintValue.Null,
        LintValue v => v,
        bool b => LintValue.Of(b),
        int i => LintValue.Of(i),
        double d => LintValue.Of(d),
        string s => LintValue.Of(s),
        Vec3 v => LintValue.Of(v),
        ILintObject o => LintValue.Of(o),
        IEnumerable<ILintObject> list => LintValue.Of(new List<LintValue>(Each(list))),
        IEnumerable<string> list => LintValue.Strings(new List<string>(list)),
        Dictionary<string, double> map => Map(map),
        _ => throw new ArgumentException(value.GetType().Name)
    };

    private static IEnumerable<LintValue> Each(IEnumerable<ILintObject> list)
    {
        foreach (ILintObject item in list)
        {
            yield return LintValue.Of(item);
        }
    }

    private static LintValue Map(Dictionary<string, double> map)
    {
        Dictionary<string, LintValue> values = new Dictionary<string, LintValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, double> entry in map)
        {
            values[entry.Key] = LintValue.Of(entry.Value);
        }

        return LintValue.Of(values);
    }
}

/// <summary>The engine over a world: findings, pairs, rule errors, dry runs, upstream and downstream.</summary>
public sealed class LintEngineTests
{
    private static LintRecord Piece(int id, string kind, params string[] supports)
    {
        List<LintValue> cells = new List<LintValue>();
        for (int index = 0; index < supports.Length; index++)
        {
            cells.Add(LintValue.Of(new LintRecord(LintModel.Cell, $"cell:{id}/{index}",
                new Dictionary<string, LintValue>(StringComparer.Ordinal)
                {
                    ["support"] = LintValue.Of(supports[index]),
                    ["in_door_keepout"] = LintValue.False,
                    ["on_window_face"] = LintValue.False
                })));
        }

        return new LintRecord(LintModel.Thing, $"thing:{id}", new Dictionary<string, LintValue>(StringComparer.Ordinal)
        {
            ["reference_id"] = LintValue.Of(id),
            ["prefab"] = LintValue.Of(kind == "cable" ? "StructureCableStraight" : "StructurePipeStraight"),
            ["kind"] = LintValue.Of(kind),
            ["position"] = LintValue.Of(new Vec3(id, 0, 0)),
            ["cells"] = LintValue.Of(cells),
            ["planned"] = LintValue.Of(id >= 100)
        }, describe: $"piece {id}");
    }

    private static LintRuleSet Only(params string[] ids)
    {
        LintRuleSet all = LintTestKit.Defaults();
        List<LintRule> rules = new List<LintRule>();
        foreach (string id in ids)
        {
            rules.Add(all.Find(id)!);
        }

        return new LintRuleSet(rules, new List<LintRuleError>(), new List<string>(), new List<string>());
    }

    [Fact]
    public void AFloatingRunIsFoundWithTheRulesMessageLevelAndOrder()
    {
        TestLintWorld world = new TestLintWorld()
            .Add("pieces", Piece(1, "cable", "frame_face"))
            .Add("pieces", Piece(2, "pipe", "air", "wall_plane"));
        LintRun run = LintEngine.Run(Only("floating_run", "cable_on_frames"), world, "audit");
        Assert.Equal(2, run.Subjects["floating_run"]);
        LintFinding floating = Assert.Single(run.Findings, finding => finding.Code == "floating_run");
        Assert.Equal(2L, floating.ThingId);
        Assert.Equal(ConflictLevel.Warning, floating.Level);
        Assert.Equal("StructurePipeStraight 2 floats in air (1 of 2 cells on no frame and no wall plane).", floating.Message);
        Assert.Equal(new Vec3(2, 0, 0), floating.At);
        Assert.Equal(0, floating.Order);
        Assert.DoesNotContain(run.Findings, finding => finding.Code == "cable_on_frames");
    }

    [Fact]
    public void ADryRunChecksOnlyThePlannedSubjects()
    {
        TestLintWorld world = new TestLintWorld()
            .Add("pieces", Piece(2, "pipe", "air"))
            .Add("pieces", Piece(100, "pipe", "air"));
        LintRun run = LintEngine.Run(Only("floating_run"), world, "dry_run");
        Assert.Equal(100L, Assert.Single(run.Findings).ThingId);
        Assert.Empty(LintEngine.Run(Only("not_replaceable"), world, "dry_run").Findings);
    }

    [Fact]
    public void ARuleThatCannotBeEvaluatedReportsARuleErrorAndTheRestRun()
    {
        // replaceable has no game here: it throws, so not_replaceable gives rule_error findings for each thing.
        TestLintWorld world = new TestLintWorld()
            .Add("pieces", Piece(1, "cable", "air"))
            .Add("pieces", Piece(2, "cable", "frame_face"));
        LintRun run = LintEngine.Run(Only("not_replaceable", "floating_run"), world, "audit");
        Assert.Equal(2, run.RuleErrors);
        LintFinding error = run.Findings.Find(finding => finding.Code == LintEngine.RuleErrorCode)!;
        Assert.Equal("not_replaceable", error.RuleId);
        Assert.Equal("Rule not_replaceable could not be evaluated on piece 1: no game in tests.", error.Message);
        Assert.Single(run.Findings, finding => finding.Code == "floating_run");
    }

    [Fact]
    public void PairsAreFoundOnceByMeshBoxGap()
    {
        LintGraph graph = new LintGraph();
        LintRecord Device(int id, double x, double width) => graph.Make(LintModel.Thing, $"thing:{id}",
            ("reference_id", id), ("display_name", $"D{id}"), ("prefab", "StructureX"),
            ("position", new Vec3(x, 0, 0)),
            ("mesh_box", graph.Make(LintModel.Box, $"box:{id}", ("min", new Vec3(x, 0, 0)), ("max", new Vec3(x + width, 1, 1)))),
            ("cells", new List<ILintObject>()));
        TestLintWorld world = new TestLintWorld()
            .Add("devices", Device(1, 0, 1))
            .Add("devices", Device(2, 0.5, 1))
            .Add("devices", Device(3, 1.45, 0.5))
            .Add("devices", Device(4, 10, 1));
        LintRun run = LintEngine.Run(Only("device_visual_overlap"), world, "audit");
        Assert.Equal(2, run.Subjects["device_visual_overlap"]);
        LintFinding finding = Assert.Single(run.Findings);
        Assert.Equal(1L, finding.ThingId);
        Assert.Equal(2L, finding.OtherId);
        Assert.Equal("D1 (1) and D2 (2) run 0.50 m into each other.", finding.Message);
        Assert.Equal(new List<(int, int)> { (0, 1), (1, 2) }, LintPairs.Within(world.Subjects("devices"), 0));
        Assert.Equal(3, LintPairs.Within(world.Subjects("devices"), 0.5).Count);
    }

    [Fact]
    public void UpstreamAndDownstreamFollowFlowDevicesByPortRole()
    {
        LintGraph g = new LintGraph();
        LintRecord n1 = g.Make(LintModel.Network, "network:1", ("id", 1), ("kind", "pipe"));
        LintRecord n2 = g.Make(LintModel.Network, "network:2", ("id", 2), ("kind", "pipe"));
        LintRecord n3 = g.Make(LintModel.Network, "network:3", ("id", 3), ("kind", "pipe"));
        LintRecord n4 = g.Make(LintModel.Network, "network:4", ("id", 4), ("kind", "pipe"));
        LintRecord Port(string key, LintRecord network, string? flow) =>
            g.Make(LintModel.Port, key, ("network", network), ("flow", flow));
        LintRecord pump = g.Make(LintModel.Thing, "thing:10", ("flow", "pump"),
            ("ports", new List<ILintObject> { Port("p10/0", n1, "in"), Port("p10/1", n2, "out") }));
        LintRecord valve = g.Make(LintModel.Thing, "thing:11", ("flow", "valve"),
            ("ports", new List<ILintObject> { Port("p11/0", n2, null), Port("p11/1", n3, null) }));
        LintRecord tank = g.Make(LintModel.Thing, "thing:12", ("flow", null),
            ("ports", new List<ILintObject> { Port("p12/0", n3, "in"), Port("p12/1", n4, "out") }));
        g.Set("network:1", ("devices", new List<ILintObject> { pump }));
        g.Set("network:2", ("devices", new List<ILintObject> { pump, valve }));
        g.Set("network:3", ("devices", new List<ILintObject> { valve, tank }));
        g.Set("network:4", ("devices", new List<ILintObject> { tank }));
        LintContext context = new LintContext(new TestLintWorld());
        LintFunction upstream = LintTestKit.Library().Find("upstream")!;
        LintFunction downstream = LintTestKit.Library().Find("downstream")!;
        Assert.Equal("[network:2, network:1]", context.Invoke(upstream, new[] { LintValue.Of(n3) }).ToText());
        Assert.Equal("[network:2, network:3]", context.Invoke(downstream, new[] { LintValue.Of(n1) }).ToText());
        Assert.Equal("[]", context.Invoke(upstream, new[] { LintValue.Of(n1) }).ToText());
        Assert.Equal("[]", context.Invoke(downstream, new[] { LintValue.Of(n4) }).ToText());
    }
}

/// <summary>
/// Realistic rules that are not shipped, written against the model to prove it holds: pairs, upstream and
/// downstream, logic values, slots and nested quantifiers each need only the language and the library.
/// </summary>
public sealed class LintModelRobustnessTests
{
    private static LintRule Rule(string json)
    {
        LintRuleSet set = LintTestKit.Rules(LintTestKit.File1(json));
        Assert.Empty(set.Errors);
        return Assert.Single(set.Rules);
    }

    private static LintOutcome Judge(LintRule rule, ILintObject subject, ILintObject? other = null) =>
        LintEngine.Judge(rule, new LintSubjectRef(subject, other), new LintContext(new TestLintWorld()), false, true).Outcome;

    [Fact]
    public void PairsOfStackedSensors()
    {
        LintRule rule = Rule("{\"id\": \"stacked_sensors\", \"level\": \"warning\", " +
                             "\"select\": \"pairs(devices where prefab matches '*Sensor*', within: 0.25)\", " +
                             "\"assert\": \"a.prefab != b.prefab or distance(a, b) > 0.4\", " +
                             "\"message\": \"{a.display_name} and {b.display_name} read the same air {format(distance(a, b), '0.00')} m apart\"}");
        LintRecord a = LintTestKit.Thing("{\"prefab\": \"StructureGasSensor\", \"display_name\": \"A\", \"position\": [0, 0, 0]}");
        LintRecord b = LintTestKit.Thing("{\"prefab\": \"StructureGasSensor\", \"display_name\": \"B\", \"position\": [0.25, 0, 0]}");
        LintRecord c = LintTestKit.Thing("{\"prefab\": \"StructureGasSensor\", \"display_name\": \"C\", \"position\": [0.5, 0, 0]}");
        Assert.Equal(LintOutcome.Failed, Judge(rule, a, b));
        Assert.Equal(LintOutcome.Passed, Judge(rule, a, c));
    }

    [Fact]
    public void WasteNeverReachesARoomsVentDownstream()
    {
        LintRule rule = Rule("{\"id\": \"waste_into_room\", \"level\": \"problem\", " +
                             "\"select\": \"ports where device.flow == 'filter' and role == 'Output2'\", " +
                             "\"assert\": \"not has(network) or not any(network.devices + flat_map(downstream(network), n => n.devices), d => 'ActiveVent' in d.runtime_types and not d.outdoors)\", " +
                             "\"message\": \"{device.display_name}'s waste reaches a vent in a room\"}");
        LintGraph g = new LintGraph();
        LintRecord waste = g.Make(LintModel.Network, "network:1", ("id", 1), ("kind", "pipe"));
        LintRecord room = g.Make(LintModel.Network, "network:2", ("id", 2), ("kind", "pipe"));
        LintRecord filter = g.Make(LintModel.Thing, "thing:1", ("flow", "filter"), ("display_name", "Filtration"));
        LintRecord port = g.Make(LintModel.Port, "port:1/2", ("device", filter), ("role", "Output2"), ("network", waste), ("flow", "out"));
        LintRecord pump = g.Make(LintModel.Thing, "thing:2", ("flow", "pump"), ("runtime_types", new List<string> { "VolumePump" }),
            ("outdoors", false), ("ports", new List<ILintObject>
            {
                g.Make(LintModel.Port, "port:2/0", ("network", waste), ("flow", "in")),
                g.Make(LintModel.Port, "port:2/1", ("network", room), ("flow", "out"))
            }));
        LintRecord vent = g.Make(LintModel.Thing, "thing:3", ("flow", null), ("runtime_types", new List<string> { "ActiveVent" }),
            ("outdoors", false), ("ports", new List<ILintObject>()));
        g.Set("network:1", ("devices", new List<ILintObject> { filter, pump }));
        g.Set("network:2", ("devices", new List<ILintObject> { pump, vent }));
        g.Set("thing:1", ("runtime_types", new List<string> { "FiltrationMachine" }), ("outdoors", false), ("ports", new List<ILintObject> { port }));
        Assert.Equal(LintOutcome.Failed, Judge(rule, port));
        g.Set("thing:3", ("outdoors", true));
        Assert.Equal(LintOutcome.Passed, Judge(rule, port));
    }

    [Fact]
    public void ARoomWithVentsNeedsARegulatorUpstreamOfEach()
    {
        LintRule rule = Rule("{\"id\": \"room_vents_regulated\", \"level\": \"warning\", \"select\": \"rooms\", " +
                             "\"assert\": \"all(filter(devices, d => 'ActiveVent' in d.runtime_types), v => any(v.networks, n => n.kind == 'pipe' and any(upstream(n) + [n], u => any(u.devices, d => d.flow == 'regulator'))))\", " +
                             "\"message\": \"room {id} has a vent with no regulator feeding it\"}");
        LintGraph g = new LintGraph();
        LintRecord feed = g.Make(LintModel.Network, "network:1", ("id", 1), ("kind", "pipe"));
        LintRecord line = g.Make(LintModel.Network, "network:2", ("id", 2), ("kind", "pipe"));
        LintRecord regulator = g.Make(LintModel.Thing, "thing:5", ("flow", "regulator"), ("ports", new List<ILintObject>
        {
            g.Make(LintModel.Port, "p5/0", ("network", feed), ("flow", "in")),
            g.Make(LintModel.Port, "p5/1", ("network", line), ("flow", "out"))
        }));
        LintRecord vent = g.Make(LintModel.Thing, "thing:6", ("flow", null), ("runtime_types", new List<string> { "ActiveVent" }),
            ("networks", new List<ILintObject> { line }), ("ports", new List<ILintObject>()));
        g.Set("network:1", ("devices", new List<ILintObject> { regulator }));
        g.Set("network:2", ("devices", new List<ILintObject> { regulator, vent }));
        LintRecord room = g.Make(LintModel.Room, "room:9", ("id", 9), ("devices", new List<ILintObject> { vent }));
        Assert.Equal(LintOutcome.Passed, Judge(rule, room));
        g.Set("thing:5", ("flow", "pump"));
        Assert.Equal(LintOutcome.Failed, Judge(rule, room));
    }

    [Fact]
    public void BatteriesKeepCharge()
    {
        LintRule rule = Rule("{\"id\": \"battery_low\", \"level\": \"info\", " +
                             "\"select\": \"devices where prefab matches 'StructureBattery*'\", " +
                             "\"assert\": \"(logic(x, 'Ratio') ?? 1) > 0.1\", " +
                             "\"message\": \"{display_name} is at {format((logic(x, 'Ratio') ?? 0) * 100, '0')}%\"}");
        Assert.Equal(LintOutcome.Failed, Judge(rule,
            LintTestKit.Thing("{\"prefab\": \"StructureBattery\", \"display_name\": \"B\", \"logic(Ratio)\": 0.05}")));
        Assert.Equal(LintOutcome.Passed, Judge(rule,
            LintTestKit.Thing("{\"prefab\": \"StructureBattery\", \"display_name\": \"B\", \"logic(Ratio)\": 0.5}")));
        Assert.Equal(LintOutcome.Skipped, Judge(rule, LintTestKit.Thing("{\"prefab\": \"StructureGasSensor\"}")));
    }

    [Fact]
    public void FilterSlotsHoldCartridgesWithGasLeft()
    {
        LintRule rule = Rule("{\"id\": \"filters_loaded\", \"level\": \"warning\", \"select\": \"things where flow == 'filter'\", " +
                             "\"let\": {\"slots_of_kind\": \"filter(slots, s => s.type == 'GasFilter')\"}, " +
                             "\"assert\": \"count(slots_of_kind) > 0 and all(slots_of_kind, s => has(s.occupant) and s.quantity > 0)\", " +
                             "\"message\": \"{display_name}: {count(filter(slots_of_kind, s => not has(s.occupant)))} empty filter slots\"}");
        string Filter(string slot) => "{\"flow\": \"filter\", \"display_name\": \"F\", \"slots\": [" + slot + "]}";
        Assert.Equal(LintOutcome.Passed, Judge(rule, LintTestKit.Thing(Filter(
            "{\"index\": 0, \"type\": \"GasFilter\", \"name\": \"Filter\", \"occupant\": \"ItemGasFilterOxygen\", \"quantity\": 1}"))));
        Assert.Equal(LintOutcome.Failed, Judge(rule, LintTestKit.Thing(Filter(
            "{\"index\": 0, \"type\": \"GasFilter\", \"name\": \"Filter\", \"occupant\": null, \"quantity\": 0}"))));
    }

    [Fact]
    public void EveryChipBatchTargetExistsOnItsNetwork()
    {
        LintRule rule = Rule("{\"id\": \"batch_targets_exist\", \"level\": \"warning\", \"select\": \"networks where kind == 'cable'\", " +
                             "\"let\": {\"missing\": \"filter(chip_batch_names(x), b => has(b.prefab_hash) and not any(devices, d => d.prefab_hash == b.prefab_hash and (not has(b.name_hash) or hash(d.display_name) == b.name_hash)))\"}, " +
                             "\"assert\": \"count(missing) == 0\", " +
                             "\"message\": \"network {id}: chips batch {join(map(missing, b => b.prefab ?? str(b.prefab_hash)))} and nothing answers\"}");
        LintGraph g = new LintGraph();
        LintRecord housing = g.Make(LintModel.Thing, "thing:1", ("prefab", "StructureCircuitHousing"), ("prefab_hash", -128473777),
            ("display_name", "IC Housing"));
        LintRecord chip = g.Make(LintModel.Chip, "chip:1", ("housing", housing), ("language", "ic10"),
            ("source", "sbn HASH(\"StructureSolarPanel\") HASH(\"Tracker\") Horizontal r0"), ("pins", new List<ILintObject>()));
        LintRecord panel = g.Make(LintModel.Thing, "thing:2", ("prefab_hash", -2045627372), ("display_name", "Tracker"));
        LintRecord network = g.Make(LintModel.Network, "network:3", ("id", 3), ("kind", "cable"),
            ("chips", new List<ILintObject> { chip }), ("devices", new List<ILintObject> { housing, panel }));
        Assert.Equal(LintOutcome.Passed, Judge(rule, network));
        g.Set("thing:2", ("display_name", "Solar Panel"));
        Assert.Equal(LintOutcome.Failed, Judge(rule, network));
    }

    [Fact]
    public void NeighbouringCellsAreReachedThroughTheWorld()
    {
        LintRule rule = Rule("{\"id\": \"cable_has_company\", \"level\": \"info\", \"select\": \"cells\", " +
                             "\"assert\": \"any(neighbors(x, 'y'), c => count(c.pieces) > 0) or count(pieces) == 0\", " +
                             "\"message\": \"lonely\"}");
        LintGraph g = new LintGraph();
        LintRecord lower = g.Make(LintModel.Cell, "cell:a", ("position", new Vec3(0, 0, 0)), ("size", 0.5),
            ("pieces", new List<ILintObject> { g.Make(LintModel.Thing, "thing:1") }));
        LintRecord upper = g.Make(LintModel.Cell, "cell:b", ("position", new Vec3(0, 0.5, 0)), ("size", 0.5),
            ("pieces", new List<ILintObject> { g.Make(LintModel.Thing, "thing:2") }));
        TestLintWorld world = new TestLintWorld().Add("cells", lower).Add("cells", upper);
        LintRuleSet set = new LintRuleSet(new List<LintRule> { rule }, new List<LintRuleError>(), new List<string>(), new List<string>());
        Assert.Empty(LintEngine.Run(set, world, "audit").Findings);
        TestLintWorld alone = new TestLintWorld().Add("cells", lower);
        Assert.Single(LintEngine.Run(set, alone, "audit").Findings);
    }

    [Fact]
    public void FourThousandCellsOfThingsLintWithinBudget()
    {
        // A 4000-cell box full of runs and devices: every default rule that needs no game, well under a second.
        TestLintWorld world = new TestLintWorld();
        LintGraph g = new LintGraph();
        LintRecord network = g.Make(LintModel.Network, "network:1", ("id", 1), ("kind", "cable"), ("max_cable_power", 5000.0),
            ("min_cable_power", 5000.0), ("potential_load", 1000.0), ("required_load", 1000.0), ("devices", new List<ILintObject>()),
            ("members", new List<ILintObject>()), ("chips", new List<ILintObject>()));
        for (int id = 1; id <= 6000; id++)
        {
            double x = id % 40, z = id / 40 % 40, y = id / 1600;
            List<ILintObject> cells = new List<ILintObject>
            {
                g.Make(LintModel.Cell, $"cell:{id}", ("position", new Vec3(x, y, z)), ("size", 0.5), ("support", "frame_face"),
                    ("in_door_keepout", false), ("on_window_face", false), ("door_jamb", null))
            };
            world.Add("pieces", g.Make(LintModel.Thing, $"thing:{id}", ("reference_id", id), ("prefab", "StructureCableStraight"),
                ("kind", "cable"), ("position", new Vec3(x, y, z)), ("cells", cells), ("network", network), ("max_power", 5000.0),
                ("grade", "normal"), ("outdoors", false), ("content", null), ("runtime_types", new List<string> { "Cable" }),
                ("display_name", "Cable")));
        }

        for (int id = 10001; id <= 10800; id++)
        {
            double x = id % 40 + 0.25, z = id / 40 % 40;
            world.Add("devices", g.Make(LintModel.Thing, $"thing:{id}", ("reference_id", id), ("prefab", "StructureGasSensor"),
                ("kind", "device"), ("display_name", "Gas Sensor"), ("position", new Vec3(x, 1, z)), ("cells", new List<ILintObject>()),
                ("mesh_box", g.Make(LintModel.Box, $"box:{id}", ("min", new Vec3(x, 1, z)), ("max", new Vec3(x + 0.4, 1.4, z + 0.4)))),
                ("mounted", null), ("networks", new List<ILintObject>()), ("runtime_types", new List<string> { "GasSensor" }),
                ("flow", null), ("outdoors", false)));
        }

        List<LintRule> rules = new List<LintRule>();
        foreach (LintRule rule in LintTestKit.Defaults().Rules)
        {
            if (rule.Id != "not_replaceable" && rule.Id != "replaceable_unchecked")
            {
                rules.Add(rule);
            }
        }

        Stopwatch clock = Stopwatch.StartNew();
        LintRun run = LintEngine.Run(new LintRuleSet(rules, new List<LintRuleError>(), new List<string>(), new List<string>()), world, "audit");
        clock.Stop();
        Assert.Equal(0, run.RuleErrors);
        Assert.Empty(run.Findings);
        Assert.True(clock.ElapsedMilliseconds < 1500, $"took {clock.ElapsedMilliseconds} ms");
    }
}
