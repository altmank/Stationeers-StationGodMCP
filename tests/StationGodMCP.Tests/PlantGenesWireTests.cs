#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>plant_genes: the old StationApi.Genes shapes against the new views; no renames.</summary>
public sealed class PlantGenesWireTests
{
    private static readonly ThingView Tomato = new ThingView(new ThingId(300), "SeedBag_Tomato", "Tomato Seeds");

    private static readonly GeneSetHeader Header = new GeneSetHeader(Tomato, "seed", 1, true);

    private static object OldThing => new
    {
        reference_id = "300", prefab_name = "SeedBag_Tomato", display_name = "Tomato Seeds"
    };

    private static List<object> OldGenes() => new List<object>
    {
        new
        {
            gene = "LightPerDay", value = (float?)0.25f, min = -1f, max = 1f, stability = (float?)0.1f,
            stability_min = -1f, stability_max = 1f, meaning = "Seconds of light.",
            effect = new JObject
            {
                ["stat"] = "LightPerDay", ["base_s"] = 300f, ["now_s"] = 281.25f, ["at_min_s"] = 375f,
                ["at_max_s"] = 225f
            }
        },
        new
        {
            gene = "WaterUsage", value = (float?)-0.5f, min = -1f, max = 1f, stability = (float?)0f,
            stability_min = -1f, stability_max = 1f, meaning = "Multiplies water.",
            effect = new JObject
            {
                ["stat"] = "WaterUsage", ["base_factor"] = 1f, ["now_factor"] = 0.875f, ["at_min_factor"] = 0.75f,
                ["at_max_factor"] = 1.25f
            }
        },
        new
        {
            gene = "LowTemperatureResistance", value = (float?)0f, min = -1f, max = 1f, stability = (float?)0f,
            stability_min = -1f, stability_max = 1f, meaning = "Moves the low end.",
            effect = new JObject
            {
                ["stat"] = "GrowTemperature",
                ["now"] = new JObject { ["ideal_min_k"] = 288.15, ["min_k"] = 278.15 },
                ["at_min"] = new JObject { ["ideal_min_k"] = 294.15, ["min_k"] = 290.15 },
                ["at_max"] = new JObject { ["ideal_min_k"] = 273.15, ["min_k"] = 248.15 }
            }
        },
        new
        {
            gene = "HighPressureResistance", value = (float?)0f, min = -1f, max = 1f, stability = (float?)0f,
            stability_min = -1f, stability_max = 1f, meaning = "Moves the high end.",
            effect = new JObject
            {
                ["stat"] = "GrowPressure",
                ["now"] = new JObject { ["ideal_max_kpa"] = 120.0, ["max_kpa"] = 150.0 },
                ["at_min"] = new JObject { ["ideal_max_kpa"] = 110.0, ["max_kpa"] = 130.0 },
                ["at_max"] = new JObject { ["ideal_max_kpa"] = 160.0, ["max_kpa"] = 200.0 }
            }
        },
        new
        {
            gene = "DarknessTolerance", value = (float?)null, min = -1f, max = 1f, stability = (float?)null,
            stability_min = -1f, stability_max = 1f, meaning = (string?)null, effect = (JObject?)null
        }
    };

    private static List<GeneView> NewGenes() => new List<GeneView>
    {
        new GeneView("LightPerDay", 0.25f, 0.1f, "Seconds of light.",
            new SecondsEffectView("LightPerDay", new StatSpread(300f, 281.25f, 375f, 225f))),
        new GeneView("WaterUsage", -0.5f, 0f, "Multiplies water.",
            new FactorEffectView("WaterUsage", new StatSpread(1f, 0.875f, 0.75f, 1.25f))),
        new GeneView("LowTemperatureResistance", 0f, 0f, "Moves the low end.",
            new BandEffectView("GrowTemperature", new BandLowKView(288.15, 278.15), new BandLowKView(294.15, 290.15),
                new BandLowKView(273.15, 248.15))),
        new GeneView("HighPressureResistance", 0f, 0f, "Moves the high end.",
            new BandEffectView("GrowPressure", new BandHighKpaView(120.0, 150.0), new BandHighKpaView(110.0, 130.0),
                new BandHighKpaView(160.0, 200.0))),
        new GeneView("DarknessTolerance", null, null, null, null)
    };

    [Fact]
    public void ReadSameWire()
    {
        var old = new
        {
            thing = OldThing, holder = "seed", gene_set_count = 2, unit = 1, is_top = true, genes = OldGenes()
        };
        WireCheck.Same(old, new PlantGenesView(Header, 2, NewGenes()));
    }

    [Fact]
    public void BatchReadSameWire()
    {
        JObject item = JObject.Parse(WireCheck.New(new PlantGenesView(Header, 2, NewGenes())));
        item.AddFirst(new JProperty("ok", true));
        item.AddFirst(new JProperty("index", 0));
        var old = new
        {
            results = new List<object>
            {
                item,
                new { index = 1, ok = false, error = new { code = "not_a_plant", message = "Wall is not a plant." } }
            },
            count = 2, success_count = 1, error_count = 1
        };
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new PlantGenesItemView(0, new PlantGenesView(Header, 2, NewGenes())));
        batch.Failed(1, ApiErrors.Refused("not_a_plant", "Wall is not a plant."));
        WireCheck.Same(old, batch.Build());
    }

    [Fact]
    public void WriteSameWire()
    {
        var old = new
        {
            thing = OldThing, holder = "seed", unit = 1, is_top = true,
            results = new List<object>
            {
                new { index = 0, ok = true, gene = "WaterUsage", previous_value = (float?)-0.5f, value = 0.5f },
                new
                {
                    index = 1, ok = false, gene = "Nope", previous_value = (float?)null,
                    error = new { code = "unknown_gene", message = "'Nope' is not a gene." }
                }
            },
            count = 2, success_count = 1, error_count = 1
        };
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new GeneWrittenView(0, "WaterUsage", -0.5f, 0.5f));
        ApiException unknown = ApiErrors.Refused("unknown_gene", "'Nope' is not a gene.");
        batch.Failed(new GeneNotWrittenView(1, "Nope", null, unknown));
        WireCheck.Same(old, new GenesWrittenView(Header, batch.Build()));
    }
}
