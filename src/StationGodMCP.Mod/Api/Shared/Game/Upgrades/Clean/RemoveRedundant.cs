#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>What remove_redundant takes from the request: the candidate filters and the root.</summary>
internal sealed class RedundancyOptions
{
    internal RedundancyOptions(HashSet<long>? only, long? olderThan, long? root)
    {
        Only = only;
        OlderThan = olderThan;
        Root = root;
    }

    internal static RedundancyOptions None => new RedundancyOptions(null, null, null);

    /// <summary>only_ids: the only pieces that may go; null for every selected piece.</summary>
    internal HashSet<long>? Only { get; }

    /// <summary>older_than_id: only pieces with a lower reference id (built before that one) may go.</summary>
    internal long? OlderThan { get; }

    /// <summary>root: the device the network is fed from; null for every supplier on it.</summary>
    internal long? Root { get; }
}

/// <summary>
/// remove_redundant: every selected piece that no device needs is removed (RedundantPieces): the network is read
/// whole (every piece of each selected piece's network, and its members that are not pieces: an in-line tank or a
/// passive vent is part of the network and stays joined to it) with the devices on it, and a candidate goes when every
/// remaining piece stays joined to the rest without it, oldest first. For pipes, removals that would take the last
/// pipes of a network still holding gas or liquid are held back (holds_contents: the game's removal of a network's
/// last pipe deletes its contents), as remove_dead_ends holds them. Candidates are the selected pieces, narrowed by
/// only_ids and older_than_id; keep_ids never go, nor a piece joined to a device port, one with a device mounted, an
/// indestructible or rocket piece, or one no coil or kit places. This finds what remove_loops cannot: a second path
/// that meets the first at a device's port piece (an old and a new feed), since devices never join a network and
/// such a loop is plain cable here. The junctions a removal leaves with an open end become the piece with only their
/// connected ends. Every candidate that stays is reported with why, and for needed, the devices it keeps on the root.
/// </summary>
internal sealed class RemoveRedundant : ICleanOperation
{
    internal const string Operation = "remove_redundant";

    private readonly HashSet<long> _keep;
    private readonly RedundancyOptions _options;

    internal RemoveRedundant(HashSet<long> keep, RedundancyOptions options)
    {
        _keep = keep;
        _options = options;
    }

    public string Name => CleanOperationSet.RemoveRedundant;

    private const int MaximumPasses = 4;

    public void Plan(CleanPass pass)
    {
        Dictionary<long, SmallGrid> pieces = WholeNetworks(pass);
        List<PieceModel> models = new List<PieceModel>(pieces.Count);
        foreach (SmallGrid piece in pieces.Values)
        {
            models.Add(pass.IsSelected(piece.ReferenceId) ? pass.LiveOf(piece) : PieceShapes.Live(piece));
        }

        Dictionary<long, SmallGrid> devices = Devices(pass, models, pieces);
        List<PieceModel> all = new List<PieceModel>(models);
        HashSet<long> ids = new HashSet<long>(pieces.Keys);
        foreach (SmallGrid device in devices.Values)
        {
            all.Add(PieceShapes.Live(device));
            ids.Add(device.ReferenceId);
        }

        HashSet<Link> links = Connectivity.LinksTouching(all, ids);
        Dictionary<long, Kit> kits = new Dictionary<long, Kit>();
        Dictionary<long, string> blocked = new Dictionary<long, string>();
        List<long> candidates = Candidates(pass, kits, blocked);
        HashSet<long> roots = Roots(pass, pieces);
        RedundancyResult result = Hold(pass, pieces, blocked, () => RedundantPieces.Find(pieces.Keys, links,
            new HashSet<long>(devices.Keys), candidates, _keep, blocked, roots));

        HashSet<long> removed = new HashSet<long>(result.Removed);
        foreach (long id in result.Removed)
        {
            SmallGrid piece = pieces[id];
            PieceModel live = pass.LiveOf(piece);
            pass.Remove(piece, kits[id], CleanPass.Detail(Operation, live, pass.ConnectedEnds(live)));
        }

        foreach (SmallGrid anchor in Anchors(pass, models, removed, pieces))
        {
            SimplifyJunctions.ShrinkIfOpenEnded(pass, anchor);
        }

        Dictionary<long, SmallGrid> named = new Dictionary<long, SmallGrid>(pieces);
        foreach (KeyValuePair<long, SmallGrid> device in devices)
        {
            named[device.Key] = device.Value;
        }

        pass.Plan.Redundancy = new RedundancyRecord(result, named, new List<long>(roots), candidates.Count);
    }

    // Finds, then holds back every removal that, with what earlier operations remove, would empty a network that
    // still holds contents (UpgradeFamily.RemovalHolds), and finds again without them.
    private static RedundancyResult Hold(CleanPass pass, Dictionary<long, SmallGrid> pieces,
        Dictionary<long, string> blocked, System.Func<RedundancyResult> find)
    {
        RedundancyResult result = find();
        for (int attempt = 1; attempt < MaximumPasses; attempt++)
        {
            List<SmallGrid> removed = pass.RemovedPieces;
            removed.AddRange(result.Removed.ConvertAll(id => pieces[id]));
            bool held = false;
            foreach (KeyValuePair<long, string> hold in pass.Family.RemovalHolds(removed))
            {
                if (pieces.ContainsKey(hold.Key) && !pass.IsRemoved(hold.Key))
                {
                    blocked[hold.Key] = UpgradeFamily.HoldsContents;
                    held = true;
                }
            }

            if (!held)
            {
                break;
            }

            result = find();
        }

        return result;
    }

    // Every piece of each network a selected piece is on (the connectivity check needs the whole network), with the
    // network's members that are not pieces (never candidates, but part of the network), less what an earlier
    // operation removes.
    private static Dictionary<long, SmallGrid> WholeNetworks(CleanPass pass)
    {
        Dictionary<long, SmallGrid> pieces = new Dictionary<long, SmallGrid>();
        HashSet<long> networks = new HashSet<long>();
        foreach (SmallGrid piece in pass.Pieces)
        {
            IReferencable? network = pass.Family.NetworkOf(piece);
            if (network != null && networks.Add(network.ReferenceId))
            {
                foreach (SmallGrid member in pass.Family.NetworkMembers(new ThingId(network.ReferenceId)))
                {
                    if (pass.Family.IsMember(member) && !member.IsBeingDestroyed)
                    {
                        pieces[member.ReferenceId] = member;
                    }
                }
            }

            pieces[piece.ReferenceId] = piece;
        }

        foreach (long id in new List<long>(pieces.Keys))
        {
            if (pass.IsRemoved(id))
            {
                pieces.Remove(id);
            }
        }

        return pieces;
    }

    private static Dictionary<long, SmallGrid> Devices(CleanPass pass, List<PieceModel> models,
        Dictionary<long, SmallGrid> pieces)
    {
        Dictionary<long, SmallGrid> devices = new Dictionary<long, SmallGrid>();
        foreach (SmallGrid thing in LinkSurvey.Neighbourhood(models).Values)
        {
            if (thing is Device && !pieces.ContainsKey(thing.ReferenceId) && !pass.Family.IsMember(thing))
            {
                devices[thing.ReferenceId] = thing;
            }
        }

        return devices;
    }

    // The open selected pieces the filters let through, oldest (lowest id) first; kits and blocks read for each.
    private List<long> Candidates(CleanPass pass, Dictionary<long, Kit> kits, Dictionary<long, string> blocked)
    {
        List<long> candidates = new List<long>();
        foreach (SmallGrid piece in pass.Open)
        {
            long id = piece.ReferenceId;
            if ((_options.Only != null && !_options.Only.Contains(id)) ||
                (_options.OlderThan.HasValue && id >= _options.OlderThan.Value))
            {
                continue;
            }

            RemoveDeadEnds.Block(pass, piece, kits, blocked);
            candidates.Add(id);
        }

        candidates.Sort();
        return candidates;
    }

    private HashSet<long> Roots(CleanPass pass, Dictionary<long, SmallGrid> pieces)
    {
        List<IReferencable> networks = new List<IReferencable>();
        foreach (SmallGrid piece in pieces.Values)
        {
            IReferencable? network = pass.Family.NetworkOf(piece);
            if (network != null && !networks.Contains(network))
            {
                networks.Add(network);
            }
        }

        if (_options.Root.HasValue)
        {
            NetworkRoots.RequireOn(new ThingId(_options.Root.Value), pass.Family,
                networks.ConvertAll(static network => network.ReferenceId));
            return new HashSet<long> { _options.Root.Value };
        }

        return NetworkRoots.Suppliers(networks);
    }

    // Open pieces that were linked to a removed piece and stay.
    private static List<SmallGrid> Anchors(CleanPass pass, List<PieceModel> models, HashSet<long> removed,
        Dictionary<long, SmallGrid> pieces)
    {
        List<SmallGrid> anchors = new List<SmallGrid>();
        if (removed.Count == 0)
        {
            return anchors;
        }

        List<PieceModel> gone = models.FindAll(model => removed.Contains(model.Id));
        List<SmallGrid> open = pass.Open;
        foreach (PieceModel model in models)
        {
            if (removed.Contains(model.Id) || !pass.IsSelected(model.Id))
            {
                continue;
            }

            bool linked = gone.Exists(other => Connectivity.Links(model, other) || Connectivity.Links(other, model));
            if (linked && open.Contains(pieces[model.Id]))
            {
                anchors.Add(pieces[model.Id]);
            }
        }

        return anchors;
    }
}

/// <summary>What remove_redundant decided, with every piece and device it names, for the report.</summary>
internal sealed class RedundancyRecord
{
    internal RedundancyRecord(RedundancyResult result, Dictionary<long, SmallGrid> things, List<long> roots,
        int candidates)
    {
        Result = result;
        Things = things;
        Roots = roots;
        Candidates = candidates;
    }

    internal RedundancyResult Result { get; }

    internal Dictionary<long, SmallGrid> Things { get; }

    internal List<long> Roots { get; }

    internal int Candidates { get; }
}
