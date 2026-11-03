#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>write fabricator: the order's problems, the merge of loads, the busy and power rules, consumption, the messages.</summary>
public sealed class WriteFabricatorTests
{
    private static PrefabFilter? Prefab(string? name) =>
        name == null ? null : new PrefabFilter(new[] { PrefabPattern.Of(new JValue(name))! });

    private static JArray Load(params (object prefab, double quantity)[] entries) =>
        new JArray(entries.Select(entry => new JObject
        {
            ["prefab"] = JToken.FromObject(entry.prefab),
            ["quantity"] = entry.quantity
        }));

    private static FabricateOrder? Order(out List<OptionProblem> problems, string? prefab = "ItemMKIIWrench",
        JToken? load = null, bool? make = null)
    {
        problems = new List<OptionProblem>();
        return FabricateOrder.Of(Prefab(prefab), load, make, problems);
    }

    private static void ProblemAt(string path, FabricateOrder? order, List<OptionProblem> problems)
    {
        Assert.Null(order);
        Assert.Contains(problems, problem => problem.Path == path);
    }

    [Fact]
    public void AnOrderNamesOneItemAndMakesIt()
    {
        FabricateOrder order = Order(out _)!;
        Assert.True(order.Make);
        Assert.Empty(order.Loads);
        Assert.IsType<PrefabPattern.Whole>(order.Prefab);
    }

    [Fact]
    public void ThePrefabIsOneWholeNameOrHash()
    {
        FabricateOrder? pattern = Order(out List<OptionProblem> patternProblems, prefab: "*Wrench*");
        ProblemAt("prefab", pattern, patternProblems);
        PrefabFilter list = new PrefabFilter(new[] { PrefabPattern.Of(new JValue("A"))!, PrefabPattern.Of(new JValue("B"))! });
        List<OptionProblem> listProblems = new List<OptionProblem>();
        ProblemAt("prefab", FabricateOrder.Of(list, null, null, listProblems), listProblems);
    }

    [Fact]
    public void MakingNeedsAPrefabAndLoadingOnlyNeedsALoad()
    {
        ProblemAt("prefab", Order(out List<OptionProblem> a, prefab: null), a);
        ProblemAt("load", Order(out List<OptionProblem> b, prefab: null, make: false), b);
        FabricateOrder loadOnly = Order(out _, prefab: null, load: Load(("ItemIronIngot", 2)), make: false)!;
        Assert.False(loadOnly.Make);
        Assert.Null(loadOnly.Prefab);
        Assert.Single(loadOnly.Loads);
    }

    [Fact]
    public void ALoadIsBoundedAndEachEntryNamesAnIngotAndAQuantity()
    {
        JArray tooMany = new JArray(Enumerable.Range(0, FabricateRule.MaximumLoads + 1)
            .Select(i => new JObject { ["prefab"] = "Ingot" + i, ["quantity"] = 1 }));
        ProblemAt("load", Order(out List<OptionProblem> many, load: tooMany), many);
        ProblemAt("load[0].quantity", Order(out List<OptionProblem> zero, load: Load(("ItemIronIngot", 0))), zero);
        ProblemAt("load[0].quantity", Order(out List<OptionProblem> negative, load: Load(("ItemIronIngot", -1))), negative);
        ProblemAt("load[1].quantity",
            Order(out List<OptionProblem> big, load: Load(("A", 1), ("ItemIronIngot", FabricateRule.MaximumLoadQuantity + 1))),
            big);
        ProblemAt("load[0].quantity", Order(out List<OptionProblem> inf, load: Load(("A", double.PositiveInfinity))), inf);
        JArray noPrefab = new JArray(new JObject { ["quantity"] = 2 });
        ProblemAt("load[0].prefab", Order(out List<OptionProblem> missing, load: noPrefab), missing);
        ProblemAt("load", Order(out List<OptionProblem> notList, load: new JValue("x")), notList);
        Assert.NotNull(Order(out _, load: Load(("A", FabricateRule.MaximumLoadQuantity))));
    }

    [Fact]
    public void LoadsOfOneIngotAreMergedInFirstSeenOrder()
    {
        FabricateOrder order = Order(out _,
            load: Load(("ItemIronIngot", 2), ("ItemElectrumIngot", 1), ("itemironingot", 3.5), (123, 1), (123, 2)))!;
        Assert.Equal(3, order.Loads.Count);
        Assert.Equal(5.5, order.Loads[0].Quantity);
        Assert.Equal("ItemIronIngot", ((PrefabPattern.Whole)order.Loads[0].Prefab).Name);
        Assert.Equal(1, order.Loads[1].Quantity);
        Assert.Equal(3, order.Loads[2].Quantity);
    }

    [Fact]
    public void BusyReasonsAreCheckedInOrder()
    {
        Assert.Equal("making", FabricateRule.BusyReason(true, true, "1"));
        Assert.Equal("open", FabricateRule.BusyReason(false, true, "1"));
        Assert.Equal("output_occupied", FabricateRule.BusyReason(false, false, "1"));
        Assert.Null(FabricateRule.BusyReason(false, false, null));
    }

    [Fact]
    public void PowerReasonsAreCheckedInOrder()
    {
        Assert.Equal("not_built", FabricateRule.PowerReason(false, true, false, false, 1));
        Assert.Equal("not_built", FabricateRule.PowerReason(true, false, false, false, 1));
        Assert.Equal("off", FabricateRule.PowerReason(true, true, false, false, 1));
        Assert.Equal("unpowered", FabricateRule.PowerReason(true, true, true, false, 1));
        Assert.Equal("error", FabricateRule.PowerReason(true, true, true, true, 2));
        Assert.Null(FabricateRule.PowerReason(true, true, true, true, 0));
    }

    [Fact]
    public void ConsumedIsBeforePlusLoadedMinusAfterRoundedWithoutTrace()
    {
        Dictionary<string, double> consumed = FabricateRule.Consumed(
            new Dictionary<string, double> { ["Iron"] = 1, ["Gold"] = 2 },
            new Dictionary<string, double> { ["Iron"] = 4.0000004, ["Electrum"] = 4 },
            new Dictionary<string, double> { ["Iron"] = 2.0000001, ["Gold"] = 2, ["Electrum"] = 1 });
        Assert.Equal(2, consumed.Count);
        Assert.Equal(3.0, consumed["Iron"]);
        Assert.Equal(3.0, consumed["Electrum"]);
        Assert.DoesNotContain("Gold", consumed.Keys);
    }

    [Fact]
    public void ALoadOnlyCallConsumesNothing()
    {
        Dictionary<string, double> none = new Dictionary<string, double>();
        Assert.Empty(FabricateRule.Consumed(none, new Dictionary<string, double> { ["Iron"] = 5 },
            new Dictionary<string, double> { ["Iron"] = 5 }));
    }

    [Fact]
    public void TheMessagesNameWhatTheyMustAndEndWithNothingMade()
    {
        string tier = FabricateRule.WrongTier("ItemMKIIWrench", "TierTwo", "Tool Manufactory", "TierOne");
        Assert.Contains("TierTwo", tier);
        Assert.Contains("TierOne", tier);
        Assert.EndsWith("Nothing was loaded or made.", tier);

        string missing = FabricateRule.MissingReagents("Tool Manufactory",
            new Dictionary<string, double> { ["Iron"] = 3, ["Gold"] = 0.5 },
            new Dictionary<string, string> { ["Iron"] = "ItemIronIngot" });
        Assert.Contains("3 Iron (ItemIronIngot)", missing);
        Assert.Contains("0.5 Gold", missing);
        Assert.EndsWith("Nothing was loaded or made.", missing);

        string none = FabricateRule.NotARecipe("ItemIronIngot", "Tool Manufactory", new List<string>());
        Assert.Contains("no machine makes it", none);
        List<string> makers = Enumerable.Range(1, 8).Select(i => "Maker" + i).ToList();
        string capped = FabricateRule.NotARecipe("ItemIronIngot", "Tool Manufactory", makers);
        Assert.Contains("Maker5", capped);
        Assert.DoesNotContain("Maker6", capped);
        Assert.EndsWith("Nothing was loaded or made.", capped);
    }

    [Fact]
    public void EveryNewErrorCodeIsInTheCatalogueAndPointsAtTheWhat()
    {
        JsonObject errors = CatalogueFiles.ReadJson(Path.Combine(VerbCatalogue.SourceRoot, "_errors.json")).AsObject();
        foreach (string code in new[]
                 {
                     "not_a_fabricator", "not_a_recipe", "wrong_tier", "not_accepted", "missing_reagents",
                     "fabricator_unpowered", "fabricator_busy"
                 })
        {
            JsonNode? see = errors[code]?["see"];
            Assert.NotNull(see);
            Assert.Equal("write", (string?)see!["verb"]);
            Assert.Equal("fabricator", (string?)see["what"]);
        }
    }
}
