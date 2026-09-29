#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A pipe network's contents, gas by gas: the moles and energy of each single gas type, in one fixed order (the game
/// side's GasTypes.All). Immutable; sums and differences make new mixes.
/// </summary>
internal sealed class GasMix
{
    private readonly double[] _moles;
    private readonly double[] _energies;

    internal GasMix(double[] moles, double[] energies)
    {
        if (moles.Length != energies.Length)
        {
            throw new ArgumentException("A mix needs one energy per gas type.", nameof(energies));
        }

        _moles = moles;
        _energies = energies;
        for (int index = 0; index < moles.Length; index++)
        {
            TotalMol += moles[index];
            TotalEnergyJ += energies[index];
        }
    }

    internal static GasMix Empty(int types) => new GasMix(new double[types], new double[types]);

    internal int Types => _moles.Length;

    internal double TotalMol { get; }

    internal double TotalEnergyJ { get; }

    internal double MolesOf(int index) => _moles[index];

    internal double EnergyOf(int index) => _energies[index];

    internal GasMix Plus(GasMix other) => Combine(other, 1.0);

    internal GasMix Minus(GasMix other) => Combine(other, -1.0);

    /// <summary>Every amount times the factor (a share of the whole).</summary>
    internal GasMix Scaled(double factor)
    {
        double[] moles = new double[Types];
        double[] energies = new double[Types];
        for (int index = 0; index < Types; index++)
        {
            moles[index] = _moles[index] * factor;
            energies[index] = _energies[index] * factor;
        }

        return new GasMix(moles, energies);
    }

    /// <summary>What this mix has that the other lacks, gas by gas: the positive part of each difference.</summary>
    internal GasMix Lacking(GasMix after)
    {
        double[] moles = new double[Types];
        double[] energies = new double[Types];
        for (int index = 0; index < Types; index++)
        {
            moles[index] = Math.Max(0.0, _moles[index] - after._moles[index]);
            energies[index] = Math.Max(0.0, _energies[index] - after._energies[index]);
        }

        return new GasMix(moles, energies);
    }

    private GasMix Combine(GasMix other, double sign)
    {
        if (other.Types != Types)
        {
            throw new ArgumentException("Both mixes must list the same gas types.", nameof(other));
        }

        double[] moles = new double[Types];
        double[] energies = new double[Types];
        for (int index = 0; index < Types; index++)
        {
            moles[index] = _moles[index] + sign * other._moles[index];
            energies[index] = _energies[index] + sign * other._energies[index];
        }

        return new GasMix(moles, energies);
    }
}

/// <summary>
/// How far two totals may differ and still be the same contents: an absolute floor (moles, joules) plus a share of
/// the larger total, for the rounding of the game's double arithmetic.
/// </summary>
internal readonly struct GasTolerance
{
    internal static readonly GasTolerance Default = new GasTolerance(0.01, 10.0, 1e-5);

    internal GasTolerance(double absoluteMol, double absoluteJ, double relative)
    {
        AbsoluteMol = absoluteMol;
        AbsoluteJ = absoluteJ;
        Relative = relative;
    }

    internal double AbsoluteMol { get; }

    internal double AbsoluteJ { get; }

    internal double Relative { get; }

    internal bool Same(GasMix a, GasMix b) =>
        Close(a.TotalMol, b.TotalMol, AbsoluteMol) && Close(a.TotalEnergyJ, b.TotalEnergyJ, AbsoluteJ);

    /// <summary>Two amounts of gas, in moles, within the tolerance.</summary>
    internal bool SameMol(double a, double b) => Close(a, b, AbsoluteMol);

    /// <summary>Two energies, in joules, within the tolerance.</summary>
    internal bool SameEnergy(double a, double b) => Close(a, b, AbsoluteJ);

    internal bool Negligible(GasMix mix) => Close(mix.TotalMol, 0.0, AbsoluteMol) &&
                                           Close(mix.TotalEnergyJ, 0.0, AbsoluteJ);

    private bool Close(double a, double b, double absolute) =>
        Math.Abs(a - b) <= absolute + Relative * Math.Max(Math.Abs(a), Math.Abs(b));
}

/// <summary>
/// One pipe network as read at one moment: its id, contents, volume, the ids of its member pipes and of the devices
/// registered on it, and the small-grid cells its members fill. A network with no members is still listed by the game
/// while its atmosphere awaits an event (ReferencableNetwork.RefreshNetwork skips deregistering it): that is a ghost.
/// </summary>
internal sealed class NetworkGas
{
    internal NetworkGas(long id, GasMix gas, double volumeL, IReadOnlyList<long> members, IReadOnlyList<long> devices,
        IReadOnlyList<GridCell>? cells = null)
    {
        Id = id;
        Gas = gas;
        VolumeL = volumeL;
        Members = members;
        Devices = devices;
        Cells = cells ?? Array.Empty<GridCell>();
    }

    internal long Id { get; }

    internal GasMix Gas { get; }

    internal double VolumeL { get; }

    internal IReadOnlyList<long> Members { get; }

    internal IReadOnlyList<long> Devices { get; }

    /// <summary>The cells its member pipes fill, as registered on the small grid.</summary>
    internal IReadOnlyList<GridCell> Cells { get; }

    internal bool Live => Members.Count > 0;
}

/// <summary>
/// Networks a job touched that belong together: every network before and after that shares a pipe or a pipe's cell,
/// directly or through another. Its contents before (the networks live then) must equal its contents after (the
/// networks live now), unless every one of its pipes was removed and nothing stands in their cells (Emptied: the
/// game's removal of a network's last pipe deletes its contents, which the planner holds back unless allowed).
/// </summary>
internal sealed class GasFamily
{
    internal GasFamily(List<NetworkGas> before, List<NetworkGas> after, GasMix gasBefore, GasMix gasAfter,
        bool conserved)
    {
        Before = before;
        After = after;
        GasBefore = gasBefore;
        GasAfter = gasAfter;
        Conserved = conserved;
    }

    internal List<NetworkGas> Before { get; }

    internal List<NetworkGas> After { get; }

    internal GasMix GasBefore { get; }

    internal GasMix GasAfter { get; }

    internal bool Emptied => After.Count == 0;

    internal bool Conserved { get; }

    /// <summary>Contents before minus contents after: positive when gas went missing, negative when gas appeared.</summary>
    internal double MissingMol => GasBefore.TotalMol - GasAfter.TotalMol;

    internal double MissingEnergyJ => GasBefore.TotalEnergyJ - GasAfter.TotalEnergyJ;

    internal bool Contains(long id) => IndexIn(Before, id) >= 0 || IndexIn(After, id) >= 0;

    private static int IndexIn(List<NetworkGas> networks, long id) =>
        networks.FindIndex(network => network.Id == id);
}

/// <summary>What a job did to the pipe networks' contents, from a reading before it and one after.</summary>
internal sealed class GasAudit
{
    private GasAudit(List<GasFamily> families, List<GasNetworkGhost> ghosts, List<NetworkGas> oldGhosts, bool ok)
    {
        Families = families;
        Ghosts = ghosts;
        OldGhosts = oldGhosts;
        Ok = ok;
    }

    /// <summary>Every family of networks the job changed, in order of their lowest network id.</summary>
    internal List<GasFamily> Families { get; }

    /// <summary>
    /// Networks with no member left that hold gas and were not ghosts with that gas before the job, each with the
    /// family whose pipes it held before (null for a network the job made and lost).
    /// </summary>
    internal List<GasNetworkGhost> Ghosts { get; }

    /// <summary>Ghosts that were already there, holding the same gas, before the job: reported, not the job's.</summary>
    internal List<NetworkGas> OldGhosts { get; }

    /// <summary>Every family kept its contents (or was emptied by removal) and the job left no ghost holding gas.</summary>
    internal bool Ok { get; }

    /// <summary>
    /// Compares the two readings. A network is changed when it is gone, new, holds other contents or other pipes;
    /// unchanged networks are left out. Changed networks are joined into families by what physically stands: a pipe
    /// in network A before and network B after puts A and B in one family, and so does a cell a pipe of A filled
    /// before and a pipe of B fills after (a piece replaced in place, such as a long straight swapped for its singles,
    /// is a new pipe with a new id). Network ids are never followed: the game's merge keeps whichever network the
    /// joining piece met first (StructureNetwork.Merge), so the one that carries the contents on may be new.
    /// </summary>
    internal static GasAudit Of(IReadOnlyList<NetworkGas> before, IReadOnlyList<NetworkGas> after,
        GasTolerance tolerance)
    {
        Dictionary<long, NetworkGas> liveBefore = LiveById(before);
        Dictionary<long, NetworkGas> liveAfter = LiveById(after);
        Dictionary<long, long> pipeBefore = Owners(liveBefore, static network => network.Members);
        Dictionary<long, long> pipeAfter = Owners(liveAfter, static network => network.Members);
        Dictionary<GridCell, long> cellBefore = Owners(liveBefore, static network => network.Cells);
        Dictionary<GridCell, long> cellAfter = Owners(liveAfter, static network => network.Cells);

        FamilyBuilder families = new FamilyBuilder();
        foreach (NetworkGas network in liveBefore.Values)
        {
            if (Changed(network, liveAfter, pipeAfter, tolerance))
            {
                families.Join(network.Id, network.Members, pipeAfter);
                families.Join(network.Id, network.Cells, cellAfter);
            }
        }

        foreach (NetworkGas network in liveAfter.Values)
        {
            if (!liveBefore.ContainsKey(network.Id))
            {
                families.Join(network.Id, network.Members, pipeBefore);
                families.Join(network.Id, network.Cells, cellBefore);
            }
        }

        List<GasFamily> grouped = families.Group(liveBefore, liveAfter, tolerance, TypesOf(before, after));
        List<NetworkGas> ghosts = new List<NetworkGas>();
        List<NetworkGas> oldGhosts = new List<NetworkGas>();
        Dictionary<long, NetworkGas> beforeById = AllById(before);
        foreach (NetworkGas network in after)
        {
            if (network.Live || tolerance.Negligible(network.Gas))
            {
                continue;
            }

            bool wasGhost = beforeById.TryGetValue(network.Id, out NetworkGas old) && !old.Live &&
                            tolerance.Same(old.Gas, network.Gas);
            (wasGhost ? oldGhosts : ghosts).Add(network);
        }

        ghosts.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        oldGhosts.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        List<GasNetworkGhost> located = new List<GasNetworkGhost>(ghosts.Count);
        foreach (NetworkGas ghost in ghosts)
        {
            located.Add(new GasNetworkGhost(ghost, grouped.Find(family => family.Contains(ghost.Id))));
        }

        bool ok = ghosts.Count == 0 && grouped.TrueForAll(static family => family.Emptied || family.Conserved);
        return new GasAudit(grouped, located, oldGhosts, ok);
    }

    // Gone, now a ghost, other contents, or other pipes (a pipe gone, added, or in another network now).
    private static bool Changed(NetworkGas before, Dictionary<long, NetworkGas> liveAfter,
        Dictionary<long, long> pipeAfter, GasTolerance tolerance)
    {
        if (!liveAfter.TryGetValue(before.Id, out NetworkGas now) || !tolerance.Same(before.Gas, now.Gas) ||
            now.Members.Count != before.Members.Count)
        {
            return true;
        }

        foreach (long pipe in before.Members)
        {
            if (!pipeAfter.TryGetValue(pipe, out long owner) || owner != before.Id)
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<long, NetworkGas> LiveById(IReadOnlyList<NetworkGas> networks)
    {
        Dictionary<long, NetworkGas> byId = new Dictionary<long, NetworkGas>(networks.Count);
        foreach (NetworkGas network in networks)
        {
            if (network.Live)
            {
                byId[network.Id] = network;
            }
        }

        return byId;
    }

    private static Dictionary<long, NetworkGas> AllById(IReadOnlyList<NetworkGas> networks)
    {
        Dictionary<long, NetworkGas> byId = new Dictionary<long, NetworkGas>(networks.Count);
        foreach (NetworkGas network in networks)
        {
            byId[network.Id] = network;
        }

        return byId;
    }

    // Which network holds each pipe (or cell).
    private static Dictionary<TKey, long> Owners<TKey>(Dictionary<long, NetworkGas> networks,
        Func<NetworkGas, IReadOnlyList<TKey>> keysOf)
        where TKey : notnull
    {
        Dictionary<TKey, long> owners = new Dictionary<TKey, long>();
        foreach (NetworkGas network in networks.Values)
        {
            foreach (TKey key in keysOf(network))
            {
                owners[key] = network.Id;
            }
        }

        return owners;
    }

    private static int TypesOf(IReadOnlyList<NetworkGas> before, IReadOnlyList<NetworkGas> after) =>
        before.Count > 0 ? before[0].Gas.Types : after.Count > 0 ? after[0].Gas.Types : 0;

    /// <summary>Union-find over network ids, keyed by the lowest id of each group.</summary>
    private sealed class FamilyBuilder
    {
        private readonly Dictionary<long, long> _parent = new Dictionary<long, long>();

        internal void Join<TKey>(long network, IReadOnlyList<TKey> keys, Dictionary<TKey, long> otherSide)
            where TKey : notnull
        {
            Root(network);
            foreach (TKey key in keys)
            {
                if (otherSide.TryGetValue(key, out long other))
                {
                    Union(network, other);
                }
            }
        }

        internal List<GasFamily> Group(Dictionary<long, NetworkGas> liveBefore, Dictionary<long, NetworkGas> liveAfter,
            GasTolerance tolerance, int types)
        {
            Dictionary<long, List<long>> members = new Dictionary<long, List<long>>();
            List<long> ids = new List<long>(_parent.Keys);
            ids.Sort();
            foreach (long id in ids)
            {
                long root = Root(id);
                if (!members.TryGetValue(root, out List<long> list))
                {
                    list = new List<long>();
                    members[root] = list;
                }

                list.Add(id);
            }

            List<GasFamily> families = new List<GasFamily>(members.Count);
            foreach (List<long> group in members.Values)
            {
                families.Add(FamilyOf(group, liveBefore, liveAfter, tolerance, types));
            }

            families.Sort(static (a, b) => Lowest(a).CompareTo(Lowest(b)));
            return families;
        }

        private static GasFamily FamilyOf(List<long> group, Dictionary<long, NetworkGas> liveBefore,
            Dictionary<long, NetworkGas> liveAfter, GasTolerance tolerance, int types)
        {
            List<NetworkGas> before = new List<NetworkGas>();
            List<NetworkGas> after = new List<NetworkGas>();
            GasMix gasBefore = GasMix.Empty(types);
            GasMix gasAfter = GasMix.Empty(types);
            foreach (long id in group)
            {
                if (liveBefore.TryGetValue(id, out NetworkGas then))
                {
                    before.Add(then);
                    gasBefore = gasBefore.Plus(then.Gas);
                }

                if (liveAfter.TryGetValue(id, out NetworkGas now))
                {
                    after.Add(now);
                    gasAfter = gasAfter.Plus(now.Gas);
                }
            }

            return new GasFamily(before, after, gasBefore, gasAfter, tolerance.Same(gasBefore, gasAfter));
        }

        private static long Lowest(GasFamily family)
        {
            long lowest = long.MaxValue;
            foreach (NetworkGas network in family.Before)
            {
                lowest = Math.Min(lowest, network.Id);
            }

            foreach (NetworkGas network in family.After)
            {
                lowest = Math.Min(lowest, network.Id);
            }

            return lowest;
        }

        private long Root(long id)
        {
            if (!_parent.TryGetValue(id, out long parent))
            {
                _parent[id] = id;
                return id;
            }

            if (parent == id)
            {
                return id;
            }

            long root = Root(parent);
            _parent[id] = root;
            return root;
        }

        private void Union(long a, long b)
        {
            long rootA = Root(a);
            long rootB = Root(b);
            if (rootA != rootB)
            {
                _parent[Math.Max(rootA, rootB)] = Math.Min(rootA, rootB);
            }
        }
    }
}

/// <summary>A ghost network and the family whose pipes it once held, when it held any before the job.</summary>
internal sealed class GasNetworkGhost
{
    internal GasNetworkGhost(NetworkGas network, GasFamily? family)
    {
        Network = network;
        Family = family;
    }

    internal NetworkGas Network { get; }

    internal GasFamily? Family { get; }
}

/// <summary>Gas to put into one network to make its family whole again.</summary>
internal sealed class GasRefill
{
    internal GasRefill(long into, GasMix gas)
    {
        Into = into;
        Gas = gas;
    }

    internal long Into { get; }

    internal GasMix Gas { get; }
}

/// <summary>
/// How a family that lost gas is made whole: what it lacks, gas by gas (contents before minus contents after, each
/// gas's positive part), goes into the family's live networks by volume, as the game's own split divides a
/// network's contents (NetworkAtmosphereEvent.Apply). A family that gained gas, or has no live network, gets nothing.
/// </summary>
internal static class GasRefills
{
    internal static List<GasRefill> For(GasAudit audit, GasTolerance tolerance)
    {
        List<GasRefill> refills = new List<GasRefill>();
        foreach (GasFamily family in audit.Families)
        {
            if (family.Conserved || family.Emptied)
            {
                continue;
            }

            GasMix lacking = family.GasBefore.Lacking(family.GasAfter);
            if (tolerance.Negligible(lacking))
            {
                continue;
            }

            double[] volumes = new double[family.After.Count];
            for (int index = 0; index < volumes.Length; index++)
            {
                volumes[index] = family.After[index].VolumeL;
            }

            double[] shares = GasShares.ByVolume(volumes);
            for (int index = 0; index < shares.Length; index++)
            {
                refills.Add(new GasRefill(family.After[index].Id, lacking.Scaled(shares[index])));
            }
        }

        return refills;
    }
}
