#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A member of a pipe network: a pipe piece, an in-line tank or a passive vent, its volume and rating.</summary>
internal sealed class TakedownMember
{
    internal TakedownMember(long id, double volumeL, double? ratingKpa)
    {
        Id = id;
        VolumeL = volumeL;
        RatingKpa = ratingKpa;
    }

    internal long Id { get; }

    internal double VolumeL { get; }

    /// <summary>Its MaxPressure; null for a member no rating applies to.</summary>
    internal double? RatingKpa { get; }
}

/// <summary>One network left once the removal is done: its members, their volume, the gas it holds, its weakest pipe.</summary>
internal sealed class TakedownPart
{
    internal TakedownPart(List<long> members, double volumeL, double moles, double? lowestKpa)
    {
        Members = members;
        VolumeL = volumeL;
        Moles = moles;
        LowestKpa = lowestKpa;
    }

    internal List<long> Members { get; }

    internal double VolumeL { get; }

    internal double Moles { get; }

    internal double? LowestKpa { get; }
}

/// <summary>A pipe network after a removal: the networks left, and the gas the game deleted on the way.</summary>
internal sealed class TakedownOutcome
{
    internal TakedownOutcome(List<TakedownPart> parts, double molesBefore, double removedL)
    {
        Parts = parts;
        MolesBefore = molesBefore;
        RemovedL = removedL;
        double kept = 0.0;
        foreach (TakedownPart part in parts)
        {
            kept += part.Moles;
        }

        LostMol = Math.Max(0.0, molesBefore - kept);
    }

    internal List<TakedownPart> Parts { get; }

    internal double MolesBefore { get; }

    internal double RemovedL { get; }

    /// <summary>Gas no network left holds: deleted with a part's last member, or left in a network without pipes.</summary>
    internal double LostMol { get; }

    internal bool Split => Parts.Count > 1;
}

/// <summary>
/// What removing some members of one pipe network in one job does to it and its gas, in the order the job removes
/// them: the one model remove_structure's would_split, holds_gas and would_burst are read from.
/// <para>
/// Kept whole (every removed member leaves the network before it is destroyed, as the run tools do when the network
/// stays in one piece): the network keeps every mole in what is left.
/// </para>
/// <para>
/// Otherwise the game's Pipe.OnDestroy, member by member in the order destroyed (every member of the job is already
/// marked as being destroyed when the first one goes): the member leaves its network; each neighbour not yet gone
/// starts a new network holding itself and everything reachable from it without passing a member being destroyed
/// (ReferencableNetwork.RebuildNetworkCore); the old network's gas is divided among those new networks by their volume
/// (NetworkAtmosphereEvent.DivideNetworkAtmosphere); with no neighbour left it is divided among nothing and deleted.
/// A network all of whose members a later rebuild takes over keeps the share it was given, with no pipe left to hold
/// it: that gas is lost too.
/// </para>
/// </summary>
internal static class PipeTakedown
{
    internal static TakedownOutcome Run(IReadOnlyList<TakedownMember> members, IEnumerable<Link> links,
        IReadOnlyList<long> order, bool keptWhole, double moles)
    {
        Dictionary<long, TakedownMember> byId = new Dictionary<long, TakedownMember>();
        foreach (TakedownMember member in members)
        {
            byId[member.Id] = member;
        }

        HashSet<long> removed = new HashSet<long>();
        List<long> sequence = new List<long>();
        double removedL = 0.0;
        foreach (long id in order)
        {
            if (byId.ContainsKey(id) && removed.Add(id))
            {
                sequence.Add(id);
                removedL += byId[id].VolumeL;
            }
        }

        if (keptWhole)
        {
            List<long> left = new List<long>();
            foreach (TakedownMember member in members)
            {
                if (!removed.Contains(member.Id))
                {
                    left.Add(member.Id);
                }
            }

            List<TakedownPart> whole = left.Count > 0
                ? new List<TakedownPart> { PartOf(left, byId, moles) }
                : new List<TakedownPart>();
            return new TakedownOutcome(whole, moles, removedL);
        }

        Dictionary<long, SortedSet<long>> neighbours = Neighbours(byId, links);
        Chain chain = new Chain(byId, neighbours, removed, moles);
        foreach (long id in sequence)
        {
            chain.Destroy(id);
        }

        return new TakedownOutcome(chain.Parts(), moles, removedL);
    }

    private static TakedownPart PartOf(List<long> ids, Dictionary<long, TakedownMember> byId, double moles)
    {
        double volume = 0.0;
        double? lowest = null;
        foreach (long id in ids)
        {
            TakedownMember member = byId[id];
            volume += member.VolumeL;
            if (member.RatingKpa.HasValue)
            {
                lowest = lowest.HasValue ? Math.Min(lowest.Value, member.RatingKpa.Value) : member.RatingKpa;
            }
        }

        ids.Sort();
        return new TakedownPart(ids, volume, moles, lowest);
    }

    private static Dictionary<long, SortedSet<long>> Neighbours(Dictionary<long, TakedownMember> byId,
        IEnumerable<Link> links)
    {
        Dictionary<long, SortedSet<long>> neighbours = new Dictionary<long, SortedSet<long>>();
        foreach (long id in byId.Keys)
        {
            neighbours[id] = new SortedSet<long>();
        }

        foreach (Link link in links)
        {
            if (link.From != link.To && byId.ContainsKey(link.From) && byId.ContainsKey(link.To))
            {
                neighbours[link.From].Add(link.To);
                neighbours[link.To].Add(link.From);
            }
        }

        return neighbours;
    }

    private sealed class Network
    {
        internal HashSet<long> Members { get; } = new HashSet<long>();

        internal double Moles { get; set; }
    }

    private sealed class Chain
    {
        private readonly Dictionary<long, TakedownMember> _byId;
        private readonly Dictionary<long, SortedSet<long>> _neighbours;
        private readonly HashSet<long> _removed;
        private readonly HashSet<long> _gone = new HashSet<long>();
        private readonly Dictionary<long, Network> _networkOf = new Dictionary<long, Network>();
        private readonly List<Network> _networks = new List<Network>();

        internal Chain(Dictionary<long, TakedownMember> byId, Dictionary<long, SortedSet<long>> neighbours,
            HashSet<long> removed, double moles)
        {
            _byId = byId;
            _neighbours = neighbours;
            _removed = removed;
            Network first = new Network { Moles = moles };
            _networks.Add(first);
            foreach (long id in byId.Keys)
            {
                first.Members.Add(id);
                _networkOf[id] = first;
            }
        }

        internal void Destroy(long id)
        {
            Network old = _networkOf[id];
            double gas = old.Moles;
            old.Members.Remove(id);
            _networkOf.Remove(id);
            _gone.Add(id);

            List<Network> into = new List<Network>();
            foreach (long neighbour in _neighbours[id])
            {
                if (!_gone.Contains(neighbour))
                {
                    into.Add(Rebuild(neighbour));
                }
            }

            double total = 0.0;
            foreach (Network network in into)
            {
                total += VolumeOf(network);
            }

            // Shares by the volume each new network has once every neighbour is rebuilt; none left: deleted.
            foreach (Network network in into)
            {
                network.Moles = total > 0.0 ? gas * VolumeOf(network) / total : 0.0;
            }
        }

        internal List<TakedownPart> Parts()
        {
            List<TakedownPart> parts = new List<TakedownPart>();
            foreach (Network network in _networks)
            {
                if (network.Members.Count > 0)
                {
                    parts.Add(PartOf(new List<long>(network.Members), _byId, network.Moles));
                }
            }

            parts.Sort(static (a, b) => a.Members[0].CompareTo(b.Members[0]));
            return parts;
        }

        // The origin joins whatever it is; the search from it passes nothing being destroyed.
        private Network Rebuild(long origin)
        {
            Network network = new Network();
            _networks.Add(network);
            Move(origin, network);
            HashSet<long> seen = new HashSet<long> { origin };
            Queue<long> queue = new Queue<long>(_neighbours[origin]);
            while (queue.Count > 0)
            {
                long next = queue.Dequeue();
                if (_removed.Contains(next) || !seen.Add(next))
                {
                    continue;
                }

                Move(next, network);
                foreach (long beyond in _neighbours[next])
                {
                    queue.Enqueue(beyond);
                }
            }

            return network;
        }

        private void Move(long id, Network into)
        {
            if (_networkOf.TryGetValue(id, out Network? from))
            {
                from!.Members.Remove(id);
            }

            into.Members.Add(id);
            _networkOf[id] = into;
        }

        private double VolumeOf(Network network)
        {
            double volume = 0.0;
            foreach (long id in network.Members)
            {
                volume += _byId[id].VolumeL;
            }

            return volume;
        }
    }
}
