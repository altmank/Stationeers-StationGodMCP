#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Genetics;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// plant_genes: read and edit the genes of plants, seeds and harvested produce. Reading is read only; the write form
/// edits one gene set the way the game's Gene Splicer does.
///
/// How the game keeps genes (CODE, Assets.Scripts.Genetics): Gene is 19 traits. A GeneCollection holds a GeneWrapper
/// {Gene, Value, Stability} per trait in GeneCollection.Lookup; a new one starts every Value at +-0.05 and Stability 0.
/// A Plant item (seeds are Seed : Plant) keeps one GeneCollection per unit of its stack in
/// Stackable.StackedGeneCollections; Plant.Genes is the last one, the top of the stack, which is what planting copies
/// (Plant.ApplySeedTraits), what the plant's stats read and what the network sends. Harvesting gives each fruit or seed
/// GeneCollection.CreateSimilar of the parent's (Plant.InheritTraits): the values are copied, then mutated by a
/// gaussian step whose width shrinks with Stability and is biased by the parent's recorded stress
/// (PlantLifeRequirements.GetMutationBias), clamped to -1..1. The save stores every unit's set
/// (Plant.InitialiseSaveData), so an edit is saved and inherited like a natural gene.
///
/// The list can hold one set more than the stack (live, 1.4.4 round 3: crate seed bags of quantity 1, 2 and 3 held
/// 2, 3 and 4). The Plant.Genes getter adds a set to an empty list, and Plant.DeserializeSave, when the saved sets do
/// not number the quantity, adds quantity new ones on top of whatever is there. Planting copies the top set and
/// using a unit removes the top set (DecrementQuantity, RemoveQuantity), so the extra one is the bottom set (unit 0),
/// which no unit ever plants. The tool reports the game's list as it is.
///
/// Ranges: a gene Value is -1..1 (GeneCollection.MutateValue and Stackable.SetGene clamp to it), Stability -1..1
/// (GeneWrapper.Stabilise). Fifteen genes scale one PlantStat of PlantLifeRequirements: PlantStat.Get is
/// Lerp(Base, Max, v) for v > 0 and Lerp(Base, Min, -v) otherwise, Min and Max swapped when the stat is inverted
/// (LightPerDay and DarknessPerDay: a positive gene needs less). The other four move the ends of the temperature and
/// pressure bands (MultiPlantStat): the low genes set IdealMin and Min, the high genes IdealMax and Max. Every stat is
/// read live from Plant.Genes except the curves PlantLifeRequirements.UpdateTemperaturePressureCurves caches; a write
/// to the top set recomputes them.
///
/// The write follows PlantGeneticSplicer.StartSplice: set the value in the Lookup and, on a multiplayer host, set
/// network flag 512, which Plant.BuildUpdate sends as the top gene set. A multiplayer client does not own the plant, so
/// it is refused.
/// </summary>
internal static class PlantGenesApi
{
    internal const int MaximumIds = 256;

    internal static object Handle(Args args)
    {
        switch (GenesRequest.Parse(args))
        {
            case GenesRequest.Read read:
                return GeneReader.Read(RequirePlant(read.Id), read.Unit);
            case GenesRequest.ReadMany many:
                return ReadMany(many.Ids);
            case GenesRequest.Write write:
                return GeneWriter.Write(RequirePlant(write.Id), write);
            default:
                throw ApiErrors.InvalidArgument("Unknown plant_genes form.");
        }
    }

    private static BatchResultView ReadMany(JArray ids)
    {
        BatchBuilder batch = new BatchBuilder(ids.Count);
        for (int index = 0; index < ids.Count; index++)
        {
            if (!ThingId.TryRead(ids[index], out ThingId id))
            {
                batch.Failed(
                    index, ApiErrors.InvalidArgument("Argument 'reference_id' must be an Int64 encoded as a string."));
            }
            else if (!TryFindPlant(id, out Plant? plant, out ApiException? error))
            {
                batch.Failed(index, error!);
            }
            else if (!GeneReader.TryRead(plant!, null, out PlantGenesView? view, out error))
            {
                batch.Failed(index, error!);
            }
            else
            {
                batch.Succeeded(new PlantGenesItemView(index, view!));
            }
        }

        return batch.Build();
    }

    private static Plant RequirePlant(ThingId id)
    {
        if (!TryFindPlant(id, out Plant? plant, out ApiException? error))
        {
            throw error!;
        }

        return plant!;
    }

    private static bool TryFindPlant(ThingId id, out Plant? plant, out ApiException? error)
    {
        plant = null;
        error = null;
        if (!GameLookup.TryFindThing(id, out Thing thing))
        {
            error = ApiErrors.ThingNotFound(id);
            return false;
        }

        plant = thing as Plant;
        if (plant == null)
        {
            error = ApiErrors.Refused("not_a_plant",
                $"{Names.Of(thing)} ({thing.GetType().Name}) is not a plant, seed or plant produce.");
            return false;
        }

        return true;
    }
}

/// <summary>plant_genes' three forms.</summary>
internal abstract class GenesRequest
{
    private GenesRequest()
    {
    }

    internal static GenesRequest Parse(Args args)
    {
        bool one = args.Has("reference_id");
        bool many = args.Has("reference_ids");
        if (one == many)
        {
            throw ApiErrors.InvalidArgument("Pass reference_id or reference_ids.");
        }

        if (many)
        {
            args.Reject("reference_ids, which only reads", "genes", "force", "unit");
            return new ReadMany(args.Array("reference_ids", PlantGenesApi.MaximumIds));
        }

        ThingId id = args.ThingId("reference_id");
        int? unit = args.OptionalInt("unit", 0, int.MaxValue);
        JObject? genes = args.OptionalObject("genes");
        if (genes == null)
        {
            args.Reject("a read (without genes)", "force");
            return new Read(id, unit);
        }

        if (!genes.HasValues)
        {
            throw ApiErrors.InvalidArgument(
                "Argument 'genes' must be an object of gene name to value, with at least one gene.");
        }

        return new Write(id, unit, GeneArgs.RequireNumbers(genes), args.OptionalBool("force") ?? false);
    }

    internal sealed class Read : GenesRequest
    {
        internal Read(ThingId id, int? unit)
        {
            Id = id;
            Unit = unit;
        }

        internal ThingId Id { get; }

        internal int? Unit { get; }
    }

    internal sealed class ReadMany : GenesRequest
    {
        internal ReadMany(JArray ids)
        {
            Ids = ids;
        }

        internal JArray Ids { get; }
    }

    internal sealed class Write : GenesRequest
    {
        internal Write(ThingId id, int? unit, JObject genes, bool force)
        {
            Id = id;
            Unit = unit;
            Genes = genes;
            Force = force;
        }

        internal ThingId Id { get; }

        internal int? Unit { get; }

        internal JObject Genes { get; }

        internal bool Force { get; }
    }
}

/// <summary>What a gene's effect is measured in, and for a band gene which end it moves.</summary>
internal enum GeneEffectKind
{
    Seconds,
    Factor,
    LowKelvin,
    HighKelvin,
    LowKpa,
    HighKpa
}

/// <summary>The stat a gene sets, how it reads, and what it means to a player.</summary>
internal sealed class GeneInfo
{
    internal GeneInfo(string stat, GeneEffectKind kind, string meaning)
    {
        Stat = stat;
        Kind = kind;
        Meaning = meaning;
    }

    internal string Stat { get; }

    internal GeneEffectKind Kind { get; }

    internal string Meaning { get; }
}

/// <summary>Every gene's stat (PlantLifeRequirements' fields, read from plantliferequirements.xml).</summary>
internal static class GeneTable
{
    private static readonly Dictionary<Gene, GeneInfo> Infos = new Dictionary<Gene, GeneInfo>
    {
        [Gene.GrowthSpeedMultiplier] = new GeneInfo("GrowthSpeedMultiplier", GeneEffectKind.Factor,
            "Multiplies growth efficiency, so every stage passes faster or slower."),
        [Gene.DarkPerDay] = new GeneInfo("DarknessPerDay", GeneEffectKind.Seconds,
            "Seconds of darkness needed per day; a positive gene needs less."),
        [Gene.LightPerDay] = new GeneInfo("LightPerDay", GeneEffectKind.Seconds,
            "Seconds of light needed per day; a positive gene needs less."),
        [Gene.DroughtTolerance] = new GeneInfo("TimeUntilDehydrationDamage", GeneEffectKind.Seconds,
            "Seconds without water before dehydration damage."),
        [Gene.WaterUsage] = new GeneInfo("WaterUsage", GeneEffectKind.Factor,
            "Multiplies the water drunk per tick."),
        [Gene.LowPressureResistance] = new GeneInfo("GrowPressure", GeneEffectKind.LowKpa,
            "Moves the low end of the pressure band: the ideal minimum and the survivable minimum."),
        [Gene.LowTemperatureResistance] = new GeneInfo("GrowTemperature", GeneEffectKind.LowKelvin,
            "Moves the low end of the temperature band: the ideal minimum and the survivable minimum."),
        [Gene.UndesiredGasTolerance] = new GeneInfo("TimeUntilUndesiredGasDamage", GeneEffectKind.Seconds,
            "Seconds in harmful gas before damage."),
        [Gene.GasProduction] = new GeneInfo("GasProduction", GeneEffectKind.Factor,
            "Multiplies the gas breathed in and out; a positive gene can lift breathing efficiency, and so growth, " +
            "above 1."),
        [Gene.HighPressureResistance] = new GeneInfo("GrowPressure", GeneEffectKind.HighKpa,
            "Moves the high end of the pressure band: the ideal maximum and the survivable maximum."),
        [Gene.HighTemperatureResistance] = new GeneInfo("GrowTemperature", GeneEffectKind.HighKelvin,
            "Moves the high end of the temperature band: the ideal maximum and the survivable maximum."),
        [Gene.SuffocationTolerance] = new GeneInfo("TimeUntilSuffocatedDamage", GeneEffectKind.Seconds,
            "Seconds without the gas it breathes before damage."),
        [Gene.LowPressureTolerance] = new GeneInfo("TimeUntilLowPressureDamage", GeneEffectKind.Seconds,
            "Seconds below the survivable pressure before damage."),
        [Gene.LowTemperatureTolerance] = new GeneInfo("TimeUntilFrozenDamage", GeneEffectKind.Seconds,
            "Seconds below the survivable temperature before damage."),
        [Gene.HighPressureTolerance] = new GeneInfo("TimeUntilHighPressureDamage", GeneEffectKind.Seconds,
            "Seconds above the survivable pressure before damage."),
        [Gene.HighTemperatureTolerance] = new GeneInfo("TimeUntilOverHeatedDamage", GeneEffectKind.Seconds,
            "Seconds above the survivable temperature before damage."),
        [Gene.UndesiredGasResistance] = new GeneInfo("UndesiredGasResistance", GeneEffectKind.Factor,
            "Multiplies the partial pressure of each harmful gas the plant takes before it counts as polluted."),
        [Gene.LightTolerance] = new GeneInfo("TimeUntilLightDamage", GeneEffectKind.Seconds,
            "Seconds of too much light before damage."),
        [Gene.DarknessTolerance] = new GeneInfo("TimeUntilDarknessDamage", GeneEffectKind.Seconds,
            "Seconds of too much darkness before damage.")
    };

    internal static GeneInfo? Of(Gene gene) => Infos.TryGetValue(gene, out GeneInfo info) ? info : null;

    /// <summary>A gene by the enum name, ignoring case, or by the save's name for GrowthSpeedMultiplier.</summary>
    internal static Gene? Parse(string name)
    {
        // Gene.GrowthSpeedMultiplier's XmlEnum name in saves.
        if (string.Equals(name, "GrowthTimeMultiplier", StringComparison.OrdinalIgnoreCase))
        {
            return Gene.GrowthSpeedMultiplier;
        }

        foreach (Gene gene in GeneCollection.Genes)
        {
            if (string.Equals(gene.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                return gene;
            }
        }

        return null;
    }

    internal static PlantStat StatFor(PlantLifeRequirements needs, Gene gene)
    {
        switch (gene)
        {
            case Gene.GrowthSpeedMultiplier: return needs.GrowthSpeedMultiplier;
            case Gene.DarkPerDay: return needs.DarknessPerDay;
            case Gene.LightPerDay: return needs.LightPerDay;
            case Gene.DroughtTolerance: return needs.TimeUntilDehydrationDamage;
            case Gene.WaterUsage: return needs.WaterUsage;
            case Gene.UndesiredGasTolerance: return needs.TimeUntilUndesiredGasDamage;
            case Gene.GasProduction: return needs.GasProduction;
            case Gene.SuffocationTolerance: return needs.TimeUntilSuffocatedDamage;
            case Gene.LowPressureTolerance: return needs.TimeUntilLowPressureDamage;
            case Gene.LowTemperatureTolerance: return needs.TimeUntilFrozenDamage;
            case Gene.HighPressureTolerance: return needs.TimeUntilHighPressureDamage;
            case Gene.HighTemperatureTolerance: return needs.TimeUntilOverHeatedDamage;
            case Gene.UndesiredGasResistance: return needs.UndesiredGasResistance;
            case Gene.LightTolerance: return needs.TimeUntilLightDamage;
            case Gene.DarknessTolerance: return needs.TimeUntilDarknessDamage;
            default: throw new ApiException(ApiErrors.GameChangedCode, $"Gene {gene} has no known stat.");
        }
    }
}

/// <summary>One gene set of a plant: which unit, and how the plant holds it.</summary>
internal static class GeneSets
{
    internal static bool TryPick(Plant plant, int? unit, out GeneCollection? set, out GeneSetHeader? header,
        out ApiException? error)
    {
        set = null;
        header = null;
        error = null;
        List<GeneCollection> sets = plant.StackedGeneCollections;
        if (sets == null || sets.Count == 0)
        {
            error = ApiErrors.Refused("no_genes", $"{Names.Of(plant)} has no gene set yet.");
            return false;
        }

        int index = unit ?? sets.Count - 1;
        if (index >= sets.Count)
        {
            error = ApiErrors.Refused("unit_out_of_range",
                $"{Names.Of(plant)} has {sets.Count} gene set(s), the top one last: " +
                $"unit must be 0 to {sets.Count - 1}.");
            return false;
        }

        set = sets[index];
        if (set == null)
        {
            error = ApiErrors.Refused("no_genes", $"Gene set {index} of {Names.Of(plant)} is empty.");
            return false;
        }

        header = new GeneSetHeader(GameLookup.ViewOf(plant), Holder(plant), index, index == sets.Count - 1);
        return true;
    }

    private static string Holder(Plant plant)
    {
        if (plant is Seed)
        {
            return "seed";
        }

        return plant.ParentTray != null ? "planted_plant" : "produce";
    }
}

/// <summary>Reads a gene set with each gene's effect on the plant's stats.</summary>
internal static class GeneReader
{
    private const double CelsiusToKelvin = 273.15;

    internal static PlantGenesView Read(Plant plant, int? unit)
    {
        if (!TryRead(plant, unit, out PlantGenesView? view, out ApiException? error))
        {
            throw error!;
        }

        return view!;
    }

    internal static bool TryRead(Plant plant, int? unit, out PlantGenesView? view, out ApiException? error)
    {
        view = null;
        if (!GeneSets.TryPick(plant, unit, out GeneCollection? set, out GeneSetHeader? header, out error))
        {
            return false;
        }

        List<GeneView> genes = new List<GeneView>(GeneCollection.Genes.Length);
        foreach (Gene gene in GeneCollection.Genes)
        {
            set!.Lookup.TryGetValue(gene, out GeneWrapper? wrapper);
            GeneInfo? info = GeneTable.Of(gene);
            object? effect = wrapper == null ? null : EffectOf(plant, gene, wrapper.Value);
            genes.Add(new GeneView(gene.ToString(), wrapper?.Value, wrapper?.Stability, info?.Meaning, effect));
        }

        view = new PlantGenesView(header!, plant.StackedGeneCollections.Count, genes);
        return true;
    }

    // The stat a gene sets, at the gene's current value and at both ends of its range.
    private static object? EffectOf(Plant plant, Gene gene, float value)
    {
        GeneInfo? info = GeneTable.Of(gene);
        PlantLifeRequirements? needs = plant.lifeRequirements;
        if (info == null || needs?.Data == null)
        {
            return null;
        }

        switch (info.Kind)
        {
            case GeneEffectKind.Seconds:
                return new SecondsEffectView(info.Stat, Spread(GeneTable.StatFor(needs, gene), value));
            case GeneEffectKind.Factor:
                return new FactorEffectView(info.Stat, Spread(GeneTable.StatFor(needs, gene), value));
            case GeneEffectKind.LowKelvin:
            case GeneEffectKind.HighKelvin:
                return BandEffect(info, needs.GrowTemperatureC, CelsiusToKelvin, value);
            default:
                return BandEffect(info, needs.GrowPressure, 0.0, value);
        }
    }

    private static StatSpread Spread(PlantStat stat, float value)
    {
        bool invert = (bool)GameMembers.PlantStatInvert.GetValue(stat)!;
        return new StatSpread(stat.Base, StatAt(stat, invert, value), StatAt(stat, invert, GeneView.Minimum),
            StatAt(stat, invert, GeneView.Maximum));
    }

    // PlantStat.Get for a given gene value instead of the plant's top gene set.
    private static float StatAt(PlantStat stat, bool invert, float value)
    {
        float toward = value > 0f ? (invert ? stat.Min : stat.Max) : (invert ? stat.Max : stat.Min);
        return UnityEngine.Mathf.Lerp(stat.Base, toward, Math.Abs(value));
    }

    private static BandEffectView BandEffect(GeneInfo info, MultiPlantStat band, double offset, float value) =>
        new BandEffectView(info.Stat, BandEnd(info.Kind, band, offset, value),
            BandEnd(info.Kind, band, offset, GeneView.Minimum), BandEnd(info.Kind, band, offset, GeneView.Maximum));

    private static object BandEnd(GeneEffectKind kind, MultiPlantStat band, double offset, float gene)
    {
        switch (kind)
        {
            case GeneEffectKind.LowKelvin:
                return new BandLowKView(band.IdealMin(gene) + offset, band.Min(gene) + offset);
            case GeneEffectKind.HighKelvin:
                return new BandHighKView(band.IdealMax(gene) + offset, band.Max(gene) + offset);
            case GeneEffectKind.LowKpa:
                return new BandLowKpaView(band.IdealMin(gene) + offset, band.Min(gene) + offset);
            default:
                return new BandHighKpaView(band.IdealMax(gene) + offset, band.Max(gene) + offset);
        }
    }
}

/// <summary>Writes genes into one gene set, as PlantGeneticSplicer.StartSplice does.</summary>
internal static class GeneWriter
{
    // Plant.BuildUpdate sends the top gene set when this network flag is set.
    private const ushort GenesNetworkFlag = 512;

    internal static GenesWrittenView Write(Plant plant, GenesRequest.Write request)
    {
        if (NetworkManager.IsClient)
        {
            throw ApiErrors.Refused("not_host",
                "This game is a multiplayer client; only the host can change genes.");
        }

        if (!GeneSets.TryPick(plant, request.Unit, out GeneCollection? set, out GeneSetHeader? header,
                out ApiException? error))
        {
            throw error!;
        }

        BatchResultView results = WriteAll(set!, request);
        if (results.SuccessCount > 0 && header!.IsTop)
        {
            // The only gene-derived state the game caches; everything else reads the genes live.
            plant.lifeRequirements?.UpdateTemperaturePressureCurves();
            if (NetworkManager.IsServer)
            {
                plant.NetworkUpdateFlags |= GenesNetworkFlag;
            }
        }

        return new GenesWrittenView(header!, results);
    }

    private static BatchResultView WriteAll(GeneCollection set, GenesRequest.Write request)
    {
        BatchBuilder batch = new BatchBuilder(request.Genes.Count);
        int index = 0;
        foreach (JProperty property in request.Genes.Properties())
        {
            if (TryWrite(set, property, request.Force, index, out BatchItemView item))
            {
                batch.Succeeded(item);
            }
            else
            {
                batch.Failed(item);
            }

            index++;
        }

        return batch.Build();
    }

    private static bool TryWrite(GeneCollection set, JProperty property, bool force, int index,
        out BatchItemView item)
    {
        Gene? parsed = GeneTable.Parse(property.Name);
        if (!parsed.HasValue)
        {
            item = new GeneNotWrittenView(index, property.Name, null, ApiErrors.Refused("unknown_gene",
                $"'{property.Name}' is not a gene. Genes: {string.Join(", ", GeneCollection.Genes)}."));
            return false;
        }

        Gene gene = parsed.Value;
        set.Lookup.TryGetValue(gene, out GeneWrapper? wrapper);
        float? previous = wrapper?.Value;
        if (!TryValue(gene, property.Value, force, out double value, out ApiException? error))
        {
            item = new GeneNotWrittenView(index, property.Name, previous, error!);
            return false;
        }

        if (wrapper == null)
        {
            // Every gene set the game makes has all 19 genes; a missing one is added as a new gene would be.
            wrapper = new GeneWrapper(gene, 0f, 0f);
            set.Lookup[gene] = wrapper;
        }

        wrapper.Value = (float)value;
        item = new GeneWrittenView(index, gene.ToString(), previous, wrapper.Value);
        return true;
    }

    private static bool TryValue(Gene gene, JToken token, bool force, out double value, out ApiException? error)
    {
        // GenesRequest.Parse has refused any value that is not a number.
        error = null;
        value = token.Value<double>();
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            error = ApiErrors.InvalidArgument($"Gene {gene}: the value must be finite.");
            return false;
        }

        if (!force && (value < GeneView.Minimum || value > GeneView.Maximum))
        {
            error = ApiErrors.Refused("out_of_range",
                $"Gene {gene}: {value} is outside the game's range {GeneView.Minimum} to {GeneView.Maximum}; " +
                "pass force to write it anyway.");
            return false;
        }

        return true;
    }
}
