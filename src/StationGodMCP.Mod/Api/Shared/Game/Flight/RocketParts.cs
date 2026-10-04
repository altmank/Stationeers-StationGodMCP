#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Networks;
using Objects.Items;
using Objects.Rockets;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Rockets;

namespace StationGodMCP.Api.Shared.Game.Flight;

/// <summary>A tank on a fuel line, with the atmosphere it holds and whether the rocket counts it for mass.</summary>
internal sealed class FuelTankRead
{
    internal FuelTankRead(Thing owner, Atmosphere atmosphere, bool counted)
    {
        Owner = owner;
        Atmosphere = atmosphere;
        Counted = counted;
    }

    internal Thing Owner { get; }

    internal Atmosphere Atmosphere { get; }

    internal bool Counted { get; }
}

/// <summary>A pipe network rocket engines draw from (an engine's _inputNetwork1 or _inputNetwork2), its engines and tanks.</summary>
internal sealed class FuelLineRead
{
    internal FuelLineRead(PipeNetwork network)
    {
        Network = network;
    }

    internal PipeNetwork Network { get; }

    /// <summary>Engines drawing from it (on either input).</summary>
    internal List<RocketEngineBase> Engines { get; } = new List<RocketEngineBase>(2);

    internal List<FuelTankRead> Tanks { get; } = new List<FuelTankRead>(2);

    /// <summary>The pipe and every tank on it, gas and liquid, as one fuel sample.</summary>
    internal FuelSample Sample()
    {
        FuelSample sample = FuelSample.Of(Network.Atmosphere);
        for (int index = 0; index < Tanks.Count; index++)
        {
            sample = sample.Plus(FuelSample.Of(Tanks[index].Atmosphere));
        }

        return sample;
    }
}

/// <summary>An engine, its feed law (EngineSpecs) and the fuel lines its inputs are on.</summary>
internal sealed class EngineRead
{
    internal EngineRead(RocketEngineBase engine, string className, EngineFeed? feed, int? input1, int? input2,
        PipeNetwork? heatExchange)
    {
        Engine = engine;
        ClassName = className;
        Feed = feed;
        Input1 = input1;
        Input2 = input2;
        HeatExchange = heatExchange;
    }

    internal RocketEngineBase Engine { get; }

    /// <summary>The game class the feed law was chosen by (the engine's class, or the nearest known base class).</summary>
    internal string ClassName { get; }

    /// <summary>Null when no class in its chain is one of the six engines.</summary>
    internal EngineFeed? Feed { get; }

    internal int? Input1 { get; }

    /// <summary>Input 2's fuel line, when input 2 carries propellant and is connected.</summary>
    internal int? Input2 { get; }

    /// <summary>The Pressure Fed Liquid Engines' input 2: a heat exchanger, not propellant.</summary>
    internal PipeNetwork? HeatExchange { get; }

    /// <summary>Why the engine cannot burn, from its IsOperable inputs; null when its inputs are there.</summary>
    internal string? MissingInput =>
        Feed == null
            ? null
            : Input1 == null
                ? "input 1 has no pipe network"
                : Feed.NeedsInput2 && Input2 == null
                    ? "input 2 has no pipe network (this engine needs both)"
                    : null;
}

/// <summary>A powered device on the batteries' outputs: what it draws now and while on.</summary>
internal sealed class PowerLoadRead
{
    internal PowerLoadRead(Device device, double nowW, double onW, bool staysOnAtArrival)
    {
        Device = device;
        NowW = nowW;
        OnW = onW;
        StaysOnAtArrival = staysOnAtArrival;
    }

    internal Device Device { get; }

    internal double NowW { get; }

    internal double OnW { get; }

    internal bool StaysOnAtArrival { get; }

    internal bool IsEngine => Device is RocketEngineBase;

    internal bool IsMiningGear => Device is IRocketMiner || Device is RocketChuteStorage;
}

/// <summary>
/// A rocket's parts as the game lists them on its RocketNetwork (Internals and StructureList, RocketNetwork.cs:28-48,
/// 270-336), sorted into what the flight tools need. Read only.
/// </summary>
internal sealed class RocketParts
{
    private RocketParts(Rocket rocket)
    {
        Rocket = rocket;
        Network = rocket.RocketNetwork;
    }

    internal Rocket Rocket { get; }

    internal RocketNetwork Network { get; }

    internal List<RocketEngineBase> Engines { get; } = new List<RocketEngineBase>(2);

    internal List<EngineRead> EngineReads { get; } = new List<EngineRead>(2);

    /// <summary>Engines whose class is none of the six the forecast knows (a mod's engine).</summary>
    internal List<RocketEngineBase> Unmodelled { get; } = new List<RocketEngineBase>();

    internal List<FuelLineRead> Lines { get; } = new List<FuelLineRead>(2);

    /// <summary>Every pipe network the rocket's own pipes make, engine lines and the rest (cargo, sockets), each once.</summary>
    internal List<PipeNetwork> PipeNetworks { get; } = new List<PipeNetwork>(4);

    internal List<Battery> Batteries { get; } = new List<Battery>(2);

    internal List<RocketChuteStorage> Holds { get; } = new List<RocketChuteStorage>(2);

    internal List<RocketMiner> Miners { get; } = new List<RocketMiner>(2);

    internal List<RocketGasCollector> Collectors { get; } = new List<RocketGasCollector>(1);

    internal List<RocketScanner> Scanners { get; } = new List<RocketScanner>(1);

    internal List<RocketPayloadBay> PayloadBays { get; } = new List<RocketPayloadBay>(1);

    internal List<CrewModuleChair> Chairs { get; } = new List<CrewModuleChair>(2);

    internal List<RocketGasUmbilicalFemale> FluidSockets { get; } = new List<RocketGasUmbilicalFemale>(2);

    internal List<RocketPowerUmbilicalFemale> PowerSockets { get; } = new List<RocketPowerUmbilicalFemale>(1);

    /// <summary>Tanks and canister storage on no engine line (spare tanks, cargo gas or liquid).</summary>
    internal List<Thing> OtherTanks { get; } = new List<Thing>(2);

    internal List<PowerLoadRead> Loads { get; } = new List<PowerLoadRead>(16);

    internal RocketAvionicsDevice? Avionics { get; private set; }

    internal List<MassPartView> MassParts { get; } = new List<MassPartView>(16);

    internal static RocketParts Of(Rocket rocket)
    {
        RocketParts parts = new RocketParts(rocket);
        parts.Read();
        return parts;
    }

    /// <summary>The kg the filled cargo slots add (1 each, RocketChuteStorage.cs:30).</summary>
    internal int CargoSlotsFilled
    {
        get
        {
            int total = 0;
            for (int index = 0; index < Holds.Count; index++)
            {
                total += FilledSlots(Holds[index]);
            }

            return total;
        }
    }

    internal int CargoSlotsTotal
    {
        get
        {
            int total = 0;
            for (int index = 0; index < Holds.Count; index++)
            {
                total += Slots(Holds[index]);
            }

            return total;
        }
    }

    /// <summary>The payloads in the bays, kg (RocketPayload.MassContribution: 200, RocketPayload.cs:20).</summary>
    internal double PayloadKg
    {
        get
        {
            double total = 0.0;
            for (int index = 0; index < PayloadBays.Count; index++)
            {
                IRocketPayload? payload = PayloadBays[index].Payload;
                if (payload != null)
                {
                    total += payload.MassContribution;
                }
            }

            return total;
        }
    }

    /// <summary>RocketChuteStorage.CurrentIndex less its INDEX_OFFSET of 2 (RocketChuteStorage.cs:20, 30).</summary>
    internal static int FilledSlots(RocketChuteStorage hold) => Math.Max(0, hold.CurrentIndex - 2);

    internal static int Slots(RocketChuteStorage hold) => Math.Max(0, hold.Slots.Count - 2);

    /// <summary>The engine class whose feed law applies: the engine's own, else the nearest known base class.</summary>
    internal static string EngineClassOf(RocketEngineBase engine)
    {
        for (Type? type = engine.GetType(); type != null && type != typeof(RocketEngineBase); type = type.BaseType)
        {
            if (EngineSpecs.FeedOf(type.Name, 0.0, 0f) != null)
            {
                return type.Name;
            }
        }

        return engine.GetType().Name;
    }

    internal static RocketMiningDrillHead? HeadOf(RocketMiner miner) =>
        GameMembers.RocketMinerHead.GetValue(miner) as RocketMiningDrillHead;

    private void Read()
    {
        Dictionary<string, MassTally> mass = new Dictionary<string, MassTally>(16);
        for (int index = 0; index < Network.Internals.Count; index++)
        {
            IRocketInternals part = Network.Internals[index];
            if (!(part is Thing thing) || thing.IsBeingDestroyed)
            {
                continue;
            }

            Sort(part);
            if (part is IRocketMassContributor contributor)
            {
                Count(mass, thing, contributor.MassContribution);
            }
        }

        for (int index = 0; index < Network.StructureList.Count; index++)
        {
            if (Network.StructureList[index] is IRocketMassContributor contributor &&
                Network.StructureList[index] is Thing piece)
            {
                Count(mass, piece, contributor.MassContribution);
            }
        }

        foreach (KeyValuePair<string, MassTally> entry in mass)
        {
            MassParts.Add(new MassPartView(entry.Key, entry.Value.Count, entry.Value.Kg));
        }

        MassParts.Sort(static (a, b) => b.Kg.CompareTo(a.Kg));
        ReadLines();
        ReadOtherTanks();
        ReadLoads();
    }

    private void Sort(IRocketInternals part)
    {
        switch (part)
        {
            case RocketEngineBase engine:
                Engines.Add(engine);
                if (EngineSpecs.FeedOf(EngineClassOf(engine), 0.0, 0f) == null)
                {
                    Unmodelled.Add(engine);
                }

                break;
            case Battery battery:
                Batteries.Add(battery);
                break;
            case RocketChuteStorage hold:
                Holds.Add(hold);
                break;
            case RocketAvionicsDevice avionics:
                Avionics ??= avionics;
                break;
            case RocketMiner miner:
                Miners.Add(miner);
                break;
            case RocketGasCollector collector:
                Collectors.Add(collector);
                break;
            case RocketScanner scanner:
                Scanners.Add(scanner);
                break;
            case RocketPayloadBay bay:
                PayloadBays.Add(bay);
                break;
            case CrewModuleChair chair:
                Chairs.Add(chair);
                break;
            case RocketGasUmbilicalFemale socket:
                FluidSockets.Add(socket);
                break;
            case RocketPowerUmbilicalFemale power:
                PowerSockets.Add(power);
                break;
            case Pipe { PipeNetwork: { } network }:
                if (network.Atmosphere != null && !PipeNetworks.Contains(network))
                {
                    PipeNetworks.Add(network);
                }

                break;
        }
    }

    private static void Count(Dictionary<string, MassTally> mass, Thing thing, float kg)
    {
        if (kg <= 0f)
        {
            return;
        }

        string name = Names.Of(thing);
        if (!mass.TryGetValue(name, out MassTally tally))
        {
            tally = new MassTally();
            mass[name] = tally;
        }

        tally.Count++;
        tally.Kg += kg;
    }

    // Each engine's input networks (RocketEngineBase._inputNetwork1 / _inputNetwork2) and the tanks on them: a Tank
    // (DeviceInternal) outputs into it, a Gas/Liquid Tank Storage mixes its canisters with its ConnectedPipeNetworks
    // (GasTankStorage.cs:108-143). Input 2 of a Pressure Fed Liquid Engine is a heat exchanger, not a fuel line.
    private void ReadLines()
    {
        for (int index = 0; index < Engines.Count; index++)
        {
            RocketEngineBase engine = Engines[index];
            string className = EngineClassOf(engine);
            EngineFeed? feed = EngineSpecs.FeedOf(className, engine.PressurePerTick.ToDouble(), engine.OutputSetting);
            PipeNetwork? first = NetworkOf(GameMembers.EngineInputNetwork1.GetValue(engine));
            PipeNetwork? second = NetworkOf(GameMembers.EngineInputNetwork2.GetValue(engine));
            bool secondIsFuel = feed == null || feed.Input2IsPropellant;
            int? input1 = first != null ? LineIndex(first, engine) : null;
            int? input2 = second != null && secondIsFuel ? LineIndex(second, engine) : null;
            EngineReads.Add(new EngineRead(engine, className, feed, input1, input2, secondIsFuel ? null : second));
        }
    }

    private static PipeNetwork? NetworkOf(object? value) =>
        value is PipeNetwork network && network.Atmosphere != null ? network : null;

    private int LineIndex(PipeNetwork network, RocketEngineBase engine)
    {
        for (int index = 0; index < Lines.Count; index++)
        {
            if (ReferenceEquals(Lines[index].Network, network))
            {
                if (!Lines[index].Engines.Contains(engine))
                {
                    Lines[index].Engines.Add(engine);
                }

                return index;
            }
        }

        FuelLineRead line = new FuelLineRead(network);
        line.Engines.Add(engine);
        AddTanks(line);
        Lines.Add(line);
        return Lines.Count - 1;
    }

    /// <summary>A pipe network on this rocket with the tanks on it (not necessarily an engine line).</summary>
    internal FuelLineRead ContentsOf(PipeNetwork network)
    {
        FuelLineRead line = new FuelLineRead(network);
        AddTanks(line);
        return line;
    }

    private void AddTanks(FuelLineRead line)
    {
        for (int index = 0; index < Network.Internals.Count; index++)
        {
            IRocketInternals part = Network.Internals[index];
            if (part is Tank tank && tank.InternalAtmosphere != null &&
                ReferenceEquals(tank.ConnectedPipeNetwork, line.Network))
            {
                line.Tanks.Add(new FuelTankRead(tank, tank.InternalAtmosphere,
                    Network.RocketAtmospheres.Contains(tank.InternalAtmosphere)));
            }
            else if (part is GasTankStorage storage && storage.ConnectedPipeNetworks.Contains(line.Network))
            {
                for (int slot = 0; slot < storage.ConnectedGasCanisters.Count; slot++)
                {
                    GasCanister canister = storage.ConnectedGasCanisters[slot];
                    if (canister != null && canister.InternalAtmosphere != null)
                    {
                        line.Tanks.Add(new FuelTankRead(canister, canister.InternalAtmosphere,
                            Network.RocketAtmospheres.Contains(canister.InternalAtmosphere)));
                    }
                }
            }
        }
    }

    private void ReadOtherTanks()
    {
        for (int index = 0; index < Network.Internals.Count; index++)
        {
            IRocketInternals part = Network.Internals[index];
            if ((part is Tank || part is GasTankStorage) && part is Thing thing && !OnALine(thing))
            {
                OtherTanks.Add(thing);
            }
        }
    }

    private bool OnALine(Thing thing)
    {
        for (int line = 0; line < Lines.Count; line++)
        {
            if (thing is GasTankStorage storage && storage.ConnectedPipeNetworks.Contains(Lines[line].Network))
            {
                return true;
            }

            for (int tank = 0; tank < Lines[line].Tanks.Count; tank++)
            {
                if (ReferenceEquals(Lines[line].Tanks[tank].Owner, thing))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Every powered device on a battery's output network: what Device.GetUsedPower gives now, and with it on
    // (UsedPower, times the drill head's PowerConsumptionMultiplier for a miner, RocketMiner.cs:203-211).
    private void ReadLoads()
    {
        HashSet<CableNetwork> outputs = new HashSet<CableNetwork>();
        for (int index = 0; index < Batteries.Count; index++)
        {
            if (Batteries[index].OutputNetwork != null)
            {
                outputs.Add(Batteries[index].OutputNetwork);
            }
        }

        CableNetwork? avionicsData = Avionics != null ? Avionics.DataCableNetwork : null;
        for (int index = 0; index < Network.Internals.Count; index++)
        {
            if (!(Network.Internals[index] is Device device) || device is Battery || device.IsBeingDestroyed)
            {
                continue;
            }

            CableNetwork? power = device.PowerCableNetwork;
            if (power == null || !outputs.Contains(power))
            {
                continue;
            }

            double now = Math.Max(0f, device.GetUsedPower(power));
            double on = device.UsedPower * HeadMultiplier(device);
            bool shutOff = avionicsData != null && device.HasOnOffState && avionicsData.DataDeviceList.Contains(device) &&
                           !(device is RocketAvionicsDevice) && !(device is RocketCircuitHousing);
            Loads.Add(new PowerLoadRead(device, now, on, !shutOff));
        }
    }

    private static float HeadMultiplier(Device device) =>
        device is RocketMiner miner && HeadOf(miner) is RocketMiningDrillHead head
            ? head.PowerConsumptionMultiplier
            : 1f;

    private sealed class MassTally
    {
        internal int Count { get; set; }

        internal double Kg { get; set; }
    }
}
