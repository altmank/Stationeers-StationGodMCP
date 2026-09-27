#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// remove_loops: pieces that join parts of a network already joined another way are removed (LoopCutting): each loop
/// loses the shortest run of plain two-ended pieces whose ends stay joined without it, again until it has no cycle.
/// A run holding a piece linked to a device, a piece with a device mounted, an indestructible or rocket piece, or one
/// no coil or kit places never goes; a loop holding a keep_ids piece is spared whole. The junctions a cut leaves with
/// an open end become the piece with only their connected ends (as simplify_junctions does). Never by default: a loop
/// may be redundancy kept on purpose against a burnt cable. A removed piece gives back what deconstructing it would.
/// </summary>
internal sealed class RemoveLoops : ICleanOperation
{
    internal const string Operation = "remove_loop";

    private readonly HashSet<long> _keep;

    internal RemoveLoops(HashSet<long> keep)
    {
        _keep = keep;
    }

    public string Name => CleanOperationSet.RemoveLoops;

    public void Plan(CleanPass pass)
    {
        Dictionary<long, SmallGrid> byId = new Dictionary<long, SmallGrid>();
        Dictionary<long, Kit> kits = new Dictionary<long, Kit>();
        Dictionary<long, string> blocked = new Dictionary<long, string>();
        List<PieceModel> candidates = new List<PieceModel>();
        foreach (SmallGrid piece in pass.Open)
        {
            byId[piece.ReferenceId] = piece;
            candidates.Add(pass.LiveOf(piece));
            RemoveDeadEnds.Block(pass, piece, kits, blocked);
        }

        List<PieceModel> others = new List<PieceModel>();
        HashSet<long> devices = new HashSet<long>();
        Dictionary<long, SmallGrid> around = LinkSurvey.Neighbourhood(candidates);
        Dictionary<long, SmallGrid> outside = new Dictionary<long, SmallGrid>();
        foreach (SmallGrid thing in around.Values)
        {
            if (byId.ContainsKey(thing.ReferenceId))
            {
                continue;
            }

            outside[thing.ReferenceId] = thing;
            if (thing is Device && !pass.Family.IsMember(thing))
            {
                devices.Add(thing.ReferenceId);
            }
        }

        others.AddRange(pass.ModelsOf(outside, 0));
        LoopResult result = LoopCutting.Find(candidates, others, devices, blocked, _keep);
        pass.Plan.Loops = new List<LoopRecord>();
        foreach (NetworkLoop loop in result.Loops)
        {
            pass.Plan.Loops.Add(new LoopRecord(loop, Things(loop.Pieces, byId, around)));
        }

        HashSet<long> cut = new HashSet<long>(result.Cut);
        foreach (long id in result.Cut)
        {
            SmallGrid piece = byId[id];
            PieceModel live = pass.LiveOf(piece);
            pass.Remove(piece, kits[id], CleanPass.Detail(Operation, live, pass.ConnectedEnds(live)));
        }

        foreach (SmallGrid anchor in Anchors(pass, candidates, cut, byId))
        {
            SimplifyJunctions.ShrinkIfOpenEnded(pass, anchor);
        }
    }

    // Open pieces that were linked to a cut piece and stay.
    private static List<SmallGrid> Anchors(CleanPass pass, List<PieceModel> candidates, HashSet<long> cut,
        Dictionary<long, SmallGrid> byId)
    {
        List<SmallGrid> anchors = new List<SmallGrid>();
        if (cut.Count == 0)
        {
            return anchors;
        }

        List<PieceModel> removed = candidates.FindAll(model => cut.Contains(model.Id));
        foreach (PieceModel model in candidates)
        {
            if (cut.Contains(model.Id))
            {
                continue;
            }

            bool linked = removed.Exists(gone => Connectivity.Links(model, gone) || Connectivity.Links(gone, model));
            if (linked && pass.Open.Contains(byId[model.Id]))
            {
                anchors.Add(byId[model.Id]);
            }
        }

        return anchors;
    }

    private static List<SmallGrid> Things(List<long> ids, Dictionary<long, SmallGrid> byId,
        Dictionary<long, SmallGrid> around)
    {
        List<SmallGrid> things = new List<SmallGrid>(ids.Count);
        foreach (long id in ids)
        {
            if (byId.TryGetValue(id, out SmallGrid piece) || around.TryGetValue(id, out piece))
            {
                things.Add(piece);
            }
        }

        return things;
    }
}

/// <summary>A loop as the clean report lists it: the loop and its pieces as things.</summary>
internal sealed class LoopRecord
{
    internal LoopRecord(NetworkLoop loop, List<SmallGrid> pieces)
    {
        Loop = loop;
        Pieces = pieces;
    }

    internal NetworkLoop Loop { get; }

    internal List<SmallGrid> Pieces { get; }
}
