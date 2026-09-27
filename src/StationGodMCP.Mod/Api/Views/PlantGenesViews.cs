#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>plant_genes read: one gene set of a plant item, every gene with its range, meaning and effect.</summary>
internal sealed class PlantGenesView
{
    internal PlantGenesView(GeneSetHeader header, int geneSetCount, List<GeneView> genes)
    {
        Thing = header.Thing;
        Holder = header.Holder;
        GeneSetCount = geneSetCount;
        Unit = header.Unit;
        IsTop = header.IsTop;
        Genes = genes;
    }

    public ThingView Thing { get; }

    /// <summary>planted_plant, seed or produce.</summary>
    public string Holder { get; }

    public int GeneSetCount { get; }

    public int Unit { get; }

    public bool IsTop { get; }

    public List<GeneView> Genes { get; }
}

/// <summary>Which plant and which of its gene sets a reply is about.</summary>
internal sealed class GeneSetHeader
{
    internal GeneSetHeader(ThingView thing, string holder, int unit, bool isTop)
    {
        Thing = thing;
        Holder = holder;
        Unit = unit;
        IsTop = isTop;
    }

    internal ThingView Thing { get; }

    internal string Holder { get; }

    internal int Unit { get; }

    internal bool IsTop { get; }
}

/// <summary>plant_genes' batch read: index and ok, then the same fields as PlantGenesView.</summary>
internal sealed class PlantGenesItemView : BatchItemView
{
    private readonly PlantGenesView _genes;

    internal PlantGenesItemView(int index, PlantGenesView genes) : base(index, ok: true)
    {
        _genes = genes;
    }

    public ThingView Thing => _genes.Thing;

    public string Holder => _genes.Holder;

    public int GeneSetCount => _genes.GeneSetCount;

    public int Unit => _genes.Unit;

    public bool IsTop => _genes.IsTop;

    public List<GeneView> Genes => _genes.Genes;
}

internal sealed class GeneView
{
    internal const float Minimum = -1f;
    internal const float Maximum = 1f;

    internal GeneView(string gene, float? value, float? stability, string? meaning, object? effect)
    {
        Gene = gene;
        Value = value;
        Stability = stability;
        Meaning = meaning;
        Effect = effect;
    }

    public string Gene { get; }

    public float? Value { get; }

    public float Min => Minimum;

    public float Max => Maximum;

    public float? Stability { get; }

    public float StabilityMin => Minimum;

    public float StabilityMax => Maximum;

    public string? Meaning { get; }

    /// <summary>A SecondsEffectView, FactorEffectView or BandEffectView; null when the gene is missing.</summary>
    public object? Effect { get; }
}

/// <summary>A gene's stat in seconds, at the base, now, and at both ends of the gene's range.</summary>
internal sealed class SecondsEffectView
{
    internal SecondsEffectView(string stat, StatSpread spread)
    {
        Stat = stat;
        BaseS = spread.Base;
        NowS = spread.Now;
        AtMinS = spread.AtMin;
        AtMaxS = spread.AtMax;
    }

    public string Stat { get; }

    public float BaseS { get; }

    public float NowS { get; }

    public float AtMinS { get; }

    public float AtMaxS { get; }
}

/// <summary>A gene's stat as a multiplier, at the base, now, and at both ends of the gene's range.</summary>
internal sealed class FactorEffectView
{
    internal FactorEffectView(string stat, StatSpread spread)
    {
        Stat = stat;
        BaseFactor = spread.Base;
        NowFactor = spread.Now;
        AtMinFactor = spread.AtMin;
        AtMaxFactor = spread.AtMax;
    }

    public string Stat { get; }

    public float BaseFactor { get; }

    public float NowFactor { get; }

    public float AtMinFactor { get; }

    public float AtMaxFactor { get; }
}

/// <summary>A PlantStat at the base and at three gene values.</summary>
internal sealed class StatSpread
{
    internal StatSpread(float baseValue, float now, float atMin, float atMax)
    {
        Base = baseValue;
        Now = now;
        AtMin = atMin;
        AtMax = atMax;
    }

    internal float Base { get; }

    internal float Now { get; }

    internal float AtMin { get; }

    internal float AtMax { get; }
}

/// <summary>A band gene's end of the temperature or pressure band, now and at both ends of the gene's range.</summary>
internal sealed class BandEffectView
{
    internal BandEffectView(string stat, object now, object atMin, object atMax)
    {
        Stat = stat;
        Now = now;
        AtMin = atMin;
        AtMax = atMax;
    }

    public string Stat { get; }

    public object Now { get; }

    public object AtMin { get; }

    public object AtMax { get; }
}

internal sealed class BandLowKView
{
    internal BandLowKView(double idealMin, double min)
    {
        IdealMinK = idealMin;
        MinK = min;
    }

    public double IdealMinK { get; }

    public double MinK { get; }
}

internal sealed class BandHighKView
{
    internal BandHighKView(double idealMax, double max)
    {
        IdealMaxK = idealMax;
        MaxK = max;
    }

    public double IdealMaxK { get; }

    public double MaxK { get; }
}

internal sealed class BandLowKpaView
{
    internal BandLowKpaView(double idealMin, double min)
    {
        IdealMinKpa = idealMin;
        MinKpa = min;
    }

    public double IdealMinKpa { get; }

    public double MinKpa { get; }
}

internal sealed class BandHighKpaView
{
    internal BandHighKpaView(double idealMax, double max)
    {
        IdealMaxKpa = idealMax;
        MaxKpa = max;
    }

    public double IdealMaxKpa { get; }

    public double MaxKpa { get; }
}

/// <summary>plant_genes write: each gene written or refused, on one gene set.</summary>
internal sealed class GenesWrittenView
{
    internal GenesWrittenView(GeneSetHeader header, BatchResultView results)
    {
        Thing = header.Thing;
        Holder = header.Holder;
        Unit = header.Unit;
        IsTop = header.IsTop;
        Results = results.Results;
        Count = results.Count;
        SuccessCount = results.SuccessCount;
        ErrorCount = results.ErrorCount;
    }

    public ThingView Thing { get; }

    public string Holder { get; }

    public int Unit { get; }

    public bool IsTop { get; }

    public List<BatchItemView> Results { get; }

    public int Count { get; }

    public int SuccessCount { get; }

    public int ErrorCount { get; }
}

internal sealed class GeneWrittenView : BatchItemView
{
    internal GeneWrittenView(int index, string gene, float? previousValue, float value) : base(index, ok: true)
    {
        Gene = gene;
        PreviousValue = previousValue;
        Value = value;
    }

    public string Gene { get; }

    public float? PreviousValue { get; }

    public float Value { get; }
}

internal sealed class GeneNotWrittenView : BatchItemView
{
    internal GeneNotWrittenView(int index, string gene, float? previousValue, ApiException error)
        : base(index, ok: false)
    {
        Gene = gene;
        PreviousValue = previousValue;
        Error = new ErrorView(error.Code, error.Message);
    }

    public string Gene { get; }

    public float? PreviousValue { get; }

    public ErrorView Error { get; }
}
