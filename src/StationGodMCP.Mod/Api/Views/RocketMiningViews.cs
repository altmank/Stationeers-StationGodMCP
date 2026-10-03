#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Rockets;

namespace StationGodMCP.Api.Views;

/// <summary>One thing a deposit lists: ore (an ore prefab or Space Ore with its reagents per unit), ice (gas per unit), or gas.</summary>
internal sealed class SiteMaterialView
{
    internal SiteMaterialView(string kind, string prefab, double weight, Dictionary<string, double>? perUnit)
    {
        Kind = kind;
        Prefab = prefab;
        Weight = RocketRound.Of(weight, 3);
        PerUnit = perUnit;
    }

    public string Kind { get; }

    public string Prefab { get; }

    /// <summary>Its weight in the deposit's random pick (ore and reagent lists only decide what a cycle digs).</summary>
    public double Weight { get; }

    /// <summary>Reagent grams (ore) or gas moles (ice, gas) in one unit.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, double>? PerUnit { get; }
}

/// <summary>A Mining-Drill Head: its serialized multipliers and the durability left.</summary>
internal sealed class DrillHeadView
{
    internal DrillHeadView(DrillHeadReading head)
    {
        Name = head.Name;
        Speed = RocketRound.Of(head.Speed, 3);
        IceYield = RocketRound.Of(head.IceYield, 3);
        OreYield = RocketRound.Of(head.OreYield, 3);
        Health = RocketRound.Of(head.Health, 3);
        PowerFactor = RocketRound.Of(head.Power, 3);
        Durability = RocketRound.Of(head.Quantity, 1);
        MaxDurability = RocketRound.Of(head.MaxQuantity, 1);
        ExpectedCyclesLeft = RocketRound.Of(head.CyclesLeft, 0);
        Mines = head.IceYield > 0f && head.OreYield > 0f ? "ice and ore"
            : head.IceYield > 0f ? "ice only"
            : head.OreYield > 0f ? "ore only" : "nothing";
    }

    public string Name { get; }

    /// <summary>SpeedMultiplier: the cycle runs this many times faster.</summary>
    public double Speed { get; }

    /// <summary>IceYieldMultiplier: ice units per cycle are multiplied by it (0 gives 0 ice).</summary>
    public double IceYield { get; }

    /// <summary>ReagentYieldMultiplier: Space Ore units per cycle are multiplied by it (0 gives 0 ore).</summary>
    public double OreYield { get; }

    /// <summary>HealthMultiplier: a cycle wears 1 durability with chance 0.1 / health.</summary>
    public double Health { get; }

    /// <summary>PowerConsumptionMultiplier on the miner's 100 W.</summary>
    public double PowerFactor { get; }

    public double Durability { get; }

    public double MaxDurability { get; }

    /// <summary>Durability x 10 x health: cycles until it is expected to wear out.</summary>
    public double ExpectedCyclesLeft { get; }

    /// <summary>ice and ore, ice only, ore only, or nothing (from the yield factors).</summary>
    public string Mines { get; }
}

internal sealed class MinerView
{
    internal MinerView(ThingId referenceId, MinerReading miner)
    {
        ReferenceId = referenceId;
        Name = miner.Name;
        On = miner.On;
        Head = miner.Head != null ? new DrillHeadView(miner.Head) : null;
        PowerW = RocketRound.Of(miner.PowerW, 1);
        OreSpeed = miner.OreSpeed;
        IceSpeed = miner.IceSpeed;
        HoldOnBoard = miner.ExportsToHold;
    }

    public ThingId ReferenceId { get; }

    public string Name { get; }

    public bool On { get; }

    /// <summary>The head in its slot; null: none (the miner refuses to mine).</summary>
    public DrillHeadView? Head { get; }

    /// <summary>Its draw while mining: 100 W x the head's power factor.</summary>
    public double PowerW { get; }

    /// <summary>The miner's own speed factor on ore deposits (prefab value).</summary>
    public double OreSpeed { get; }

    /// <summary>The miner's own speed factor on ice deposits (prefab value).</summary>
    public double IceSpeed { get; }

    /// <summary>A cargo hold is on board for its chute export (without one it stops at a full export slot).</summary>
    public bool HoldOnBoard { get; }
}

internal sealed class CollectorView
{
    internal CollectorView(ThingId referenceId, CollectorReading collector)
    {
        ReferenceId = referenceId;
        Name = collector.Name;
        On = collector.On;
        HasPipeNetwork = collector.HasPipeNetwork;
    }

    public ThingId ReferenceId { get; }

    public string Name { get; }

    public bool On { get; }

    /// <summary>It refuses without a pipe network on its output; it only equalises with it, so a pump must fill tanks.</summary>
    public bool HasPipeNetwork { get; }
}

internal sealed class ScannerView
{
    internal ScannerView(ThingId referenceId, string name, bool on, string headKind, string? head, int points,
        double cycleS)
    {
        ReferenceId = referenceId;
        Name = name;
        On = on;
        HeadKind = headKind;
        Head = head;
        Points = points;
        CycleS = cycleS;
        Does = headKind == "surface"
            ? "Surface Scan only (at the planet's orbit node); refused for chart, discover and survey"
            : headKind == "normal"
                ? "chart, discover and survey; refused for Surface Scan"
                : "nothing: no head (every scan is refused)";
    }

    public ThingId ReferenceId { get; }

    public string Name { get; }

    public bool On { get; }

    /// <summary>normal (Rocket Scanner Head), surface (Rocket Surface Scanner Head), or none.</summary>
    public string HeadKind { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Head { get; }

    /// <summary>RocketScanner.Points per cycle (the prefab's value).</summary>
    public int Points { get; }

    /// <summary>RocketScanner.CycleTime (the prefab's value).</summary>
    public double CycleS { get; }

    public string Does { get; }
}

internal sealed class CargoSummaryView
{
    internal CargoSummaryView(int holds, int slots, int filled)
    {
        Holds = holds;
        Slots = slots;
        Filled = filled;
        Free = System.Math.Max(0, slots - filled);
    }

    public int Holds { get; }

    public int Slots { get; }

    public int Filled { get; }

    public int Free { get; }
}

/// <summary>What the rocket carries for mining, scanning and storing.</summary>
internal sealed class MiningLoadoutView
{
    internal MiningLoadoutView(List<MinerView> miners, List<CollectorView> collectors, List<ScannerView> scanners,
        CargoSummaryView cargo, double gasTankLitres, double liquidTankLitres, List<string> collects)
    {
        Miners = miners;
        Collectors = collectors;
        Scanners = scanners;
        Cargo = cargo;
        GasTankLitres = RocketRound.Of(gasTankLitres, 0);
        LiquidTankLitres = RocketRound.Of(liquidTankLitres, 0);
        Collects = collects;
    }

    public List<MinerView> Miners { get; }

    public List<CollectorView> Collectors { get; }

    public List<ScannerView> Scanners { get; }

    public CargoSummaryView Cargo { get; }

    /// <summary>Litres of gas tanks on board (where collected gas can be pumped).</summary>
    public double GasTankLitres { get; }

    public double LiquidTankLitres { get; }

    /// <summary>The site kinds this loadout can bring home: ore, ice, gas; map work: chart/discover/survey, surface_scan.</summary>
    public List<string> Collects { get; }
}

/// <summary>One machine's yield at a site.</summary>
internal sealed class MachineYieldView
{
    internal MachineYieldView(MachineYield yield, int stackSize)
    {
        Machine = yield.Machine;
        Collects = yield.Collects;
        UnitsPerCycle = yield.UnitsPerCycle;
        CycleS = double.IsInfinity(yield.CycleSeconds) ? null : RocketRound.Of(yield.CycleSeconds, 1);
        UnitsPerHour = RocketRound.Of(yield.UnitsPerHour, 0);
        SlotsPerHour = yield.Collects == "gas" || stackSize <= 0 ? null : RocketRound.Of(yield.UnitsPerHour / stackSize, 1);
        CyclesToDeplete = yield.CyclesToDeplete.HasValue && !double.IsInfinity(yield.CyclesToDeplete.Value)
            ? RocketRound.Of(yield.CyclesToDeplete.Value, 0)
            : null;
        TotalUnitsLeft = yield.TotalUnitsLeft.HasValue ? RocketRound.Of(yield.TotalUnitsLeft.Value, 0) : null;
        HeadCyclesLeft = yield.HeadCyclesLeft.HasValue ? RocketRound.Of(yield.HeadCyclesLeft.Value, 0) : null;
        Problems = yield.Problems.Count == 0 ? null : yield.Problems;
        Collected = yield.Collected;
    }

    public string Machine { get; }

    /// <summary>ore, ice, gas or nothing.</summary>
    public string Collects { get; }

    public bool Collected { get; }

    /// <summary>Units of the mined item (ore or ice), or of the gas mix for a collector, per cycle now.</summary>
    public int UnitsPerCycle { get; }

    public double? CycleS { get; }

    public double UnitsPerHour { get; }

    /// <summary>Cargo slots an hour fills (units per hour over the item's stack size).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? SlotsPerHour { get; }

    /// <summary>Cycles of all the miners on board together until the deposit is depleted; null: never (gas).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? CyclesToDeplete { get; }

    /// <summary>Units this machine would get from the rest of the deposit alone.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? TotalUnitsLeft { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? HeadCyclesLeft { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Problems { get; }
}

/// <summary>A site, what it holds, and what this loadout gets from it.</summary>
internal sealed class MiningSiteView
{
    internal MiningSiteView(SpaceNodeView node, string? parent, string kind, DepositReading deposit, int stackSize,
        List<SiteMaterialView> materials, List<MachineYieldView> machines, List<string> problems)
    {
        Node = node;
        Parent = parent;
        Kind = kind;
        Density = RocketRound.Of(deposit.Density, 3);
        Richness = RocketRound.Of(deposit.Richness, 3);
        Size = RocketRound.Of(deposit.Size, 3);
        SurveyPercent = RocketRound.Of(deposit.SurveyPercent, 1);
        Depleted = deposit.IsDepleted;
        CycleTimeS = RocketRound.Of(DepositReading.TimeToMine(deposit.Density), 2);
        BaseUnitsPerCycle = deposit.OreQuantityNow;
        StackSize = stackSize;
        Materials = materials;
        Machines = machines;
        double perHour = 0.0;
        bool collectable = false;
        for (int index = 0; index < machines.Count; index++)
        {
            if (machines[index].Collected)
            {
                collectable = true;
                perHour += machines[index].UnitsPerHour;
            }
        }

        Collectable = collectable;
        UnitsPerHour = RocketRound.Of(perHour, 0);
        Problems = problems.Count == 0 ? null : problems;
    }

    public SpaceNodeView Node { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Parent { get; }

    /// <summary>ore, ice, gas, or nothing: what a cycle digs or collects here.</summary>
    public string Kind { get; }

    public double Density { get; }

    public double Richness { get; }

    public double Size { get; }

    /// <summary>The node's survey: past 200 % yields x1.1, past 1,000 % x1.25.</summary>
    public double SurveyPercent { get; }

    public bool Depleted { get; }

    /// <summary>MineableDeposit.TimeToMine: the cycle at speed 1 (10 s at density 0 to 6 s at density 10).</summary>
    public double CycleTimeS { get; }

    /// <summary>MineableDeposit.OreQuantity now, before the head's factor.</summary>
    public int BaseUnitsPerCycle { get; }

    /// <summary>Units per item stack (per cargo slot).</summary>
    public int StackSize { get; }

    public List<SiteMaterialView> Materials { get; }

    /// <summary>Every miner and collector on board at this site.</summary>
    public List<MachineYieldView> Machines { get; }

    /// <summary>Whether anything on board brings home what this site gives.</summary>
    public bool Collectable { get; }

    public double UnitsPerHour { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Problems { get; }
}

/// <summary>rocket_mining_options: the loadout and what it can collect at each site.</summary>
internal sealed class RocketMiningOptionsView
{
    internal RocketMiningOptionsView(ThingId rocketId, string rocketName, MiningLoadoutView loadout,
        List<MiningSiteView> sites, List<string> notes)
    {
        RocketId = rocketId;
        RocketName = rocketName;
        Loadout = loadout;
        Sites = sites;
        Count = sites.Count;
        List<string> yes = new List<string>(sites.Count);
        List<string> no = new List<string>(sites.Count);
        for (int index = 0; index < sites.Count; index++)
        {
            string line = $"{sites[index].Node.Name} ({sites[index].Kind})";
            (sites[index].Collectable ? yes : no).Add(line);
        }

        Collectable = yes;
        NotCollectable = no;
        Notes = notes;
    }

    public ThingId RocketId { get; }

    public string RocketName { get; }

    public MiningLoadoutView Loadout { get; }

    /// <summary>Site names this loadout collects at, with the kind.</summary>
    public List<string> Collectable { get; }

    /// <summary>Site names it gets nothing from (see each site's problems).</summary>
    public List<string> NotCollectable { get; }

    public List<MiningSiteView> Sites { get; }

    public int Count { get; }

    public List<string> Notes { get; }
}

/// <summary>rocket_forecast's look at the stop's site: can this loadout collect it, and how much the stop yields.</summary>
internal sealed class ForecastMiningView
{
    internal ForecastMiningView(string site, string kind, bool collectable, double unitsPerHour, double unitsInStop,
        int slotsInStop, List<string> problems)
    {
        Site = site;
        Kind = kind;
        Collectable = collectable;
        UnitsPerHour = RocketRound.Of(unitsPerHour, 0);
        UnitsInStop = RocketRound.Of(unitsInStop, 0);
        SlotsInStop = slotsInStop;
        Problems = problems.Count == 0 ? null : problems;
    }

    public string Site { get; }

    public string Kind { get; }

    public bool Collectable { get; }

    public double UnitsPerHour { get; }

    /// <summary>Units the park time yields at the present rate.</summary>
    public double UnitsInStop { get; }

    /// <summary>Cargo slots those units fill (the forecast adds them as mass unless cargo was given).</summary>
    public int SlotsInStop { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Problems { get; }
}
