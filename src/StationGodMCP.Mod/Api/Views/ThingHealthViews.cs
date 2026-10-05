#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>thing_health: one thing's damage state.</summary>
internal sealed class HealthView
{
    internal HealthView(ThingView thing, string kind, string type, DamageReading damage, HealthFlags flags,
        PositionView position, double? distanceM, string? customName = null, List<NetworkRefView>? networks = null,
        BuildStateView? buildState = null)
    {
        BuildState = buildState;
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        DisplayName = thing.DisplayName;
        CustomName = customName;
        Condition = flags.Condition;
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
        BrokenBuildState = flags.BrokenBuildState;
        BeingDestroyed = flags.BeingDestroyed;
        PipeBurst = flags.PipeBurst;
        DamageRecord = flags.DamageRecord;
        Position = position;
        DistanceM = distanceM;
        Networks = networks;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>The Labeller's name, null when it has none.</summary>
    public string? CustomName { get; }

    /// <summary>The one-thing form, a structure with build states: where it stands and what the next state takes.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public BuildStateView? BuildState { get; }

    /// <summary>
    /// broken (the game's broken state, whatever the numbers say), damaged, intact, indestructible or none
    /// (Pure/HealthCondition).
    /// </summary>
    public string Condition { get; }

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

    /// <summary>
    /// Structures only: it stands in its broken build state (build state below 0), the broken mesh the game swaps in
    /// and then heals, so its damage reads 0; null for things that are not structures.
    /// </summary>
    public bool? BrokenBuildState { get; }

    public bool BeingDestroyed { get; }

    /// <summary>
    /// Pipes only: none, or the burst state; the game sets pressure for any pipe broken by damage, so damage_record
    /// says the cause.
    /// </summary>
    public string? PipeBurst { get; }

    /// <summary>Pipes only: every cause that has damaged it (pressure, liquid, solid), as the tooltip names them.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? DamageRecord { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }

    /// <summary>Structures only: the cable, pipe and chute networks it is part of or its ends join; left out otherwise.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<NetworkRefView>? Networks { get; }
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
    internal HealthFlags(string? band, bool isBroken, bool beingDestroyed, string? pipeBurst,
        string condition = HealthCondition.Intact, bool? brokenBuildState = null, List<string>? damageRecord = null)
    {
        DamageRecord = damageRecord;
        Band = band;
        IsBroken = isBroken;
        BeingDestroyed = beingDestroyed;
        PipeBurst = pipeBurst;
        Condition = condition;
        BrokenBuildState = brokenBuildState;
    }

    internal string Condition { get; }

    internal bool? BrokenBuildState { get; }

    internal string? Band { get; }

    internal bool IsBroken { get; }

    internal bool BeingDestroyed { get; }

    internal string? PipeBurst { get; }

    internal List<string>? DamageRecord { get; }
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

    public string? CustomName => _health.CustomName;

    public string Condition => _health.Condition;

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

    public bool? BrokenBuildState => _health.BrokenBuildState;

    public bool BeingDestroyed => _health.BeingDestroyed;

    public string? PipeBurst => _health.PipeBurst;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? DamageRecord => _health.DamageRecord;

    public PositionView Position => _health.Position;

    public double? DistanceM => _health.DistanceM;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<NetworkRefView>? Networks => _health.Networks;
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

    /// <summary>Damaged or broken things matching the filters, on every page.</summary>
    public int Total { get; }

    public int Structures { get; }

    /// <summary>Things in the game's broken state among the matches (counted before paging).</summary>
    public int Broken { get; }

    public int Scanned { get; }

    public double MinDamageRatio { get; }

    public int Offset { get; }

    public int Limit { get; }

    public bool HasMore { get; }

    public LocalPlayerView? LocalPlayer { get; }
}

/// <summary>thing_health network_id: the network's pieces, worst first, paged (only the damaged ones with damaged_only).</summary>
internal sealed class HealthNetworkView
{
    internal HealthNetworkView(ThingId networkId, string kind, int pieces, bool damagedOnly, Slice<HealthView> page,
        BrokenNeighbourReport broken)
    {
        Warning = broken.Warning;
        BrokenNeighbourCount = broken.Count;
        BrokenNeighbours = broken.OrNull;
        NetworkId = networkId;
        Kind = kind;
        Pieces = pieces;
        DamagedOnly = damagedOnly;
        Things = page.Items;
        Count = page.Items.Count;
        Total = page.Total;
        Offset = page.Offset;
        Limit = page.Limit;
        HasMore = page.HasMore;
    }

    public ThingId NetworkId { get; }

    /// <summary>pipe, cable or chute.</summary>
    public string Kind { get; }

    /// <summary>Every piece on the network, damaged or not.</summary>
    public int Pieces { get; }

    public bool DamagedOnly { get; }

    /// <summary>Broken pieces touching the network that things does not list, damaged_only or not; left out when none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Warning { get; }

    /// <summary>How many broken pieces touch the network at its ends without being on it (0: none).</summary>
    public int BrokenNeighbourCount { get; }

    /// <summary>The first of them (see broken_neighbour_count); left out when none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<BrokenNeighbourView>? BrokenNeighbours { get; }

    public List<HealthView> Things { get; }

    public int Count { get; }

    /// <summary>The pieces listed on every page: all of them, or the damaged and broken ones with damaged_only.</summary>
    public int Total { get; }

    public int Offset { get; }

    public int Limit { get; }

    public bool HasMore { get; }
}
