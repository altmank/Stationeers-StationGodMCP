#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Networks;
using Objects.Electrical;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api;

/// <summary>
/// Which atmospheres the game joins: pairs it mixes to one composition and temperature every atmospherics tick with
/// AtmosphereHelper.Mix, which pools two mixtures and hands each its volume's share, or with
/// GasTankStorage's own pooling of the same kind. Gas moved out of one member flows back from the others, so move_gas
/// treats a joined set as one unit. Only these joins are followed, each under the condition its OnAtmosphericTick
/// checks (CODE, Assets.Scripts.Objects.Pipes unless noted):
///   GasTankStorage.OnAtmosphericTick: every ConnectedGasCanisters canister with every ConnectedPipeNetworks network,
///     all matter, while it has at least one of each.
///   PortablesConnector.OnAtmosphericTick: the TankSlot portable with InputNetwork (gases only) and InputNetwork2
///     (liquids only).
///   Connector.OnAtmosphericTick: the TankSlot portable with the connector's own PipeNetwork, all matter.
///   DynamicGasCanister.OnAtmosphericTick (Assets.Scripts.Objects): the portable tank with the GasCanister in its
///     GasCanisterSlot, in its MatterType.
///   DeviceInternal.OnAtmosphericTick: the device's internal atmosphere with ConnectedPipeNetwork, in its MatterState.
///     This covers Tank, which calls it first, AtmosphericSeat and HydroponicsStation. It does not cover Fridge and
///     FridgePowered, which override the tick without calling it and mix only under their own conditions.
///   DeviceMixAtmosphere.OnAtmosphericTick: the internal atmosphere with every ConnectedPipeNetworks network, all
///     matter (HydroponicsTrayDevice, RocketGasCollector).
///   Valve.OnAtmosphericTick: ConnectedPipeNetworks[0] with [1], all matter, while OnOff, Error != 1 and powered if it
///     has a power state.
/// A network counts only while IsNetworkValid and it has an Atmosphere. The game also skips a tick while a network has
/// a queued event (IsAwaitingEvent); that is transient and ignored here. Joins that move gas only part-way (pumps,
/// regulators, vents, radiators, heat exchangers, suits, rooms) are not followed.
/// </summary>
internal sealed class JoinEdge
{
    internal JoinEdge(GasEnd a, GasEnd b, AtmosphereHelper.MatterState state, Thing joiner)
    {
        A = a;
        B = b;
        State = state;
        Joiner = joiner;
    }

    internal GasEnd A { get; }

    internal GasEnd B { get; }

    internal AtmosphereHelper.MatterState State { get; }

    internal Thing Joiner { get; }

    internal bool Carries(AtmosphereHelper.MatterState? state) =>
        !state.HasValue || State == AtmosphereHelper.MatterState.All || State == state.Value;

    internal GasEnd Other(GasEnd end) => ReferenceEquals(A.Atmosphere, end.Atmosphere) ? B : A;

    internal bool Touches(GasEnd end) =>
        ReferenceEquals(A.Atmosphere, end.Atmosphere) || ReferenceEquals(B.Atmosphere, end.Atmosphere);
}

/// <summary>The atmospheres joined to a start, the start first, and the things that join them.</summary>
internal sealed class JoinedSet
{
    private JoinedSet(List<GasEnd> members, List<Thing> joiners)
    {
        Members = members;
        Joiners = joiners;
    }

    internal List<GasEnd> Members { get; }

    internal List<Thing> Joiners { get; }

    internal static JoinedSet Alone(GasEnd end) => new JoinedSet(new List<GasEnd> { end }, new List<Thing>());

    internal bool Contains(Atmosphere atmosphere) => IndexOf(atmosphere) >= 0;

    internal int IndexOf(Atmosphere atmosphere)
    {
        for (int index = 0; index < Members.Count; index++)
        {
            if (ReferenceEquals(Members[index].Atmosphere, atmosphere))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Everything reachable from the start through joins that carry the given matter state (null: any state).
    /// </summary>
    internal static JoinedSet Collect(GasEnd start, AtmosphereHelper.MatterState? state)
    {
        JoinedSet set = Alone(start);
        for (int next = 0; next < set.Members.Count; next++)
        {
            GasEnd end = set.Members[next];
            foreach (JoinEdge edge in GasJoins.EdgesAt(end))
            {
                if (edge.Touches(end) && edge.Carries(state))
                {
                    set.Add(edge.Other(end), edge.Joiner);
                }
            }
        }

        return set;
    }

    private void Add(GasEnd member, Thing? joiner)
    {
        if (!Contains(member.Atmosphere))
        {
            if (Members.Count >= GasJoins.MaximumMembers)
            {
                throw ApiErrors.Refused("joined_too_large",
                    $"More than {GasJoins.MaximumMembers} atmospheres are joined; pass joined: false to move from " +
                    "the named one only.");
            }

            Members.Add(member);
        }

        if (joiner != null && !Joiners.Contains(joiner))
        {
            Joiners.Add(joiner);
        }
    }

    /// <summary>The members of this set and then those of another that this one lacks.</summary>
    internal JoinedSet Union(JoinedSet other)
    {
        JoinedSet union = new JoinedSet(new List<GasEnd>(Members), new List<Thing>(Joiners));
        foreach (GasEnd member in other.Members)
        {
            if (!union.Contains(member.Atmosphere))
            {
                union.Members.Add(member);
            }
        }

        foreach (Thing joiner in other.Joiners)
        {
            if (!union.Joiners.Contains(joiner))
            {
                union.Joiners.Add(joiner);
            }
        }

        return union;
    }
}

/// <summary>The joins the game makes at one atmosphere, read from the joining devices.</summary>
internal static class GasJoins
{
    internal const int MaximumMembers = 64;

    internal static List<JoinEdge> EdgesAt(GasEnd end)
    {
        List<JoinEdge> edges = new List<JoinEdge>();
        if (end.Network is PipeNetwork network)
        {
            AddNetworkJoiners(network, edges);
        }

        if (end.Thing != null)
        {
            AddJoiner(end.Thing, edges);
            Thing? holder = end.Thing is DynamicThing held && held.ParentSlot != null ? held.ParentSlot.Parent : null;
            if (holder != null)
            {
                AddJoiner(holder, edges);
            }
        }

        return edges;
    }

    // A network's joiners are its devices and, for the Connector, one of its own pipes.
    private static void AddNetworkJoiners(PipeNetwork network, List<JoinEdge> edges)
    {
        List<Device> devices;
        lock (network.DeviceList)
        {
            devices = new List<Device>(network.DeviceList);
        }

        foreach (Device device in devices)
        {
            AddJoiner(device, edges);
        }

        List<INetworkedStructure> structures;
        lock (network.StructureList)
        {
            structures = new List<INetworkedStructure>(network.StructureList);
        }

        foreach (INetworkedStructure structure in structures)
        {
            if (structure is Connector connector)
            {
                AddJoiner(connector, edges);
            }
        }
    }

    private static void AddJoiner(Thing joiner, List<JoinEdge> edges)
    {
        switch (joiner)
        {
            case GasTankStorage storage:
                AddTankStorage(storage, edges);
                break;
            case PortablesConnector connector:
                AddPortablesConnector(connector, edges);
                break;
            case Connector connector:
                AddPortable(connector.ConnectedPortableAtmospherics, connector.PipeNetwork,
                    AtmosphereHelper.MatterState.All, connector, edges);
                break;
            case DynamicGasCanister tank:
                GasCanister? held = tank.GasCanisterSlot != null ? tank.GasCanisterSlot.Get() as GasCanister : null;
                AddPair(GasEnd.OfThing(tank), GasEnd.OfThing(held), tank.MatterType, tank, edges);
                break;
            case Fridge _:
            case FridgePowered _:
                break;
            case DeviceInternal device:
                AddPortable(device, device.ConnectedPipeNetwork, device.MatterState, device, edges);
                break;
            case DeviceMixAtmosphere device:
                AddEachNetwork(GasEnd.OfThing(device), device.ConnectedPipeNetworks, device, edges);
                break;
            case Valve valve:
                AddValve(valve, edges);
                break;
        }
    }

    private static void AddTankStorage(GasTankStorage storage, List<JoinEdge> edges)
    {
        foreach (GasCanister canister in storage.ConnectedGasCanisters)
        {
            AddEachNetwork(GasEnd.OfThing(canister), storage.ConnectedPipeNetworks, storage, edges);
        }
    }

    private static void AddPortablesConnector(PortablesConnector connector, List<JoinEdge> edges)
    {
        PortableAtmospherics? portable =
            connector.TankSlot != null ? connector.TankSlot.Get<PortableAtmospherics>() : null;
        AddPortable(portable, connector.InputNetwork, AtmosphereHelper.MatterState.Gas, connector, edges);
        AddPortable(portable, connector.InputNetwork2, AtmosphereHelper.MatterState.Liquid, connector, edges);
    }

    private static void AddPortable(Thing? holder, PipeNetwork? network, AtmosphereHelper.MatterState state,
        Thing joiner, List<JoinEdge> edges)
    {
        if (IsLive(network))
        {
            AddPair(GasEnd.OfThing(holder), GasEnd.OfNetwork(network), state, joiner, edges);
        }
    }

    private static void AddEachNetwork(GasEnd? end, List<PipeNetwork>? networks, Thing joiner, List<JoinEdge> edges)
    {
        if (end == null || networks == null)
        {
            return;
        }

        foreach (PipeNetwork network in networks)
        {
            if (IsLive(network))
            {
                AddPair(end, GasEnd.OfNetwork(network), AtmosphereHelper.MatterState.All, joiner, edges);
            }
        }
    }

    private static void AddValve(Valve valve, List<JoinEdge> edges)
    {
        List<PipeNetwork>? networks = valve.ConnectedPipeNetworks;
        bool open = valve.OnOff && valve.Error != 1 && (!valve.HasPowerState || valve.Powered);
        if (open && networks != null && networks.Count >= 2 && IsLive(networks[0]) && IsLive(networks[1]))
        {
            AddPair(GasEnd.OfNetwork(networks[0]), GasEnd.OfNetwork(networks[1]), AtmosphereHelper.MatterState.All,
                valve, edges);
        }
    }

    private static void AddPair(GasEnd? a, GasEnd? b, AtmosphereHelper.MatterState state, Thing joiner,
        List<JoinEdge> edges)
    {
        if (a != null && b != null && !ReferenceEquals(a.Atmosphere, b.Atmosphere))
        {
            edges.Add(new JoinEdge(a, b, state, joiner));
        }
    }

    private static bool IsLive(PipeNetwork? network) =>
        network != null && network.Atmosphere != null && network.IsNetworkValid();
}
