#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Pipes;
using Objects.Rockets;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Rockets;

namespace StationGodMCP.Api;

/// <summary>
/// rocket_mining_options: a rocket's mining, collecting and scanning gear, and for each site (the destination and its
/// discovered sites, or every charted node with a deposit) whether that gear brings anything home, how much per cycle
/// and per hour by the game's own formulas (Pure/Rockets/Mining), and why not. Read only.
/// </summary>
internal static class RocketMiningOptionsApi
{
    internal static RocketMiningOptionsView Handle(Args args)
    {
        Rocket rocket = RocketLocator.One(args);
        RocketParts parts = RocketParts.Of(rocket);
        string? to = args.OptionalString("to");
        List<SiteRead> sites = to != null ? MiningReads.SitesAt(SpaceRoutes.Resolve(to)) : MiningReads.AllSites();
        bool collectableOnly = args.OptionalBool("collectable_only") ?? false;
        List<MiningSiteView> views = new List<MiningSiteView>(sites.Count);
        for (int index = 0; index < sites.Count; index++)
        {
            MiningSiteView view = MiningPlans.SiteView(sites[index], parts);
            if (!collectableOnly || view.Collectable)
            {
                views.Add(view);
            }
        }

        List<string> notes = new List<string>(4)
        {
            "Mine mode runs every miner and gas collector on board at once (RocketMine.cs:38-61). A miner's cycle " +
            "shrinks the deposit, then gives OreQuantity = round(baseline(size 1..10 -> 2..6) x richness^1.6 x type " +
            "factor (ice 4) x survey bonus) units, at least 1, times the head's ore or ice factor " +
            "(MineableDeposit.cs:109-146, 349-391). A cycle takes TimeToMine / speed, in whole 0.5 s ticks.",
            "The rocket must be at the site's own node to mine it; ore and ice sites shrink every cycle, gas sites never " +
            "(MineableDeposit.cs:184-190, 425-434)."
        };
        if (to != null && sites.Count == 0)
        {
            notes.Add($"{to}: no deposit at that node or its discovered sites.");
        }

        return new RocketMiningOptionsView(new ThingId(rocket.ReferenceId), rocket.DisplayName,
            MiningPlans.Loadout(parts), views, notes);
    }
}

/// <summary>The mining formulas applied to a rocket's parts at a site, shared by rocket_mining_options and rocket_forecast.</summary>
internal static class MiningPlans
{
    internal static MiningLoadoutView Loadout(RocketParts parts)
    {
        List<MinerView> miners = new List<MinerView>(parts.Miners.Count);
        bool ore = false;
        bool ice = false;
        for (int index = 0; index < parts.Miners.Count; index++)
        {
            MinerReading reading = MiningReads.MinerOf(parts.Miners[index], parts);
            miners.Add(new MinerView(new ThingId(parts.Miners[index].ReferenceId), reading));
            if (reading.Head != null && reading.Head.Quantity > 0f && reading.ExportsToHold)
            {
                ore |= reading.Head.OreYield > 0f;
                ice |= reading.Head.IceYield > 0f;
            }
        }

        List<CollectorView> collectors = new List<CollectorView>(parts.Collectors.Count);
        bool gas = false;
        for (int index = 0; index < parts.Collectors.Count; index++)
        {
            CollectorReading reading = MiningReads.CollectorOf(parts.Collectors[index]);
            collectors.Add(new CollectorView(new ThingId(parts.Collectors[index].ReferenceId), reading));
            gas |= reading.HasPipeNetwork;
        }

        List<ScannerView> scanners = new List<ScannerView>(parts.Scanners.Count);
        bool chart = false;
        bool surface = false;
        for (int index = 0; index < parts.Scanners.Count; index++)
        {
            RocketScanner scanner = parts.Scanners[index];
            (string kind, string? head) = MiningReads.ScannerHeadOf(scanner);
            scanners.Add(new ScannerView(new ThingId(scanner.ReferenceId), Names.Of(scanner), scanner.OnOff, kind, head,
                scanner.Points, scanner.CycleTime));
            chart |= kind == "normal";
            surface |= kind == "surface";
        }

        List<string> collects = new List<string>(5);
        if (ore)
        {
            collects.Add("ore");
        }

        if (ice)
        {
            collects.Add("ice");
        }

        if (gas)
        {
            collects.Add("gas");
        }

        if (chart)
        {
            collects.Add("chart/discover/survey");
        }

        if (surface)
        {
            collects.Add("surface_scan");
        }

        (double gasLitres, double liquidLitres) = TankLitres(parts);
        return new MiningLoadoutView(miners, collectors, scanners,
            new CargoSummaryView(parts.Holds.Count, parts.CargoSlotsTotal, parts.CargoSlotsFilled), gasLitres,
            liquidLitres, collects);
    }

    internal static MiningSiteView SiteView(SiteRead site, RocketParts parts)
    {
        List<MachineYieldView> machines = Machines(site, parts, out List<string> problems);
        SpaceMapNode? parent = site.Node.ParentConnection?.Parent;
        return new MiningSiteView(SpaceRoutes.ViewOf(site.Node), parent != null ? SpaceRoutes.NameOf(parent) : null,
            site.Kind, site.Reading, site.StackSize, site.Materials, machines, problems);
    }

    /// <summary>Every miner and collector at the site, and the site-wide problems (nothing on board collects it).</summary>
    internal static List<MachineYieldView> Machines(SiteRead site, RocketParts parts, out List<string> problems)
    {
        double tick = Assets.Scripts.GameManager.GameTickSpeedSeconds;
        problems = new List<string>(3);
        List<MachineYieldView> machines = new List<MachineYieldView>(parts.Miners.Count + parts.Collectors.Count);
        for (int index = 0; index < parts.Miners.Count; index++)
        {
            MachineYield yield = MiningYields.Miner(MiningReads.MinerOf(parts.Miners[index], parts), site.Reading,
                tick, parts.Miners.Count);
            machines.Add(new MachineYieldView(yield, site.StackSize));
        }

        for (int index = 0; index < parts.Collectors.Count; index++)
        {
            MachineYield yield = MiningYields.Collector(MiningReads.CollectorOf(parts.Collectors[index]), site.Reading,
                tick);
            machines.Add(new MachineYieldView(yield, site.StackSize));
        }

        if (!site.Node.HasAction(RocketMode.Mine))
        {
            problems.Add("The node offers no Mine action.");
        }

        if (parts.Miners.Count == 0 && parts.Collectors.Count == 0)
        {
            problems.Add("Nothing on board mines or collects: Mine fails with no miner (RocketMine.cs:40-44).");
        }
        else if (site.Kind == "gas" && parts.Collectors.Count == 0)
        {
            problems.Add("A gas site needs a Rocket Gas Collector (with a pump into a gas tank); miners skip gas " +
                         "(RocketMiner.cs:321).");
        }
        else if ((site.Kind == "ore" || site.Kind == "ice") && parts.Miners.Count == 0)
        {
            problems.Add($"An {site.Kind} site needs a Rocket Miner with a head that mines {site.Kind} and a cargo hold; " +
                         "a gas collector gets nothing here.");
        }

        if (site.Kind == "gas" && parts.Collectors.Count > 0)
        {
            problems.Add("Collected gas goes into the collector's 200 L and only equalises with its pipe: a pump into a " +
                         "tank is needed to store much (RocketGasCollector.cs:19, DeviceMixAtmosphere).");
        }

        return machines;
    }

    /// <summary>Litres of gas and of liquid tanks on board (Tank devices by their connection type).</summary>
    private static (double Gas, double Liquid) TankLitres(RocketParts parts)
    {
        double gas = 0.0;
        double liquid = 0.0;
        for (int index = 0; index < parts.Network.Internals.Count; index++)
        {
            if (parts.Network.Internals[index] is Tank tank && tank.InternalAtmosphere != null)
            {
                double litres = tank.InternalAtmosphere.Volume.ToDouble();
                if (tank.ContentType == Pipe.ContentType.Liquid)
                {
                    liquid += litres;
                }
                else
                {
                    gas += litres;
                }
            }
        }

        return (gas, liquid);
    }
}
