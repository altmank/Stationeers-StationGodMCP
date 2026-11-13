#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Api.Views;

/// <summary>One way a prefab is made: the machine, its inputs, time and energy, and a furnace's ranges.</summary>
internal sealed class RecipeView
{
    internal RecipeView(string? maker, string? makerDisplayName, float timeS, float energyJ,
        Dictionary<string, double> inputs, RangeView? temperatureK, RangeView? pressureKpa, string? source)
    {
        Maker = maker;
        MakerDisplayName = makerDisplayName;
        TimeS = timeS;
        EnergyJ = energyJ;
        Inputs = inputs;
        TemperatureK = temperatureK;
        PressureKpa = pressureKpa;
        Source = source;
    }

    /// <summary>The machine's prefab (StructureAutolathe, StructureFurnace, AppliancePackagingMachine...).</summary>
    public string? Maker { get; }

    public string? MakerDisplayName { get; }

    public float TimeS { get; }

    public float EnergyJ { get; }

    /// <summary>Material amounts by Recipe's names (Iron, Copper, Solder...): grams of an ingot, units of a reagent.</summary>
    public Dictionary<string, double> Inputs { get; }

    /// <summary>The temperature range a furnace recipe needs; left out when it needs none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RangeView? TemperatureK { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public RangeView? PressureKpa { get; }

    /// <summary>The item the recipe is made from (the reagent processor's input); left out for the others.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Source { get; }
}

internal sealed class RangeView
{
    internal RangeView(float start, float stop)
    {
        Start = start;
        Stop = stop;
    }

    public float Start { get; }

    public float Stop { get; }
}

/// <summary>One slot of a prefab's template: its place in the list, names, class, and whether it takes any item.</summary>
internal sealed record PrefabSlotView(int Index, string? DisplayName, string? StringKey, EnumValueView SlotClass,
    bool General);

/// <summary>A prefab's slots as the template lists them; the rows never change after it is built.</summary>
internal sealed record PrefabSlotsView(IReadOnlyList<PrefabSlotView> Slots, int GeneralSlots);

/// <summary>
/// inspect prefab of a prefab that is no buildable structure (an item, a kit), and each row of a list: what it is, its
/// game data and its recipes when asked.
/// </summary>
internal sealed class PrefabFactsView
{
    internal PrefabFactsView(string? prefabName, int prefabHash, string? displayName, string runtimeType,
        bool buildable, JObject? data, List<RecipeView>? recipes, PrefabSlotsView? slots = null)
    {
        Slots = slots?.Slots;
        GeneralSlots = slots?.GeneralSlots;
        PrefabName = prefabName;
        PrefabHash = prefabHash;
        DisplayName = displayName;
        RuntimeType = runtimeType;
        Buildable = buildable;
        Data = data;
        Recipes = recipes;
    }

    public string? PrefabName { get; }

    public int PrefabHash { get; }

    public string? DisplayName { get; }

    public string RuntimeType { get; }

    /// <summary>A kit builds it: inspect prefab of it alone gives its placement, cells and ports.</summary>
    public bool Buildable { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public JObject? Data { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<RecipeView>? Recipes { get; }

    /// <summary>The template's slots, one row each; left out for a prefab with none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public IReadOnlyList<PrefabSlotView>? Slots { get; }

    /// <summary>How many of the slots take any item; left out with Slots.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? GeneralSlots { get; }
}

/// <summary>inspect prefab of a list or a pattern: a row per prefab it names, by name, at most MaximumRows.</summary>
internal sealed class PrefabListView
{
    internal PrefabListView(List<PrefabFactsView> prefabs, int matched)
    {
        Prefabs = prefabs;
        Matched = matched;
    }

    public List<PrefabFactsView> Prefabs { get; }

    /// <summary>How many prefabs the list or pattern named; more than listed when it was cut at MaximumRows.</summary>
    public int Matched { get; }

    public int Listed => Prefabs.Count;
}
