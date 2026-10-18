#nullable enable

using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Hub;
using StationGodMCP.Tests.Runs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// build item (items made out of nothing, by prefab and quantity, into a holder or at a point) and move gas source
/// "create": the orders, the rules that bound them, and both acting inside a confirmed run with no player.
/// </summary>
public sealed class ItemSpawnAndGasCreateTests
{
    // The kinds a headless build needs (an IC Housing's chip, a Lua chip, a Console's board): items that do not stack,
    // whose full stack is 1.
    private const double DoesNotStack = 1.0;

    private static PrefabFilter Prefab(string name) => new(new[] { PrefabPattern.Of(new JValue(name))! });

    private static ItemSpawnOrder? Order(out List<OptionProblem> problems, string? prefab = "ItemIntegratedCircuit10",
        double? quantity = null, string? to = null, long? slot = null, JToken? at = null)
    {
        problems = new List<OptionProblem>();
        return ItemSpawnOrder.Of(prefab == null ? null : Prefab(prefab), quantity, to, slot, at, problems);
    }

    [Fact]
    public void AnOrderNamesOnePrefabAndGoesIntoAHolderOrToAPoint()
    {
        ItemSpawnOrder order = Order(out _, quantity: 3, to: "5200100", slot: 2)!;
        Assert.Equal(3, order.Quantity);
        Assert.Equal("5200100", order.To);
        Assert.Equal(2, order.Slot);
        Assert.Null(order.At);

        ItemSpawnOrder loose = Order(out _, prefab: "ItemLuaChip", at: new JArray(10.5, 241, -3))!;
        Assert.Equal(1, loose.Quantity);
        Assert.Null(loose.To);
        Assert.Equal(241, loose.At!.Value.Y);

        Assert.Null(Order(out List<OptionProblem> neither));
        Assert.Contains(neither, problem => problem.Path == "to");
        Assert.Null(Order(out List<OptionProblem> both, to: "1", at: new JArray(0, 0, 0)));
        Assert.Contains(both, problem => problem.Path == "at");
        Assert.Null(Order(out List<OptionProblem> slotAlone, at: new JArray(0, 0, 0), slot: 1));
        Assert.Contains(slotAlone, problem => problem.Path == "slot");
        Assert.Null(Order(out List<OptionProblem> pattern, prefab: "*Chip*", to: "1"));
        Assert.Contains(pattern, problem => problem.Path == "prefab");
        Assert.Null(Order(out List<OptionProblem> none, prefab: null, to: "1"));
        Assert.Contains(none, problem => problem.Path == "prefab");
        Assert.Null(Order(out List<OptionProblem> zero, quantity: 0, to: "1"));
        Assert.Contains(zero, problem => problem.Path == "quantity");
    }

    [Theory]
    [InlineData("ItemIntegratedCircuit10")]
    [InlineData("ItemLuaChip")]
    [InlineData("MotherboardProgrammableChip")]
    public void ItemsThatDoNotStackAreMadeOneAStackAndCountWhole(string prefab)
    {
        Assert.Equal(3, ItemSpawnRule.Stacks(3, DoesNotStack));
        StackPlan loose = ItemSpawnRule.AtPoint(3, DoesNotStack);
        Assert.Equal(3, loose.Steps.Count);
        Assert.All(loose.Steps, step => Assert.Equal(1, step.Quantity));
        Assert.True(loose.Fits);
        Assert.Null(ItemSpawnRule.QuantityRefusal(3, DoesNotStack, countsWhole: true, prefab));
        Assert.Contains("not whole", ItemSpawnRule.QuantityRefusal(1.5, DoesNotStack, countsWhole: true, prefab));
        Assert.Contains("at most 64 stacks",
            ItemSpawnRule.QuantityRefusal(65, DoesNotStack, countsWhole: true, prefab));
    }

    [Fact]
    public void StackingItemsFillFullStacksFirst()
    {
        StackPlan ingots = ItemSpawnRule.AtPoint(120, 50);
        Assert.Equal(new double[] { 50, 50, 20 }, ingots.Steps.ConvertAll(step => step.Quantity));
        Assert.Null(ItemSpawnRule.QuantityRefusal(3200, 50, countsWhole: true, "ItemIronIngot"));
        Assert.NotNull(ItemSpawnRule.QuantityRefusal(3201, 50, countsWhole: true, "ItemIronIngot"));
        Assert.Null(ItemSpawnRule.QuantityRefusal(0.5, 1, countsWhole: false, "ItemCookedRice"));
    }

    [Fact]
    public void MakingThingsOutOfNothingIsForCreativeWorldsOnly()
    {
        Assert.Null(ItemSpawnRule.CreativeRefusal(creative: true));
        Assert.Contains("creative", ItemSpawnRule.CreativeRefusal(creative: false));
        Assert.Null(GasCreationRule.CreativeRefusal(creative: true));
        Assert.Contains("creative", GasCreationRule.CreativeRefusal(creative: false));
    }

    private static GasMoveOrder? Gas(out List<OptionProblem> problems, string? to = "2005300", bool delete = false,
        string[]? gases = null, double? mol = 500, double? kelvin = 293.15, string source = "create")
    {
        problems = new List<OptionProblem>();
        return GasMoveOrder.Of(new JValue(source), to == null ? null : new JValue(to), delete,
            gases ?? new[] { "Oxygen" }, mol, false, true, problems, kelvin);
    }

    [Fact]
    public void GasCreateGoesWithToNamedGasesMolesAndATemperature()
    {
        GasMoveOrder order = Gas(out _)!;
        Assert.True(order.Creates);
        Assert.IsType<GasPlaceOption.Created>(order.Source);
        Assert.Equal(293.15, order.TemperatureK);
        Assert.False(order.Deletes);

        Assert.Null(Gas(out List<OptionProblem> noTo, to: null));
        Assert.Contains(noTo, problem => problem.Path == "to");
        Assert.Null(Gas(out List<OptionProblem> deleting, to: null, delete: true));
        Assert.Contains(deleting, problem => problem.Path == "to");
        Assert.Null(Gas(out List<OptionProblem> noMol, mol: null));
        Assert.Contains(noMol, problem => problem.Path == "amount_mol");
        Assert.Null(Gas(out List<OptionProblem> noKelvin, kelvin: null));
        Assert.Contains(noKelvin, problem => problem.Path == "temperature_k");
        Assert.Null(Gas(out List<OptionProblem> cold, kelvin: 0));
        Assert.Contains(cold, problem => problem.Path == "temperature_k");
        Assert.Null(Gas(out List<OptionProblem> moved, source: "2005301"));
        Assert.Contains(moved, problem => problem.Path == "temperature_k");
        Assert.Null(Gas(out List<OptionProblem> intoCreate, to: "create"));
        Assert.Contains(intoCreate, problem => problem.Path == "to");
    }
}

/// <summary>build item and move gas create act in a confirmed run, and what build item made is the run's own.</summary>
[Collection(PythonCollection.Name)]
public sealed class ItemSpawnAndGasCreateRunTests
{
    [PythonFact]
    public async Task AConfirmedRunMakesItemsAndGasAndActsOnWhatItMade()
    {
        await using RunBench bench = new();
        bench.World.Override = call => RunWorld.KeyOf(call) == "build.item" && call.Parameters.GetRawText().Contains("\"confirm\":true")
            ? FakeGameLink.Ok("""{"placed":["7001"],"prefab_name":"ItemIntegratedCircuit10","quantity":1,"items":[{"where":"slot","slot":{"id":"5200100","slot":0},"quantity":1,"reference_id":"7001"}]}""")
            : null;

        string text = await bench.RunAsync(
            "made = sg.build('item', prefab='ItemIntegratedCircuit10', to='5200100')\n" +
            "sg.move('gas', source='create', to='5200100', gases=['Oxygen'], amount_mol=500, temperature_k=293.15)\n" +
            "sg.write('label', id=made['placed'][0], label='Chip')", confirm: true);

        Assert.True(text.StartsWith("script-1 done\n"), text);
        Assert.Contains("build.item", bench.World.Log);
        Assert.Contains("move.gas", bench.World.Log);
        Assert.Contains("write.label", bench.World.Log);
        Assert.DoesNotContain("report:build.item", bench.World.Log);
    }
}
