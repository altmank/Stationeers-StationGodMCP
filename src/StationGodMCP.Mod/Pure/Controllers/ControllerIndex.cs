#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Pure.Controllers;

/// <summary>
/// The controllers of one data network for one call: its data devices (the only ones reached through it) and its
/// sources. The first lookups match each device straight against every source (DeviceMatch: a pass over the writes,
/// nothing kept); once a call has asked IndexAfter devices of the network, the network is indexed (ControllerIndex)
/// and the rest are dictionary reads. On a network of 707 devices with 150 chips of 30 lines (net8, steady state) a
/// direct match costs about 10 to 20 us and building the index about 100 us, a lookup on it a few us; so a write or a
/// short batch never pays for an index and a network-wide query pays for it once.
/// </summary>
internal sealed class NetworkControllers
{
    /// <summary>Lookups on one network answered by direct matching before it is indexed.</summary>
    internal const int IndexAfter = 8;

    private readonly HashSet<long> _members;
    private readonly List<IControllerSource> _sources;
    private int _lookups;
    private ControllerIndex? _index;

    /// <param name="members">The network's data devices.</param>
    internal NetworkControllers(HashSet<long> members, List<IControllerSource> sources)
    {
        _members = members;
        _sources = sources;
    }

    /// <summary>Whether the network has been indexed (after IndexAfter lookups).</summary>
    internal bool Indexed => _index != null;

    /// <summary>
    /// Adds the controllers that write the device through this network, unless it is not on it. One program writing
    /// the same way twice is listed once, at its first line; so is one controller met again through another network.
    /// </summary>
    internal void AddFor(ControlledDevice device, List<Controller> into)
    {
        if (!_members.Contains(device.ReferenceId))
        {
            return;
        }

        if (_index == null && ++_lookups < IndexAfter)
        {
            DeviceMatch match = new DeviceMatch(device, new ControllerCollector(into));
            foreach (IControllerSource source in _sources)
            {
                source.Enter(match);
            }

            return;
        }

        _index ??= ControllerIndex.Of(_sources);
        _index.AddFor(device, into);
    }
}

/// <summary>
/// The writes of a network's sources indexed by the device they reach: by reference id (a pin, an id, a wired
/// writer's output) and by prefab hash (a batch write, with the name hash it also needs, if any). It holds entries
/// only; a Controller is made when a lookup matches one.
/// </summary>
internal sealed class ControllerIndex : IControllerSink
{
    private readonly Dictionary<long, List<Entry>> _byTarget = new Dictionary<long, List<Entry>>();
    private readonly Dictionary<int, List<Entry>> _byPrefab = new Dictionary<int, List<Entry>>();

    internal static ControllerIndex Of(List<IControllerSource> sources)
    {
        ControllerIndex index = new ControllerIndex();
        foreach (IControllerSource source in sources)
        {
            source.Enter(index);
        }

        return index;
    }

    public void Targets(long referenceId, IControllerSource source, ChipOperation? op, string? via) =>
        ListOf(_byTarget, referenceId).Add(new Entry(source, op, via, null));

    public void Batches(int prefabHash, int? nameHash, IControllerSource source, ChipOperation? op, string? via) =>
        ListOf(_byPrefab, prefabHash).Add(new Entry(source, op, via, nameHash));

    /// <summary>Adds the controllers of the entries for this device (its id, its prefab with its name hash).</summary>
    internal void AddFor(ControlledDevice device, List<Controller> into)
    {
        ControllerCollector collector = new ControllerCollector(into);
        if (_byTarget.TryGetValue(device.ReferenceId, out List<Entry> targeted))
        {
            foreach (Entry entry in targeted)
            {
                collector.Add(entry.Source, entry.Op, entry.Via);
            }
        }

        if (_byPrefab.TryGetValue(device.PrefabHash, out List<Entry> batches))
        {
            foreach (Entry entry in batches)
            {
                if (!entry.NameHash.HasValue || entry.NameHash.Value == device.NameHash)
                {
                    collector.Add(entry.Source, entry.Op, entry.Via);
                }
            }
        }
    }

    private static List<Entry> ListOf<TKey>(Dictionary<TKey, List<Entry>> lists, TKey key)
        where TKey : notnull
    {
        if (!lists.TryGetValue(key, out List<Entry> found))
        {
            lists[key] = found = new List<Entry>(1);
        }

        return found;
    }

    private readonly struct Entry
    {
        internal Entry(IControllerSource source, ChipOperation? op, string? via, int? nameHash)
        {
            Source = source;
            Op = op;
            Via = via;
            NameHash = nameHash;
        }

        internal IControllerSource Source { get; }

        internal ChipOperation? Op { get; }

        internal string? Via { get; }

        /// <summary>The name hash a batch also needs (sbn); null when any name matches.</summary>
        internal int? NameHash { get; }
    }
}

/// <summary>The match for one device: each write a source names that reaches it is collected at once.</summary>
internal sealed class DeviceMatch : IControllerSink
{
    private readonly ControlledDevice _device;
    private readonly ControllerCollector _collector;

    internal DeviceMatch(ControlledDevice device, ControllerCollector collector)
    {
        _device = device;
        _collector = collector;
    }

    public void Targets(long referenceId, IControllerSource source, ChipOperation? op, string? via)
    {
        if (referenceId == _device.ReferenceId)
        {
            _collector.Add(source, op, via);
        }
    }

    public void Batches(int prefabHash, int? nameHash, IControllerSource source, ChipOperation? op, string? via)
    {
        if (prefabHash == _device.PrefabHash && (!nameHash.HasValue || nameHash.Value == _device.NameHash))
        {
            _collector.Add(source, op, via);
        }
    }
}

/// <summary>
/// Adds matched writes to a device's controller list, each once. A source's writes arrive together, so a repeat is
/// looked for among that source's controllers only, or among all of them when the list already held another
/// network's controllers.
/// </summary>
internal sealed class ControllerCollector
{
    private readonly List<Controller> _into;
    private readonly bool _merging;
    private IControllerSource? _run;
    private int _runStart;

    internal ControllerCollector(List<Controller> into)
    {
        _into = into;
        _merging = into.Count > 0;
        _runStart = into.Count;
    }

    internal void Add(IControllerSource source, ChipOperation? op, string? via)
    {
        if (!ReferenceEquals(source, _run))
        {
            _run = source;
            _runStart = _into.Count;
        }

        Controller controller = source.Describe(op, via);
        for (int index = _merging ? 0 : _runStart; index < _into.Count; index++)
        {
            if (_into[index].SameAs(controller))
            {
                return;
            }
        }

        _into.Add(controller);
    }
}
