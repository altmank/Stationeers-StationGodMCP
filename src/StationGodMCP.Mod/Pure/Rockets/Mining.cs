#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Rockets;

/// <summary>
/// A site's deposit as the game holds it (Objects.Rockets.Mining.MineableDeposit): its type flags, the three numbers
/// that drive mining, the survey progress of its node, and what its composition lists.
/// </summary>
internal sealed class DepositReading
{
    internal DepositReading(int typeFlags, float density, float richness, float size, float surveyPercent,
        double oreWeight, double reagentWeight, bool hasFrozenGas, bool hasGas)
    {
        TypeFlags = typeFlags;
        Density = density;
        Richness = richness;
        Size = size;
        SurveyPercent = surveyPercent;
        OreWeight = oreWeight;
        ReagentWeight = reagentWeight;
        HasFrozenGas = hasFrozenGas;
        HasGas = hasGas;
    }

    /// <summary>MineableDepositType flags: Ore 1, ReagentMix 2, Ice 4, Junk 8, Gas 16 (MineableDepositType.cs).</summary>
    internal int TypeFlags { get; }

    internal float Density { get; }

    internal float Richness { get; }

    internal float Size { get; }

    internal float SurveyPercent { get; }

    /// <summary>The weights of the composition's Ores list (mined whatever the head's yields).</summary>
    internal double OreWeight { get; }

    /// <summary>The weights of the ReagentMixes list (space ore; scaled by the head's ReagentYieldMultiplier).</summary>
    internal double ReagentWeight { get; }

    internal bool HasFrozenGas { get; }

    internal bool HasGas { get; }

    internal const int Ore = 1;
    internal const int ReagentMix = 2;
    internal const int Ice = 4;
    internal const int Junk = 8;
    internal const int Gas = 16;

    /// <summary>MineableDeposit.IsDepleted (MineableDeposit.cs:64).</summary>
    internal bool IsDepleted => Density <= 0f;

    /// <summary>RocketMiner.ProgressMineAction skips a deposit whose type is exactly Gas (RocketMiner.cs:321).</summary>
    internal bool IsGasOnly => TypeFlags == Gas;

    /// <summary>
    /// What a miner's cycle digs (MineableDeposit.MineDeposit, MineableDeposit.cs:349-391): a random pick over the Ores
    /// and ReagentMixes weights (SpawnRatioSum counts only those two lists, :276-290), so frozen gas comes out only of a
    /// deposit that lists neither.
    /// </summary>
    internal MinedKind Kind =>
        OreWeight + ReagentWeight > 0.0 ? MinedKind.Ore : HasFrozenGas ? MinedKind.Ice : MinedKind.Nothing;

    /// <summary>MineableDeposit.DepositTypeMultiplier (MineableDeposit.cs:170-182): on the exact type value only.</summary>
    internal float TypeMultiplier => TypeFlags switch
    {
        ReagentMix => 1.25f,
        Ice => 4f,
        _ => 1f
    };

    /// <summary>MineableDeposit.TimeToMine (:109-112): 10 s at density 0 to 6 s at density 10.</summary>
    internal static float TimeToMine(float density) => MapToScale(0f, 10f, 10f, 6f, density);

    /// <summary>MineableDeposit.MineBaseLine (:119-122): 2 at size 1 to 6 at size 10.</summary>
    internal static float BaseLine(float size) => MapToScale(1f, 10f, 2f, 6f, size);

    /// <summary>
    /// MineableDeposit.OreQuantity (:129-146): baseline x richness^1.6 x the type multiplier, x1.1 once the node's
    /// survey passes 200 %, x1.25 past 1,000 %, rounded, at least 1 (0 baseline when depleted, still at least 1).
    /// </summary>
    internal static int OreQuantity(int typeFlags, float density, float richness, float size, float surveyPercent)
    {
        float units = density <= 0f ? 0f : BaseLine(size) * (float)Math.Pow(richness, 1.6f);
        units *= TypeMultiplierOf(typeFlags);
        if (surveyPercent >= 1000f)
        {
            units *= 1.25f;
        }
        else if (surveyPercent >= 200f)
        {
            units *= 1.1f;
        }

        return Math.Max(RoundToInt(units), 1);
    }

    internal int OreQuantityNow => OreQuantity(TypeFlags, Density, Richness, Size, SurveyPercent);

    /// <summary>MineableDeposit.RichnessReduction (:94-97).</summary>
    internal static float RichnessReduction(float richness, float size) =>
        (float)Math.Pow(richness / 10f + 0.75f, 5f) * 0.01f / SizeModifier(size);

    /// <summary>MineableDeposit.DensityReduction (:99-102).</summary>
    internal static float DensityReduction(float size) => 0.001f / SizeModifier(size);

    /// <summary>Mathf.RoundToInt: Math.Round, halves to even.</summary>
    internal static int RoundToInt(float value) => (int)Math.Round(value, MidpointRounding.ToEven);

    private static float TypeMultiplierOf(int flags) => flags switch
    {
        ReagentMix => 1.25f,
        Ice => 4f,
        _ => 1f
    };

    private static float SizeModifier(float size) => MapToScale(1f, 10f, 0.2f, 1f, size);

    private static float MapToScale(float min, float max, float outMin, float outMax, float value) =>
        (value - min) * (outMax - outMin) / (max - min) + outMin;
}

internal enum MinedKind
{
    Nothing,
    Ore,
    Ice
}

/// <summary>A Mining-Drill Head's serialized multipliers (RocketMiningDrillHead.cs:15-28) and its durability left.</summary>
internal sealed class DrillHeadReading
{
    internal DrillHeadReading(string name, float speed, float oreYield, float iceYield, float health, float power,
        float quantity, float maxQuantity)
    {
        Name = name;
        Speed = speed;
        OreYield = oreYield;
        IceYield = iceYield;
        Health = health;
        Power = power;
        Quantity = quantity;
        MaxQuantity = maxQuantity;
    }

    internal string Name { get; }

    internal float Speed { get; }

    /// <summary>ReagentYieldMultiplier.</summary>
    internal float OreYield { get; }

    internal float IceYield { get; }

    internal float Health { get; }

    /// <summary>PowerConsumptionMultiplier.</summary>
    internal float Power { get; }

    internal float Quantity { get; }

    internal float MaxQuantity { get; }

    /// <summary>
    /// Expected cycles until worn out: each cycle takes 1 point with chance 0.1 / HealthMultiplier
    /// (RocketMiningDrillHead.OnResourceCollected, RocketMiningDrillHead.cs:41-47).
    /// </summary>
    internal double CyclesLeft => Health > 0f ? Quantity * 10.0 * Health : 0.0;
}

/// <summary>A Rocket Miner on board: its own speed multipliers (prefab values) and its head.</summary>
internal sealed class MinerReading
{
    internal MinerReading(string name, float oreSpeed, float iceSpeed, float junkSpeed, DrillHeadReading? head,
        bool on, double powerW, bool exportsToHold)
    {
        Name = name;
        OreSpeed = oreSpeed;
        IceSpeed = iceSpeed;
        JunkSpeed = junkSpeed;
        Head = head;
        On = on;
        PowerW = powerW;
        ExportsToHold = exportsToHold;
    }

    internal string Name { get; }

    internal float OreSpeed { get; }

    internal float IceSpeed { get; }

    internal float JunkSpeed { get; }

    internal DrillHeadReading? Head { get; }

    internal bool On { get; }

    /// <summary>UsedPower x the head's power factor (RocketMiner.GetUsedPower, RocketMiner.cs:203-211).</summary>
    internal double PowerW { get; }

    /// <summary>Whether its chute export reaches a cargo hold (else it stops once its export slot is full).</summary>
    internal bool ExportsToHold { get; }

    /// <summary>The same miner switched on, as a player switches it on to mine at a site.</summary>
    internal MinerReading SwitchedOn() =>
        new MinerReading(Name, OreSpeed, IceSpeed, JunkSpeed, Head, true, PowerW, ExportsToHold);

    /// <summary>RocketMiner.MinerSpeedMultiplier (RocketMiner.cs:122-142).</summary>
    internal float SpeedOn(int typeFlags)
    {
        float speed = 1f;
        if ((typeFlags & (DepositReading.Ore | DepositReading.ReagentMix)) != 0)
        {
            speed *= OreSpeed;
        }

        if ((typeFlags & DepositReading.Ice) != 0)
        {
            speed *= IceSpeed;
        }

        if ((typeFlags & DepositReading.Junk) != 0)
        {
            speed *= JunkSpeed;
        }

        return Head != null ? speed * Head.Speed : speed;
    }
}

/// <summary>A Rocket Gas Collector on board.</summary>
internal sealed class CollectorReading
{
    internal CollectorReading(string name, bool on, bool hasPipeNetwork)
    {
        Name = name;
        On = on;
        HasPipeNetwork = hasPipeNetwork;
    }

    internal string Name { get; }

    internal bool On { get; }

    internal bool HasPipeNetwork { get; }

    /// <summary>The same collector switched on, as a player switches it on to collect at a site.</summary>
    internal CollectorReading SwitchedOn() => new CollectorReading(Name, true, HasPipeNetwork);
}

/// <summary>What one machine on board gets from one site, per cycle and per hour, and why not when it gets nothing.</summary>
internal sealed class MachineYield
{
    internal MachineYield(string machine, string collects, int unitsPerCycle, double cycleSeconds,
        double unitsPerHour, double? cyclesToDeplete, double? totalUnitsLeft, double? headCyclesLeft,
        List<string> problems)
    {
        Machine = machine;
        Collects = collects;
        UnitsPerCycle = unitsPerCycle;
        CycleSeconds = cycleSeconds;
        UnitsPerHour = unitsPerHour;
        CyclesToDeplete = cyclesToDeplete;
        TotalUnitsLeft = totalUnitsLeft;
        HeadCyclesLeft = headCyclesLeft;
        Problems = problems;
    }

    internal string Machine { get; }

    /// <summary>ore, ice, gas or nothing.</summary>
    internal string Collects { get; }

    internal int UnitsPerCycle { get; }

    internal double CycleSeconds { get; }

    internal double UnitsPerHour { get; }

    /// <summary>Cycles (of all machines together) until the deposit is depleted; null for a gas site (never).</summary>
    internal double? CyclesToDeplete { get; }

    /// <summary>Units this machine would get from the rest of the deposit if it mined it all alone.</summary>
    internal double? TotalUnitsLeft { get; }

    internal double? HeadCyclesLeft { get; }

    internal List<string> Problems { get; }

    internal bool Collected => UnitsPerCycle > 0 && Problems.Count == 0;
}

/// <summary>
/// The game's mining formulas applied to a loadout and a site (CODE: MineableDeposit.cs, RocketMiner.cs,
/// RocketGasCollector.cs, RocketMiningDrillHead.cs, RocketMine.cs). Mine mode runs every miner and every gas collector
/// on board at once (RocketMine.ProgressAction, RocketMine.cs:38-61); each miner's cycle calls MineDeposit, which
/// shrinks the deposit before the yield is worked out (MineableDeposit.cs:349-353).
/// </summary>
internal static class MiningYields
{
    /// <summary>
    /// A miner on a site. The action advances once per 0.5 s game tick by tick x speed / TimeToMine and the miner digs
    /// once it reaches 1 (RocketMiner.cs:243-258, 318-336), so a cycle is a whole number of ticks.
    /// </summary>
    internal static MachineYield Miner(MinerReading miner, DepositReading deposit, double tickSeconds,
        int minersOnBoard)
    {
        List<string> problems = new List<string>(3);
        DrillHeadReading? head = miner.Head;
        if (head == null)
        {
            problems.Add("No drill head: the miner refuses to mine (RocketMiner.CanProgressAction, RocketMiner.cs:294-297).");
        }
        else if (head.Quantity <= 0f)
        {
            problems.Add($"{head.Name} is worn out (durability 0): the miner refuses to mine (RocketMiner.cs:298-301).");
        }

        if (!miner.On)
        {
            problems.Add("The miner is off: it does not mine (RocketMiner.cs:302-305).");
        }

        if (!miner.ExportsToHold)
        {
            problems.Add("Its chute export reaches no cargo hold: it stops once its export slot holds a full stack " +
                         "(RocketMiner.cs:306-309).");
        }

        if (deposit.IsDepleted)
        {
            problems.Add("The deposit is depleted (density 0): Mine fails (RocketMine.cs:45-48).");
            return new MachineYield(miner.Name, "nothing", 0, 0.0, 0.0, 0.0, 0.0, head?.CyclesLeft, problems);
        }

        if (deposit.IsGasOnly)
        {
            problems.Add("A gas site: miners skip gas deposits (RocketMiner.cs:321); a Rocket Gas Collector collects it.");
            return new MachineYield(miner.Name, "nothing", 0, 0.0, 0.0, null, null, head?.CyclesLeft, problems);
        }

        float speed = miner.SpeedOn(deposit.TypeFlags);
        double cycle = CycleSeconds(DepositReading.TimeToMine(deposit.Density), speed, tickSeconds);
        MinedKind kind = deposit.Kind;
        // MineDeposit shrinks the deposit first, then works out the yield (MineableDeposit.cs:353-360).
        float richness = Math.Max(1f, deposit.Richness - DepositReading.RichnessReduction(deposit.Richness, deposit.Size));
        float density = Math.Max(0f, deposit.Density - DepositReading.DensityReduction(deposit.Size));
        int units = head == null ? 0 : UnitsOf(deposit, kind, head, density, richness);
        string collects = kind == MinedKind.Ore ? "ore" : kind == MinedKind.Ice ? "ice" : "nothing";
        if (head != null && units == 0 && kind != MinedKind.Nothing)
        {
            string headKind = head.IceYield > 0f ? "an ice head" : head.OreYield > 0f ? "an ore head" : "this head";
            problems.Add($"{head.Name} is {headKind} at an {collects} site: its {collects} yield factor is 0, so every " +
                         $"cycle gives 0 {collects}; each cycle still depletes the site and wears the head " +
                         "(MineableDeposit.cs:349-391, RocketMiningDrillHead.cs:41-47).");
        }

        (double depleteCycles, double total) = head == null
            ? (CyclesToDeplete(deposit), 0.0)
            : Remaining(deposit, kind, head);
        double perHour = cycle > 0.0 ? units * 3600.0 / cycle : 0.0;
        return new MachineYield(miner.Name, collects, units, cycle, perHour, depleteCycles / Math.Max(1, minersOnBoard),
            total, head?.CyclesLeft, problems);
    }

    /// <summary>
    /// A gas collector on a site: a cycle of TimeToMine (no speed factor), OreQuantity units of the site's gas mix each
    /// (RocketGasCollector.cs:133-150, 172-182; MineableDeposit.CollectGas, :425-434); gas sites never deplete.
    /// </summary>
    internal static MachineYield Collector(CollectorReading collector, DepositReading deposit, double tickSeconds)
    {
        List<string> problems = new List<string>(2);
        if (!collector.HasPipeNetwork)
        {
            problems.Add("No pipe network on its output: the collector refuses (RocketGasCollector.cs:159-162).");
        }

        if (!collector.On)
        {
            problems.Add("The collector is off: it collects nothing (RocketGasCollector.cs:144).");
        }

        if (!deposit.HasGas || (deposit.TypeFlags & DepositReading.Gas) == 0)
        {
            problems.Add("Not a gas site: CollectGas gives nothing here (MineableDeposit.cs:425-430).");
            return new MachineYield(collector.Name, "nothing", 0, 0.0, 0.0, null, null, null, problems);
        }

        double cycle = CycleSeconds(DepositReading.TimeToMine(deposit.Density), 1f, tickSeconds);
        int units = deposit.OreQuantityNow;
        return new MachineYield(collector.Name, "gas", units, cycle, cycle > 0.0 ? units * 3600.0 / cycle : 0.0, null,
            null, null, problems);
    }

    /// <summary>Ticks of tick x speed / time until progress reaches 1, as seconds.</summary>
    internal static double CycleSeconds(float timeToMine, float speed, double tickSeconds)
    {
        if (!(speed > 0f) || !(tickSeconds > 0.0))
        {
            return double.PositiveInfinity;
        }

        double ticks = Math.Ceiling(timeToMine / (tickSeconds * speed) - 1e-9);
        return Math.Max(1.0, ticks) * tickSeconds;
    }

    /// <summary>The units one cycle gives this head at this state (MineableDeposit.MineDeposit, :349-391).</summary>
    internal static int UnitsOf(DepositReading deposit, MinedKind kind, DrillHeadReading head, float density,
        float richness)
    {
        int quantity = DepositReading.OreQuantity(deposit.TypeFlags, density, richness, deposit.Size,
            deposit.SurveyPercent);
        return kind switch
        {
            // The Ores list is mined at full quantity whatever the head; the ReagentMixes list takes the ore factor.
            MinedKind.Ore => deposit.ReagentWeight <= 0.0
                ? quantity
                : deposit.OreWeight <= 0.0
                    ? DepositReading.RoundToInt(quantity * head.OreYield)
                    : (int)Math.Round((deposit.OreWeight * quantity +
                                       deposit.ReagentWeight * DepositReading.RoundToInt(quantity * head.OreYield)) /
                                      (deposit.OreWeight + deposit.ReagentWeight)),
            MinedKind.Ice => DepositReading.RoundToInt(quantity * head.IceYield),
            _ => 0
        };
    }

    /// <summary>Cycles until density reaches 0 (OnDepositMined, MineableDeposit.cs:184-190).</summary>
    internal static double CyclesToDeplete(DepositReading deposit)
    {
        float step = DepositReading.DensityReduction(deposit.Size);
        return step > 0f ? Math.Ceiling(deposit.Density / step) : double.PositiveInfinity;
    }

    // The deposit mined to the end by this head alone, as CalculateTotalOreAtLocation walks it (:192-213), with the
    // head's factor and the rounding of each cycle.
    private static (double Cycles, double Units) Remaining(DepositReading deposit, MinedKind kind,
        DrillHeadReading head)
    {
        float density = deposit.Density;
        float richness = deposit.Richness;
        double units = 0.0;
        int cycles = 0;
        for (; cycles < 10000 && density > 0f; cycles++)
        {
            richness = Math.Max(1f, richness - DepositReading.RichnessReduction(richness, deposit.Size));
            density = Math.Max(0f, density - DepositReading.DensityReduction(deposit.Size));
            units += UnitsOf(deposit, kind, head, density > 0f ? density : 0f, richness);
        }

        return (cycles, units);
    }
}
