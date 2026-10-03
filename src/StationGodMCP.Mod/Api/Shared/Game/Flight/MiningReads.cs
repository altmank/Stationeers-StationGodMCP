#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Objects.Items;
using Objects.Rockets;
using Objects.Rockets.Mining;
using Objects.Rockets.Scanning;
using Reagents;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Rockets;

namespace StationGodMCP.Api.Shared.Game.Flight;

/// <summary>A site with a deposit: its node, the deposit read for the formulas, and what it gives per unit.</summary>
internal sealed class SiteRead
{
    internal SiteRead(SpaceMapNode node, MineableDeposit deposit, DepositReading reading, string kind,
        List<SiteMaterialView> materials, int stackSize)
    {
        Node = node;
        Deposit = deposit;
        Reading = reading;
        Kind = kind;
        Materials = materials;
        StackSize = stackSize;
    }

    internal SpaceMapNode Node { get; }

    internal MineableDeposit Deposit { get; }

    internal DepositReading Reading { get; }

    /// <summary>ore, ice, gas, or nothing (a deposit that lists none of them).</summary>
    internal string Kind { get; }

    internal List<SiteMaterialView> Materials { get; }

    /// <summary>The mined item's stack size (IQuantity.GetMaxQuantity): units per cargo slot.</summary>
    internal int StackSize { get; }
}

/// <summary>Reads deposits, miners, collectors and scanners for the mining formulas (Pure/Rockets/Mining). Read only.</summary>
internal static class MiningReads
{
    /// <summary>The node's deposit, or null when it has none.</summary>
    internal static SiteRead? SiteOf(SpaceMapNode node)
    {
        MineableDeposit? deposit = node.Deposit;
        if (deposit == null || deposit.DepositComposition == null)
        {
            return null;
        }

        DepositComposition composition = deposit.DepositComposition;
        double ores = 0.0;
        double reagents = 0.0;
        List<SiteMaterialView> materials = new List<SiteMaterialView>(4);
        Thing? mined = null;
        for (int index = 0; index < composition.Ores.Count; index++)
        {
            DepositMaterialOre ore = composition.Ores[index];
            ores += ore.Weight;
            mined ??= ore.OrePrefab;
            materials.Add(new SiteMaterialView("ore", PrefabName(ore.OrePrefab), ore.Weight, null));
        }

        for (int index = 0; index < composition.ReagentMixes.Count; index++)
        {
            DepositMaterialReagentMix mix = composition.ReagentMixes[index];
            reagents += mix.Weight;
            mined ??= mix.MixPrefab;
            materials.Add(new SiteMaterialView("ore", PrefabName(mix.MixPrefab), mix.Weight, ReagentsOf(mix.Mixture)));
        }

        for (int index = 0; index < composition.FrozenGasses.Count; index++)
        {
            DepositMaterialGas ice = composition.FrozenGasses[index];
            if (ores + reagents <= 0.0)
            {
                mined ??= ice.MixPrefab;
            }

            materials.Add(new SiteMaterialView("ice", PrefabName(ice.MixPrefab), ice.Weight, GasesOf(ice)));
        }

        for (int index = 0; index < composition.Gasses.Count; index++)
        {
            DepositMaterialGas gas = composition.Gasses[index];
            materials.Add(new SiteMaterialView("gas", "gas", gas.Weight, GasesOf(gas)));
        }

        DepositReading reading = new DepositReading((int)deposit.DepositType, deposit.Density, deposit.Richness,
            deposit.Size, node.SurveyPercent, ores, reagents, composition.FrozenGasses.Count > 0,
            composition.Gasses.Count > 0);
        string kind = reading.IsGasOnly || (reading.Kind == MinedKind.Nothing && reading.HasGas)
            ? "gas"
            : reading.Kind == MinedKind.Ore ? "ore" : reading.Kind == MinedKind.Ice ? "ice" : "nothing";
        int stack = mined is IQuantity quantity ? (int)System.Math.Max(1f, quantity.GetMaxQuantity) : 1;
        return new SiteRead(node, deposit, reading, kind, materials, stack);
    }

    /// <summary>The node and its discovered child sites, each with a deposit.</summary>
    internal static List<SiteRead> SitesAt(SpaceMapNode node)
    {
        List<SiteRead> sites = new List<SiteRead>(4);
        SiteRead? own = SiteOf(node);
        if (own != null)
        {
            sites.Add(own);
        }

        for (int index = 0; index < node.ChildConnections.Count; index++)
        {
            SpaceMapNode? child = node.ChildConnections[index]?.Child;
            SiteRead? site = child != null && child.Owner == null ? SiteOf(child) : null;
            if (site != null)
            {
                sites.Add(site);
            }
        }

        return sites;
    }

    /// <summary>Every charted node with a deposit, in map order.</summary>
    internal static List<SiteRead> AllSites()
    {
        List<SiteRead> sites = new List<SiteRead>(32);
        for (int index = 0; index < SpaceMapNode.AllSpaceMapNodes.Count; index++)
        {
            SpaceMapNode node = SpaceMapNode.AllSpaceMapNodes[index];
            SiteRead? site = node != null && node.IsCharted ? SiteOf(node) : null;
            if (site != null)
            {
                sites.Add(site);
            }
        }

        return sites;
    }

    internal static DrillHeadReading? HeadReading(RocketMiningDrillHead? head) =>
        head == null
            ? null
            : new DrillHeadReading(Names.Of(head), head.SpeedMultiplier, head.ReagentYieldMultiplier,
                head.IceYieldMultiplier, head.HealthMultiplier, head.PowerConsumptionMultiplier, head.Quantity,
                head.MaxQuantity);

    /// <summary>A miner's own multipliers (prefab fields), its head, and whether a cargo hold is on board for its export.</summary>
    internal static MinerReading MinerOf(RocketMiner miner, RocketParts parts)
    {
        RocketMiningDrillHead? head = RocketParts.HeadOf(miner);
        return new MinerReading(Names.Of(miner), Multiplier(GameMembers.RocketMinerOreSpeed.GetValue(miner)),
            Multiplier(GameMembers.RocketMinerIceSpeed.GetValue(miner)),
            Multiplier(GameMembers.RocketMinerJunkSpeed.GetValue(miner)), HeadReading(head),
            miner.OnOff, miner.UsedPower * (head != null ? head.PowerConsumptionMultiplier : 1f),
            ExportsToHold(miner, parts));
    }

    internal static CollectorReading CollectorOf(RocketGasCollector collector) =>
        new CollectorReading(Names.Of(collector), collector.OnOff, collector.ConnectedPipeNetworks.Count > 0);

    /// <summary>The scanner's head: normal (chart, discover, survey), surface (Surface Scan only), or none (RocketScanner.cs:200-226).</summary>
    internal static (string Kind, string? Name) ScannerHeadOf(RocketScanner scanner)
    {
        object? head = GameMembers.RocketScannerHead.GetValue(scanner);
        return head switch
        {
            RocketDeepScanningHead deep => ("surface", Names.Of(deep)),
            RocketScanningHead normal => ("normal", Names.Of(normal)),
            _ => ("none", null)
        };
    }

    // Whether a cargo hold is on board for the miner's chute export (the chute route itself is not traced).
    private static bool ExportsToHold(RocketMiner miner, RocketParts parts) => parts.Holds.Count > 0;

    private static float Multiplier(object? value) => value is float number ? number : 1f;

    private static string PrefabName(Thing? prefab) => prefab != null ? prefab.PrefabName : "unknown";

    private static Dictionary<string, double>? ReagentsOf(ReagentMixture? mixture)
    {
        if (mixture == null)
        {
            return null;
        }

        Dictionary<string, double> reagents = new Dictionary<string, double>(4);
        foreach (Reagent reagent in Reagent.AllReagents)
        {
            double quantity = mixture.Get(reagent);
            if (quantity > 1e-6)
            {
                reagents[reagent.TypeName] = RocketRound.Of(quantity, 3);
            }
        }

        return reagents;
    }

    private static Dictionary<string, double> GasesOf(DepositMaterialGas material)
    {
        Dictionary<string, double> gases = new Dictionary<string, double>(4);
        for (int index = 0; index < material.SpawnGasses.Count; index++)
        {
            SpawnGas gas = material.SpawnGasses[index];
            string name = gas.Type.ToString();
            gases.TryGetValue(name, out double before);
            gases[name] = RocketRound.Of(before + gas.GetQuantity().ToDouble(), 3);
        }

        return gases;
    }
}
