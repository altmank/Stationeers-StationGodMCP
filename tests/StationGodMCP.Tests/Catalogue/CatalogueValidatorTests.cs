#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Server;
using Xunit;
using ModCatalogue = StationGodMCP.Pure.Catalogue.Catalogue;

namespace StationGodMCP.Tests.CatalogueChecks;

/// <summary>
/// The mod's schema-subset validator against the sidecar's ArgumentCheck, on the cases ArgumentCheck covers: unknown
/// names with the nearest one, wrong types, enum case folding, 3.0 and 1e2 as integers, nulls as omitted, required
/// arguments, entry counts, oneOf forms and nested objects; and repeated keys. Both must accept and refuse alike.
/// </summary>
public sealed class CatalogueValidatorTests
{
    private static readonly ModCatalogue Catalogue = ModCatalogue.Load(File.ReadAllText(CatalogueFiles.AssembledPath));

    public static IEnumerable<object[]> Cases() => new[]
    {
        Case("find_things", """{"prefab":"ItemDirtyOre"}""", false),
        Case("find_things", """{"prefab_contains":"Ore","kind":null}""", true),
        Case("find_things", """{"limit":"5"}""", false),
        Case("find_things", """{"limit":3.0}""", true),
        Case("find_things", """{"limit":1e2}""", true),
        Case("find_things", """{"limit":2.5}""", false),
        Case("find_things", """{"limit":null,"offset":null}""", true),
        Case("thing_health", """{"kind":" CABLE ","network_id":"5"}""", true),
        Case("thing_health", """{"kind":"wire","network_id":"5"}""", false),
        Case("thing_health", """{"reference_ids":[]}""", false),
        Case("thing_health", """{"reference_ids":["1","2"]}""", true),
        Case("thing_health", """{"reference_ids":"1"}""", false),
        Case("thing_health", """{"network_id":{"reference_id":"5","port":1}}""", true),
        Case("thing_health", """{"network_id":{"reference_id":"5","bogus":1}}""", false),
        Case("thing_health", """{"network_id":5}""", false),
        Case("read_logic", """{"reference_id":"5","logic_type":"On"}""", true),
        Case("read_logic", """{"reference_id":"5"}""", false),
        Case("read_logic", """{"reference_id":null,"logic_type":"On"}""", false),
        Case("read_logic", """{"reference_id":5,"logic_type":"On"}""", false),
        Case("write_logic", """{"reference_id":"5","logic_type":"On","value":true}""", false),
        Case("read_devices", """{"items":[{"reference_id":"5","logic":["On"]}],"include":["clock"]}""", true),
        Case("read_devices", """{"items":[{"reference_id":"5","logic":["On"]}],"include":["CLOCK"]}""", true),
        Case("game_clock", "{}", true),
        Case("game_clock", """{"verbose":true}""", false),
        Case("list_gateways", "null", true)
    };

    private static object[] Case(string tool, string arguments, bool accepted) => new object[] { tool, arguments, accepted };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheModsValidatorAgreesWithTheSidecars(string tool, string arguments, bool accepted)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        bool sidecar = ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement).Count == 0;
        Assert.True(Catalogue.TryGet(tool, out CatalogueMethod method));
        bool mod = method.Check(JToken.Parse(arguments) as JObject).Count == 0;

        Assert.Equal(accepted, sidecar);
        Assert.Equal(accepted, mod);
    }

    [Fact]
    public void EveryToolCalledWithNothingOrAnUnknownNameGetsTheSameVerdict()
    {
        foreach (CatalogueMethod method in Catalogue.Methods.Where(method => !method.Hidden))
        {
            foreach (string arguments in new[] { "{}", """{"zz_not_an_argument":1}""" })
            {
                using JsonDocument document = JsonDocument.Parse(arguments);
                bool sidecar = ArgumentCheck.Problems(Program.InputSchemas[method.Name], document.RootElement).Count == 0;
                bool mod = method.Check(JObject.Parse(arguments)).Count == 0;
                Assert.True(sidecar == mod, $"{method.Name} {arguments}: sidecar {sidecar}, mod {mod}");
            }
        }
    }

    [Fact]
    public void AnUnknownNameIsRefusedNamingTheNearestOne()
    {
        Catalogue.TryGet("find_things", out CatalogueMethod method);
        List<SchemaProblem> problems = method.Check(JObject.Parse("""{"prefab":"x"}"""));

        SchemaProblem problem = Assert.Single(problems);
        Assert.Equal("prefab", problem.Path);
        Assert.Equal("Unknown argument 'prefab'; did you mean 'prefab_contains'?", problem.Problem);
    }

    [Fact]
    public void ARangeTheHandlerReadsIsCheckedWithTheArgumentsPath()
    {
        Catalogue.TryGet("find_things", out CatalogueMethod method);

        SchemaProblem problem = Assert.Single(method.Check(JObject.Parse("""{"limit":501}""")));
        Assert.Equal("Argument 'limit' must be at least 1 and at most 500.", problem.Problem);
    }

    [Fact]
    public void ARepeatedKeyIsRefusedBeforeTheSchema()
    {
        Catalogue.TryGet("find_things", out CatalogueMethod method);

        SchemaProblem problem = Assert.Single(method.Check("""{"limit":5,"limit":500}"""));
        Assert.Equal("limit", problem.Path);
        Assert.Contains("given twice", problem.Problem);
        Assert.NotEmpty(ArgumentCheck.RepeatedKey(JsonDocument.Parse("""{"limit":5,"limit":500}""").RootElement) ?? "");
    }

    [Fact]
    public void ClassAndCostRulesFollowTheArguments()
    {
        Catalogue.TryGet("place_structure", out CatalogueMethod place);
        Assert.Equal(MethodClass.Read, place.ClassAt(JObject.Parse("""{"prefab":"x"}""")));
        Assert.Equal(MethodClass.Read, place.ClassAt(JObject.Parse("""{"dry_run":true}""")));
        Assert.Equal(MethodClass.Write, place.ClassAt(JObject.Parse("""{"dry_run":false,"confirm":true}""")));
        Assert.Equal(MethodClass.Cheat, place.ClassAt(JObject.Parse("""{"dry_run":false,"free":true}""")));
        Assert.Equal(CostClass.Plan, place.CostAt(JObject.Parse("""{"prefab":"x"}""")).Cost);
        Assert.Equal(CostClass.Job, place.CostAt(JObject.Parse("""{"dry_run":false}""")).Cost);
        Assert.Equal(CostClass.Instant, place.CostAt(JObject.Parse("""{"job_id":"place-3"}""")).Cost);

        Catalogue.TryGet("rocket_flight_log", out CatalogueMethod log);
        Assert.Equal(MethodClass.Read, log.ClassAt(null));
        Assert.Equal(MethodClass.Read, log.ClassAt(JObject.Parse("""{"action":" LIST "}""")));
        Assert.Equal(MethodClass.Write, log.ClassAt(JObject.Parse("""{"action":"start"}""")));

        Catalogue.TryGet("read_devices", out CatalogueMethod reads);
        CallCost cost = reads.CostAt(JObject.Parse("""{"items":[{},{},{}]}"""));
        Assert.Equal(CostClass.Bounded, cost.Cost);
        Assert.Equal(3, cost.Items);

        Catalogue.TryGet("move_gas", out CatalogueMethod gas);
        Assert.Equal(MethodClass.Cheat, gas.ClassAt(JObject.Parse("""{"from":"1","to":"2"}""")));
        Assert.Equal(MethodClass.Read, gas.ClassAt(JObject.Parse("""{"transfer_id":"9"}""")));
        Assert.Equal(MethodClass.Read, gas.ClassAt(JObject.Parse("""{"dry_run":true,"from":"1"}""")));
    }

    [Fact]
    public void ReadOnlyAnnotationsAreTheReadClassWithoutRules()
    {
        foreach (CatalogueMethod method in Catalogue.Methods.Where(method => !method.Hidden))
        {
            bool readOnly = Program.InputSchemas.ContainsKey(method.Name) &&
                            ToolCatalogue.Tools.EnumerateArray()
                                .Single(tool => tool.GetProperty("name").GetString() == method.Name)
                                .GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean();
            Assert.Equal(method.DefaultClass == MethodClass.Read && !method.HasClassRules, readOnly);
        }
    }
}
