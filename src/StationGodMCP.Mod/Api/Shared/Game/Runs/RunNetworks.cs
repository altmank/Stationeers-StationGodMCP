#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>What the kinds' guards read about an edit: the networks before it, its new pieces and its removals.</summary>
internal sealed class RunNetworkContext
{
    internal Dictionary<long, IReferencable> NetworksBefore { get; } = new Dictionary<long, IReferencable>();

    /// <summary>Each new piece's forecast id and the prefab it is built from.</summary>
    internal Dictionary<long, Structure> NewPieces { get; } = new Dictionary<long, Structure>();

    /// <summary>Each removed piece by id.</summary>
    internal Dictionary<long, SmallGrid> Removed { get; } = new Dictionary<long, SmallGrid>();
}

/// <summary>
/// Network summaries and the kinds' guards. Cables (PowerTick, CODE): the flow min(potential, required) of the
/// networks pooled must stay at or under the weakest cable left, new ones included, or the game burns a cable or
/// breaks a fuse every power tick. Pipes: the contents of the networks pooled, in their volume less removed pipes
/// and plus new ones, must stay at or under the weakest pipe (Pipe.MaxPressure), or pipes burst.
/// </summary>
internal static class RunNetworks
{
    private const double TraceFraction = 0.001;
    private const double ContentsMol = 1e-3;

    internal static List<T> Copy<T>(List<T> list) where T : class
    {
        List<T> copy;
        lock (list)
        {
            copy = new List<T>(list);
        }

        copy.RemoveAll(static item => item == null || (item is UnityEngine.Object unity && unity == null));
        return copy;
    }

    internal static RunCableNetworkView CableSummary(CableNetwork network)
    {
        List<Cable> cables = Copy(network.CableList);
        return new RunCableNetworkView(new ThingId(network.ReferenceId), cables.Count,
            new CableNetworkRatings(network.RequiredLoad, network.PotentialLoad, LowestCable(cables, null), null,
                LowestFuse(network)), Views(Copy(network.DeviceList)));
    }

    internal static RunPipeNetworkView PipeSummary(PipeNetwork network)
    {
        GasSnapshot? snapshot = network.Atmosphere != null ? GasSnapshot.Of(network.Atmosphere) : null;
        return new RunPipeNetworkView(new ThingId(network.ReferenceId), network.NetworkContentType.ToString(),
            PipeMembers(network).Count, AirOf(snapshot), LowestPipe(PipeMembers(network), null), GasesOf(snapshot),
            Views(Copy(network.DeviceList)));
    }

    internal static KindGuard PowerGuard(ForecastNetwork after, RunNetworkContext context)
    {
        Dictionary<long, NetworkPower> before = new Dictionary<long, NetworkPower>();
        foreach (long id in after.NetworksBefore)
        {
            if (context.NetworksBefore.TryGetValue(id, out IReferencable found) && found is CableNetwork network)
            {
                before[id] = new NetworkPower(network.PotentialLoad, network.RequiredLoad,
                    LowestCable(Copy(network.CableList), context.Removed), LowestFuse(network));
            }
        }

        Dictionary<long, double> ratings = new Dictionary<long, double>();
        foreach (long piece in after.NewPieces)
        {
            if (context.NewPieces.TryGetValue(piece, out Structure prefab) && prefab is Cable cable)
            {
                ratings[piece] = cable.MaxVoltage;
            }
        }

        PowerAfter power = PowerAfter.Of(after, before, ratings);
        RunPowerAfterView view = new RunPowerAfterView(power.PotentialW, power.RequiredW, power.FlowW,
            power.LowestCableW, power.LowestFuseW, power.Overloads);
        return power.Overloads
            ? new KindGuard(view, "would_overload",
                $"The network would carry {power.FlowW:0} W (min of {power.PotentialW:0} W potential and " +
                $"{power.RequiredW:0} W required) over a cable rated {power.LowestCableW:0} W; the game would burn " +
                "a cable every power tick. Use a higher grade, or keep the networks apart.")
            : new KindGuard(view, null, null);
    }

    internal static KindGuard PipeGuard(ForecastNetwork after, RunNetworkContext context)
    {
        List<GasSnapshot> parts = new List<GasSnapshot>();
        double volumeChange = 0.0;
        double? lowest = null;
        foreach (long id in after.NetworksBefore)
        {
            if (!context.NetworksBefore.TryGetValue(id, out IReferencable found) || !(found is PipeNetwork network))
            {
                continue;
            }

            if (network.Atmosphere != null)
            {
                parts.Add(GasSnapshot.Of(network.Atmosphere));
            }

            lowest = Lowest(lowest, LowestPipe(PipeMembers(network), context.Removed));
        }

        foreach (SmallGrid removed in context.Removed.Values)
        {
            if (removed is Pipe pipe && pipe.PipeNetwork != null &&
                after.NetworksBefore.Contains(pipe.PipeNetwork.ReferenceId))
            {
                volumeChange -= PipeFamily.VolumeOf(pipe);
            }
        }

        foreach (long piece in after.NewPieces)
        {
            if (context.NewPieces.TryGetValue(piece, out Structure prefab) && prefab is Pipe pipe)
            {
                volumeChange += PipeFamily.VolumeOf(pipe);
                lowest = Lowest(lowest, pipe.MaxPressure.ToDouble());
            }
        }

        if (parts.Count == 0)
        {
            // A network made of new pieces only: empty, in the new pieces' volume.
            PipeNetworkAir empty = new PipeNetworkAir(0.0, 0.0, 0.0, Math.Max(0.0, volumeChange), 0.0);
            return new KindGuard(new RunPipeAfterView(empty, lowest, false), null, null);
        }

        GasSnapshot pooled = GasSnapshot.Pool(parts.ToArray());
        GasSnapshot air = pooled.WithVolume(Math.Max(0.0, pooled.VolumeL + volumeChange));
        PipeNetworkAir view = AirOf(air);
        bool burst = lowest.HasValue && view.PressureKpa > lowest.Value;
        return new KindGuard(new RunPipeAfterView(view, lowest, burst),
            burst ? "would_burst" : null,
            burst
                ? $"The network would be at {view.PressureKpa:0.#} kPa with a pipe rated {lowest!.Value:0.#} kPa."
                : null);
    }

    /// <summary>
    /// Removing pipes never loses or moves contents: a network with contents may not be emptied (the game's removal
    /// of its last pipe deletes them) or split (the game divides them among the parts by volume, which this does not
    /// predict).
    /// </summary>
    internal static LayoutIssue? PipeRemovalProblem(Forecast forecast, RunNetworkContext context)
    {
        foreach (long gone in forecast.Gone)
        {
            double moles = MolesOf(context, gone);
            if (moles > ContentsMol)
            {
                return new LayoutIssue(UpgradeFamily.HoldsContents,
                    $"Removing every pipe of network {gone} would delete its {moles:0.###} mol; empty it first.", null,
                    gone);
            }
        }

        foreach (ForecastSplit split in forecast.Splits)
        {
            double moles = MolesOf(context, split.Network);
            if (moles > ContentsMol)
            {
                return new LayoutIssue("contents_would_move",
                    $"Network {split.Network} holds {moles:0.###} mol and would split in {split.Parts.Count}; the " +
                    "game would divide the contents among the parts. Empty it first.", null, split.Network);
            }
        }

        return null;
    }

    private static double MolesOf(RunNetworkContext context, long id) =>
        context.NetworksBefore.TryGetValue(id, out IReferencable found) && found is PipeNetwork network &&
        network.Atmosphere != null
            ? GasSnapshot.Of(network.Atmosphere).TotalMol()
            : 0.0;

    internal static List<SmallGrid> PipeMembers(PipeNetwork network)
    {
        List<INetworkedStructure> members = Copy(network.StructureList);
        List<SmallGrid> pieces = new List<SmallGrid>(members.Count);
        foreach (INetworkedStructure member in members)
        {
            if (member.GetAsThing is SmallGrid grid && grid != null)
            {
                pieces.Add(grid);
            }
        }

        return pieces;
    }

    private static double? LowestCable(List<Cable> cables, Dictionary<long, SmallGrid>? removed)
    {
        double? lowest = null;
        foreach (Cable cable in cables)
        {
            if (removed == null || !removed.ContainsKey(cable.ReferenceId))
            {
                lowest = Lowest(lowest, cable.MaxVoltage);
            }
        }

        return lowest;
    }

    private static double? LowestFuse(CableNetwork network)
    {
        double? lowest = null;
        foreach (CableFuse fuse in Copy(network.FuseList))
        {
            lowest = Lowest(lowest, fuse.PowerBreak);
        }

        return lowest;
    }

    private static double? LowestPipe(List<SmallGrid> members, Dictionary<long, SmallGrid>? removed)
    {
        double? lowest = null;
        foreach (SmallGrid member in members)
        {
            if (member is Pipe pipe && (removed == null || !removed.ContainsKey(pipe.ReferenceId)))
            {
                lowest = Lowest(lowest, pipe.MaxPressure.ToDouble());
            }
        }

        return lowest;
    }

    private static double? Lowest(double? a, double? b) =>
        !a.HasValue ? b : !b.HasValue ? a : Math.Min(a.Value, b.Value);

    private static PipeNetworkAir AirOf(GasSnapshot? snapshot) =>
        snapshot == null
            ? new PipeNetworkAir(0.0, 0.0, 0.0, 0.0, 0.0)
            : new PipeNetworkAir(snapshot.TotalMol(), snapshot.EnergyJ(),
                snapshot.TotalMol() > 0.0 ? snapshot.TemperatureK() : 0.0, snapshot.VolumeL,
                snapshot.PressureKpa());

    private static List<RunGasView> GasesOf(GasSnapshot? snapshot)
    {
        List<RunGasView> gases = new List<RunGasView>();
        if (snapshot == null)
        {
            return gases;
        }

        double total = snapshot.TotalMol();
        for (int index = 0; index < GasTypes.All.Length; index++)
        {
            double moles = snapshot.MolesOf(index);
            if (total > 0.0 && moles / total > TraceFraction)
            {
                gases.Add(new RunGasView(GasTypes.All[index].ToString(), moles, moles / total));
            }
        }

        gases.Sort(static (a, b) => b.Mol.CompareTo(a.Mol));
        return gases;
    }

    internal static List<ThingView> Views(List<Device> devices)
    {
        List<ThingView> views = new List<ThingView>(devices.Count);
        foreach (Device device in devices)
        {
            views.Add(GameLookup.ViewOf(device));
        }

        return views;
    }
}
