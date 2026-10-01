#nullable enable

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

namespace StationGodMCP.Api.Shared.Game.Flight;

/// <summary>A tank on an engine's fuel line, with the atmosphere it holds and whether the rocket counts it for mass.</summary>
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

/// <summary>A pipe network rocket engines draw from (RocketEngineBase._inputNetwork1), its engines and tanks.</summary>
internal sealed class FuelLineRead
{
    internal FuelLineRead(PipeNetwork network)
    {
        Network = network;
    }

    internal PipeNetwork Network { get; }

    internal List<RocketEngineBase> Engines { get; } = new List<RocketEngineBase>(2);

    internal List<FuelTankRead> Tanks { get; } = new List<FuelTankRead>(2);
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

    /// <summary>Engines whose class rocket_forecast does not fly.</summary>
    internal List<RocketEngineBase> Unmodelled { get; } = new List<RocketEngineBase>();

    internal List<FuelLineRead> Lines { get; } = new List<FuelLineRead>(1);

    internal List<Battery> Batteries { get; } = new List<Battery>(2);

    internal List<RocketChuteStorage> Holds { get; } = new List<RocketChuteStorage>(2);

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

    /// <summary>RocketChuteStorage.CurrentIndex less its INDEX_OFFSET of 2 (RocketChuteStorage.cs:20, 30).</summary>
    internal static int FilledSlots(RocketChuteStorage hold) => System.Math.Max(0, hold.CurrentIndex - 2);

    internal static int Slots(RocketChuteStorage hold) => System.Math.Max(0, hold.Slots.Count - 2);

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

            switch (part)
            {
                case RocketEngineBase engine:
                    Engines.Add(engine);
                    if (!(engine is GovernedGasEngine))
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
            }

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
        ReadLoads();
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

    // Each engine's input network, the tanks feeding it: Tank (DeviceInternal) outputs into it, GasTankStorage holds
    // canisters mixed with its ConnectedPipeNetworks (GasTankStorage.cs:108-143).
    private void ReadLines()
    {
        for (int index = 0; index < Engines.Count; index++)
        {
            RocketEngineBase engine = Engines[index];
            if (!(GameMembers.EngineInputNetwork1.GetValue(engine) is PipeNetwork input) || input.Atmosphere == null)
            {
                continue;
            }

            FuelLineRead? line = null;
            for (int existing = 0; existing < Lines.Count; existing++)
            {
                if (ReferenceEquals(Lines[existing].Network, input))
                {
                    line = Lines[existing];
                }
            }

            if (line == null)
            {
                line = new FuelLineRead(input);
                Lines.Add(line);
                AddTanks(line);
            }

            line.Engines.Add(engine);
        }
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

            double now = System.Math.Max(0f, device.GetUsedPower(power));
            double on = device.UsedPower * HeadMultiplier(device);
            bool shutOff = avionicsData != null && device.HasOnOffState && avionicsData.DataDeviceList.Contains(device) &&
                           !(device is RocketAvionicsDevice) && !(device is RocketCircuitHousing);
            Loads.Add(new PowerLoadRead(device, now, on, !shutOff));
        }
    }

    private static float HeadMultiplier(Device device) =>
        device is RocketMiner miner && GameMembers.RocketMinerHead.GetValue(miner) is RocketMiningDrillHead head
            ? head.PowerConsumptionMultiplier
            : 1f;

    private sealed class MassTally
    {
        internal int Count { get; set; }

        internal double Kg { get; set; }
    }
}
