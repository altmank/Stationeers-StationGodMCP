#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Keeps a held-tick job from losing pipe network contents, and proves it did not.
/// <para>
/// The game moves gas between pipe networks through queued events, applied only at the start of the next game tick
/// (AtmosphericsController.HandleMainThreadEvents): a merge queues "add the old network's gas to the survivor"
/// (AtmosphericsNetwork.Merge: AtmosphericEventInstance.CreateAdd(Atmosphere, old.Atmosphere.GasMixture), a copy
/// taken now), a split queues "set each new network to its share" (Pipe.OnDestroy:
/// NetworkAtmosphereEvent.DivideNetworkAtmosphere). A player never gets two of these into one tick; a job that holds
/// the tick and builds a whole run in one frame does. With two merges in one tick the second copies a survivor that has
/// not yet received the first one's gas, and the first event later fills an atmosphere whose network has been merged
/// away: the gas is gone from every live network. That network is not even deregistered while its event is queued
/// (ReferencableNetwork.RefreshNetwork skips a network whose atmosphere IsAwaitingEvent), so it stays as a ghost with
/// no pipes and the devices still registered on it.
/// </para>
/// <para>
/// Settle applies the queued events at once, as the tick would, after every piece a job builds, so each merge and
/// split sees the contents the one before it left. It runs them where the tick runs them, off the main thread: a
/// Mole's getters answer last tick's cached values on the main thread (Mole.Quantity: AtmosphereHelper.CanWriteAccess
/// is ThreadedManager.IsThread), and the game's own save cleanup (SaveHelper.PrepareToSave: AtmosphericsController
/// .PreSaveCleanup) calls the same queues from a pool thread while the tick is held. Every pipe network's cache is
/// refreshed after, since a split on the main thread divides the cached values (Pipe.OnDestroy's GasMixture.Set).
/// Open settles first, so the job's removals divide live contents too.
/// </para>
/// <para>
/// Open reads every pipe network before the job; Close reads them again once everything is applied and compares each
/// family of networks the job changed (GasAudit). A family short of gas gets what it lacks put back, and the pipeless
/// networks left holding the lost copy are emptied and dropped; anything still wrong fails the check and holds further
/// pipe jobs (GasHold).
/// </para>
/// </summary>
internal abstract class JobGas
{
    internal static JobGas Untracked { get; } = new UntrackedGas();

    /// <summary>Starts tracking for a job that touches pipe networks, once the tick is held; Untracked otherwise.</summary>
    internal static JobGas Open(bool pipeNetworks) => pipeNetworks && PipeGasQueue.CanRun()
        ? new TrackedGas(PipeGasReading.Take())
        : Untracked;

    /// <summary>Applies every queued gas change now, as the next tick would.</summary>
    internal abstract void Settle();

    /// <summary>The job's gas check; null for a job that is not tracked.</summary>
    internal abstract GasCheckView? Close(string jobId);

    private sealed class UntrackedGas : JobGas
    {
        internal override void Settle()
        {
        }

        internal override GasCheckView? Close(string jobId) => null;
    }

    private sealed class TrackedGas : JobGas
    {
        private readonly PipeGasReading _before;

        internal TrackedGas(PipeGasReading before)
        {
            _before = before;
        }

        internal override void Settle()
        {
            if (PipeGasQueue.CanRun())
            {
                AtmosphericsThread.Run(PipeGasQueue.ApplyQueued);
            }
        }

        internal override GasCheckView Close(string jobId)
        {
            if (!PipeGasQueue.CanRun())
            {
                return GasCheckView.Unchecked(
                    "The contents were not checked: a save took the game tick before the check could run.");
            }

            GasTolerance tolerance = GasTolerance.Default;
            PipeGasReading after = PipeGasReading.Take();
            GasAudit audit = GasAudit.Of(_before.Networks, after.Networks, tolerance);
            List<GasRefill> refills = new List<GasRefill>();
            List<long> cleared = new List<long>();
            if (!audit.Ok)
            {
                refills = GasRefills.For(audit, tolerance);
                after.Refill(refills);
                after = PipeGasReading.Take();
                audit = GasAudit.Of(_before.Networks, after.Networks, tolerance);
                if (audit.Families.TrueForAll(static family => family.Emptied || family.Conserved))
                {
                    cleared = after.ClearGhosts(audit.Ghosts);
                    after = PipeGasReading.Take();
                    audit = GasAudit.Of(_before.Networks, after.Networks, tolerance);
                }
            }

            GasCheckView check = GasCheckView.Of(audit, refills, cleared,
                GasOrphans.Of(_before.Orphans, after.Orphans));
            if (!check.Ok)
            {
                GasHold.Set(jobId, check.Summary);
                StationGodMod.LogWarning($"job {jobId} gas check failed: {check.Summary}");
            }

            return check;
        }
    }
}

/// <summary>
/// Pipe jobs refused after a job's gas check failed, so a fault that lost gas once cannot lose more before someone
/// looks. Lifted when the world is left (loading a save or going to the menu; checked every frame by
/// StationGodMod.Update).
/// </summary>
internal static class GasHold
{
    private static string? _reason;

    internal static bool Held => _reason != null;

    internal static void Set(string jobId, string summary) =>
        _reason = $"Job {jobId}'s gas check failed: {summary} Poll that job with job_id for its gas_check.";

    internal static ApiException Refusal() => ApiErrors.Refused("gas_check_failed", _reason ?? string.Empty);

    /// <summary>Called every frame: a world that is no longer running is not the one the hold was for.</summary>
    internal static void LiftIfWorldLeft()
    {
        if (_reason != null && (GameManager.GameState == GameState.None || GameManager.GameState == GameState.Loading))
        {
            _reason = null;
        }
    }
}

/// <summary>The game's queued gas changes, applied as the start of a game tick applies them.</summary>
internal static class PipeGasQueue
{
    /// <summary>
    /// Only while this job holds the tick: the tick's own thread is then idle, and a save (which applies the queues
    /// itself) is not running. The check is made on the main thread, which a save must pass through to start.
    /// </summary>
    internal static bool CanRun() =>
        GameManager.RunSimulation && GameManager.GameTickPaused && !HeldTickJobs.IsSaving();

    /// <summary>
    /// AtmosphericsController.HandleMainThreadEvents without the mod's own move_gas postfix: split events, then merge
    /// and other atmosphere events, then the awaiting grids cleared. Then every pipe network's cached values are set
    /// to its live ones (Atmosphere.UpdateCache, as AtmosphericsManager.RunCacheAtmosphereDataJobs does each tick):
    /// the tick caches before it mixes and runs the devices, so a split made on the main thread would otherwise divide
    /// contents a tick old. On a pool thread only.
    /// </summary>
    internal static bool ApplyAll()
    {
        HandleQueues();
        foreach (PipeNetwork network in PipeNetwork.AllPipeNetworks.Active())
        {
            network?.Atmosphere?.UpdateCache();
        }

        return true;
    }

    /// <summary>
    /// As ApplyAll, refreshing only the caches of the pipe atmospheres an event was queued for (every event on an
    /// atmosphere marks it IsAwaitingEvent): once ApplyAll has run, the others are still live. On a pool thread only.
    /// </summary>
    internal static bool ApplyQueued()
    {
        List<Atmosphere> touched = new List<Atmosphere>();
        foreach (PipeNetwork network in PipeNetwork.AllPipeNetworks.Active())
        {
            if (network?.Atmosphere is { IsAwaitingEvent: true } atmosphere)
            {
                touched.Add(atmosphere);
            }
        }

        HandleQueues();
        foreach (Atmosphere atmosphere in touched)
        {
            atmosphere.UpdateCache();
        }

        return true;
    }

    private static void HandleQueues()
    {
        NetworkAtmosphereEvent.HandleNetworkChangedEvents();
        AtmosphericEventInstance.HandleNetworkChangedEvents();
        AtmosphericEventInstance.ClearAwaiting();
    }
}

/// <summary>Runs atmosphere work where the game tick runs it: on a pool thread, while the main thread waits.</summary>
internal static class AtmosphericsThread
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    internal static T Run<T>(Func<T> work)
    {
        Task<T> task = Task.Run(work);
        try
        {
            if (!task.Wait(Limit))
            {
                throw new TimeoutException($"Atmosphere work did not finish within {Limit.TotalSeconds} s.");
            }
        }
        catch (AggregateException failure) when (failure.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
        }

        return task.Result;
    }
}

/// <summary>
/// Every pipe network as it stands: members, their cells and devices read on the main thread, contents (with every queued change
/// applied first) on a pool thread, where the Mole getters give the live values. Besides the networks the game lists,
/// every network a pipe still names that the game no longer lists (an orphan, GasOrphans) is read too, so its gas
/// counts where it sits.
/// </summary>
internal sealed class PipeGasReading
{
    private readonly Dictionary<long, PipeNetwork> _byId;

    private PipeGasReading(List<NetworkGas> networks, List<NetworkGas> orphans, Dictionary<long, PipeNetwork> byId)
    {
        Networks = networks;
        Orphans = orphans;
        _byId = byId;
    }

    /// <summary>Every network read: the listed ones, then the orphans.</summary>
    internal List<NetworkGas> Networks { get; }

    /// <summary>The networks pipes name that the game no longer lists.</summary>
    internal List<NetworkGas> Orphans { get; }

    internal static PipeGasReading Take()
    {
        List<PipeNetwork> networks = new List<PipeNetwork>();
        foreach (PipeNetwork network in PipeNetwork.AllPipeNetworks.Active())
        {
            if (network != null)
            {
                networks.Add(network);
            }
        }

        int listed = networks.Count;
        networks.AddRange(OrphansBeside(networks));
        List<long[]> members = networks.ConvertAll(MembersOf);
        List<long[]> devices = networks.ConvertAll(DevicesOf);
        List<List<GridCell>> cells = networks.ConvertAll(CellsOf);
        List<(GasMix gas, double volumeL)> contents = AtmosphericsThread.Run(() =>
        {
            PipeGasQueue.ApplyAll();
            return networks.ConvertAll(ContentsOf);
        });

        List<NetworkGas> read = new List<NetworkGas>(networks.Count);
        Dictionary<long, PipeNetwork> byId = new Dictionary<long, PipeNetwork>(listed);
        for (int index = 0; index < networks.Count; index++)
        {
            read.Add(new NetworkGas(networks[index].ReferenceId, contents[index].gas, contents[index].volumeL,
                members[index], devices[index], cells[index]));
            if (index < listed)
            {
                // Refills and ghost clearing only ever touch a network the game lists.
                byId[networks[index].ReferenceId] = networks[index];
            }
        }

        return new PipeGasReading(read, read.GetRange(listed, read.Count - listed), byId);
    }

    // Every network a live pipe names that is not among the listed ones (AtmosphericsManager.AtmosphericThings holds
    // every registered pipe: Pipe.OnRegistered adds it, OnDeregistered takes it off). Main thread.
    private static List<PipeNetwork> OrphansBeside(List<PipeNetwork> listed)
    {
        HashSet<PipeNetwork> known = new HashSet<PipeNetwork>(listed);
        List<PipeNetwork> orphans = new List<PipeNetwork>();
        foreach (Thing thing in AtmosphericsManager.AtmosphericThings.Active())
        {
            if (thing is Pipe pipe && pipe != null && !pipe.IsBeingDestroyed && pipe.PipeNetwork != null &&
                known.Add(pipe.PipeNetwork))
            {
                orphans.Add(pipe.PipeNetwork);
            }
        }

        return orphans;
    }

    /// <summary>Puts each refill into its network's atmosphere, on a pool thread.</summary>
    internal void Refill(List<GasRefill> refills)
    {
        AtmosphericsThread.Run(() =>
        {
            foreach (GasRefill refill in refills)
            {
                if (_byId.TryGetValue(refill.Into, out PipeNetwork network) && network.Atmosphere != null)
                {
                    network.Atmosphere.Add(MixtureOf(refill.Gas));
                    network.Atmosphere.UpdateCache();
                }
            }

            return true;
        });
    }

    /// <summary>
    /// Empties each ghost's atmosphere (pool thread), then lets the game drop the pipeless network (main thread:
    /// ReferencableNetwork.RefreshNetwork deregisters a network without members once no event is awaited, and
    /// PipeNetwork.OnDeregister takes it off every device). Returns the ids cleared.
    /// </summary>
    internal List<long> ClearGhosts(List<GasNetworkGhost> ghosts)
    {
        List<PipeNetwork> found = new List<PipeNetwork>();
        foreach (GasNetworkGhost ghost in ghosts)
        {
            if (_byId.TryGetValue(ghost.Network.Id, out PipeNetwork network))
            {
                found.Add(network);
            }
        }

        AtmosphericsThread.Run(() =>
        {
            foreach (PipeNetwork network in found)
            {
                if (network.Atmosphere != null)
                {
                    network.Atmosphere.GasMixture.Reset();
                    network.Atmosphere.IsAwaitingEvent = false;
                    network.Atmosphere.UpdateCache();
                }
            }

            return true;
        });

        List<long> cleared = new List<long>(found.Count);
        foreach (PipeNetwork network in found)
        {
            network.RefreshNetwork();
            cleared.Add(network.ReferenceId);
        }

        return cleared;
    }

    private static long[] MembersOf(PipeNetwork network)
    {
        lock (network.StructureList)
        {
            List<long> ids = new List<long>(network.StructureList.Count);
            foreach (INetworkedStructure member in network.StructureList)
            {
                if (member != null)
                {
                    ids.Add(member.ReferenceId);
                }
            }

            return ids.ToArray();
        }
    }

    // The cells the members fill as registered (GasAudit links a replaced pipe to what stands in its cells now).
    private static List<GridCell> CellsOf(PipeNetwork network)
    {
        List<SmallGrid> pieces = new List<SmallGrid>();
        lock (network.StructureList)
        {
            foreach (INetworkedStructure member in network.StructureList)
            {
                if (member is SmallGrid piece && piece != null)
                {
                    pieces.Add(piece);
                }
            }
        }

        List<GridCell> cells = new List<GridCell>(pieces.Count);
        foreach (SmallGrid piece in pieces)
        {
            cells.AddRange(PieceShapes.RegisteredCells(piece));
        }

        return cells;
    }

    private static long[] DevicesOf(PipeNetwork network)
    {
        List<long> ids = new List<long>(network.DeviceList.Count);
        foreach (Device device in network.DeviceList)
        {
            if (device != null)
            {
                ids.Add(device.ReferenceId);
            }
        }

        return ids.ToArray();
    }

    // On a pool thread: the Mole getters' live values.
    private static (GasMix gas, double volumeL) ContentsOf(PipeNetwork network)
    {
        int count = GasTypes.All.Length;
        double[] moles = new double[count];
        double[] energies = new double[count];
        Atmosphere? atmosphere = network.Atmosphere;
        if (atmosphere == null)
        {
            return (new GasMix(moles, energies), 0.0);
        }

        for (int index = 0; index < count; index++)
        {
            Mole mole = atmosphere.GasMixture.GetMoleValue(GasTypes.All[index]);
            moles[index] = mole.Quantity.ToDouble();
            energies[index] = mole.Energy.ToDouble();
        }

        return (new GasMix(moles, energies), atmosphere.Volume.ToDouble());
    }

    private static GasMixture MixtureOf(GasMix gas)
    {
        GasMixture mixture = GasMixtureHelper.Create();
        for (int index = 0; index < gas.Types; index++)
        {
            if (gas.MolesOf(index) > 0.0)
            {
                mixture.Add(new Mole(GasTypes.All[index], new MoleQuantity(gas.MolesOf(index)),
                    new MoleEnergy(gas.EnergyOf(index))));
            }
        }

        return mixture;
    }
}
