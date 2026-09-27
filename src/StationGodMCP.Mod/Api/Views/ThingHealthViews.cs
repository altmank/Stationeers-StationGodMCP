#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>thing_health: one thing's damage state.</summary>
internal sealed class HealthView
{
    internal HealthView(ThingView thing, string kind, string type, DamageReading damage, HealthFlags flags,
        PositionView position, double? distanceM)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        Kind = kind;
        Type = type;
        DamageState = damage.State;
        DamageStateClass = damage.StateClass;
        MaxDamage = damage.MaxDamage;
        TotalDamage = damage.TotalDamage;
        DamageRatio = damage.DamageRatio;
        HealthPercent = damage.HealthPercent;
        Band = flags.Band;
        Damage = damage.Parts;
        IsBroken = flags.IsBroken;
        BeingDestroyed = flags.BeingDestroyed;
        PipeBurst = flags.PipeBurst;
        Position = position;
        DistanceM = distanceM;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>structure, item or other.</summary>
    public string Kind { get; }

    /// <summary>The runtime class.</summary>
    public string Type { get; }

    /// <summary>destructible, indestructible or none.</summary>
    public string DamageState { get; }

    public string? DamageStateClass { get; }

    public float? MaxDamage { get; }

    public double? TotalDamage { get; }

    /// <summary>TotalDamage / MaxDamage: 0 like new, 1 destroyed; null when indestructible.</summary>
    public double? DamageRatio { get; }

    public int? HealthPercent { get; }

    /// <summary>Solar panels only: the tooltip colour (SolarPanel.DamageColor).</summary>
    public string? Band { get; }

    public DamagePartsView? Damage { get; }

    public bool IsBroken { get; }

    public bool BeingDestroyed { get; }

    /// <summary>Pipes only: none, pressure, liquid or solid.</summary>
    public string? PipeBurst { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }
}

/// <summary>A thing's damage numbers, for HealthView.</summary>
internal sealed class DamageReading
{
    internal DamageReading(string state, string? stateClass, float? maxDamage, double? totalDamage,
        double? damageRatio, int? healthPercent, DamagePartsView? parts)
    {
        State = state;
        StateClass = stateClass;
        MaxDamage = maxDamage;
        TotalDamage = totalDamage;
        DamageRatio = damageRatio;
        HealthPercent = healthPercent;
        Parts = parts;
    }

    internal string State { get; }

    internal string? StateClass { get; }

    internal float? MaxDamage { get; }

    internal double? TotalDamage { get; }

    internal double? DamageRatio { get; }

    internal int? HealthPercent { get; }

    internal DamagePartsView? Parts { get; }
}

/// <summary>The state flags beside the damage, for HealthView.</summary>
internal sealed class HealthFlags
{
    internal HealthFlags(string? band, bool isBroken, bool beingDestroyed, string? pipeBurst)
    {
        Band = band;
        IsBroken = isBroken;
        BeingDestroyed = beingDestroyed;
        PipeBurst = pipeBurst;
    }

    internal string? Band { get; }

    internal bool IsBroken { get; }

    internal bool BeingDestroyed { get; }

    internal string? PipeBurst { get; }
}

/// <summary>Each kind of damage, as IndestructableDamageState keeps it.</summary>
internal sealed class DamagePartsView
{
    internal DamagePartsView(float brute, float burn, float oxygen, float hydration, float starvation, float toxic,
        float radiation, float decay, float stun)
    {
        Brute = brute;
        Burn = burn;
        Oxygen = oxygen;
        Hydration = hydration;
        Starvation = starvation;
        Toxic = toxic;
        Radiation = radiation;
        Decay = decay;
        Stun = stun;
    }

    public float Brute { get; }

    public float Burn { get; }

    public float Oxygen { get; }

    public float Hydration { get; }

    public float Starvation { get; }

    public float Toxic { get; }

    public float Radiation { get; }

    public float Decay { get; }

    public float Stun { get; }
}

/// <summary>A thing's damage state as a batch item: index and ok, then the same fields as HealthView.</summary>
internal sealed class HealthItemView : BatchItemView
{
    private readonly HealthView _health;

    internal HealthItemView(int index, HealthView health) : base(index, ok: true)
    {
        _health = health;
    }

    public ThingId ReferenceId => _health.ReferenceId;

    public string? PrefabName => _health.PrefabName;

    public string? DisplayName => _health.DisplayName;

    public string Kind => _health.Kind;

    public string Type => _health.Type;

    public string DamageState => _health.DamageState;

    public string? DamageStateClass => _health.DamageStateClass;

    public float? MaxDamage => _health.MaxDamage;

    public double? TotalDamage => _health.TotalDamage;

    public double? DamageRatio => _health.DamageRatio;

    public int? HealthPercent => _health.HealthPercent;

    public string? Band => _health.Band;

    public DamagePartsView? Damage => _health.Damage;

    public bool IsBroken => _health.IsBroken;

    public bool BeingDestroyed => _health.BeingDestroyed;

    public string? PipeBurst => _health.PipeBurst;

    public PositionView Position => _health.Position;

    public double? DistanceM => _health.DistanceM;
}

/// <summary>thing_health's scan: the damaged things of the world, worst first, one page of them.</summary>
internal sealed class HealthScanView
{
    internal HealthScanView(Slice<HealthView> page, int structures, int broken, int scanned, double minDamageRatio,
        LocalPlayerView? localPlayer)
    {
        Things = page.Items;
        Count = page.Items.Count;
        Total = page.Total;
        Structures = structures;
        Broken = broken;
        Scanned = scanned;
        MinDamageRatio = minDamageRatio;
        Offset = page.Offset;
        Limit = page.Limit;
        HasMore = page.HasMore;
        LocalPlayer = localPlayer;
    }

    public List<HealthView> Things { get; }

    public int Count { get; }

    /// <summary>Damaged things matching the filters, on every page.</summary>
    public int Total { get; }

    public int Structures { get; }

    public int Broken { get; }

    public int Scanned { get; }

    public double MinDamageRatio { get; }

    public int Offset { get; }

    public int Limit { get; }

    public bool HasMore { get; }

    public LocalPlayerView? LocalPlayer { get; }
}
