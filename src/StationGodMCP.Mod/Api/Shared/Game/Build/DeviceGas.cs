#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>
/// remove_structure gas_to: a removed device's own atmosphere handed to a pipe network before the device goes, instead
/// of being deleted with it (the game's deconstruction deletes a device's atmosphere; a tank lets its gas out where it
/// stood). The gas goes as Mole values with their energy (Atmosphere.Add), on the atmospherics thread, where live
/// values are read and written.
/// </summary>
internal static class DeviceGas
{
    internal const string Connected = "connected";

    /// <summary>The pipe networks the device's ends join, in its ends' order.</summary>
    internal static List<PipeNetwork> ConnectedNetworks(Structure piece)
    {
        List<PipeNetwork> networks = new List<PipeNetwork>();
        foreach (NetworkRefView reference in EndsReader.NetworksOf(piece))
        {
            if (reference.Kind == "pipe" && Referencable.Find<PipeNetwork>(reference.Id.Value) is PipeNetwork network &&
                !networks.Contains(network))
            {
                networks.Add(network);
            }
        }

        return networks;
    }

    /// <summary>The network gas_to names: a pipe network id, or a pipe piece standing for its network; null if neither.</summary>
    internal static PipeNetwork? Named(ThingId id) =>
        Referencable.Find<PipeNetwork>(id.Value) is PipeNetwork network
            ? network
            : GameLookup.TryFindThing(id, out Thing thing) && thing is Pipe { PipeNetwork: { } own }
                ? own
                : null;

    /// <summary>An atmosphere's contents as the gas check reads them (cached values on the main thread).</summary>
    internal static GasMix MixOf(Atmosphere atmosphere)
    {
        int count = GasTypes.All.Length;
        double[] moles = new double[count];
        double[] energies = new double[count];
        for (int index = 0; index < count; index++)
        {
            Mole mole = atmosphere.GasMixture.GetMoleValue(GasTypes.All[index]);
            moles[index] = mole.Quantity.ToDouble();
            energies[index] = mole.Energy.ToDouble();
        }

        return new GasMix(moles, energies);
    }

    internal static bool HoldsLiquid(GasMix gas)
    {
        for (int index = 0; index < gas.Types; index++)
        {
            if (gas.MolesOf(index) > RemovalRule.GasFloorMol &&
                Mole.MatterState(GasTypes.All[index]) == AtmosphereHelper.MatterState.Liquid)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The network as a receiver of the gas: its pressure with the gas added, its weakest pipe, its content.</summary>
    internal static GasReceiver ReceiverOf(PipeNetwork network, GasMix added, HashSet<long> removed)
    {
        double? rating = null;
        bool liquid = false;
        bool allRemoved = true;
        lock (network.StructureList)
        {
            foreach (INetworkedStructure member in network.StructureList)
            {
                if (!(member is Pipe pipe) || pipe == null)
                {
                    continue;
                }

                double pipeRating = pipe.MaxPressure.ToDouble();
                rating = rating.HasValue ? Math.Min(rating.Value, pipeRating) : pipeRating;
                liquid |= pipe.PipeContentType == Pipe.ContentType.Liquid;
                allRemoved &= removed.Contains(pipe.ReferenceId);
            }
        }

        Atmosphere? atmosphere = network.Atmosphere;
        double pressure = atmosphere != null
            ? GasSnapshot.Of(MixOf(atmosphere).Plus(added), atmosphere.Volume.ToDouble()).PressureKpa()
            : double.PositiveInfinity;
        return new GasReceiver(network.ReferenceId, pressure, rating, liquid, rating.HasValue && allRemoved);
    }

    /// <summary>
    /// Moves everything in the device's atmosphere into the network's, on the atmospherics thread; returns what moved
    /// (live values), or null when either atmosphere is gone.
    /// </summary>
    internal static GasMix? HandOver(Structure piece, PipeNetwork network) =>
        AtmosphericsThread.Run<GasMix?>(() =>
        {
            Atmosphere? from = piece.InternalAtmosphere;
            Atmosphere? into = network.Atmosphere;
            if (from == null || into == null)
            {
                return null;
            }

            GasMix moved = MixOf(from);
            into.Add(from.GasMixture);
            from.GasMixture.Reset();
            into.UpdateCache();
            from.UpdateCache();
            return moved;
        });
}
