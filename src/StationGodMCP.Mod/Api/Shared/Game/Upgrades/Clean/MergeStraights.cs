#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// merge_straights: runs of single straights of one kit, colour and owner in one line become the fewest long straights
/// the kit offers that exactly cover them, longest first (StraightRuns); leftover singles stay. A single with a device
/// mounted on it breaks the run. The kit's long lengths are read from its loaded pieces: every piece LongStraights
/// can split is a long straight of that many cells. Each long piece is found by the twin search at one tip of its
/// run, starting from the first single's rotation.
/// </summary>
internal sealed class MergeStraights : ICleanOperation
{
    internal const string Operation = "merge_straights";

    private readonly Dictionary<int, List<int>> _lengths = new Dictionary<int, List<int>>();

    public string Name => CleanOperationSet.MergeStraights;

    public void Plan(CleanPass pass)
    {
        Dictionary<Kit, List<PieceModel>> singles = new Dictionary<Kit, List<PieceModel>>();
        Dictionary<long, int> keys = new Dictionary<long, int>();
        Dictionary<long, SmallGrid> pieces = new Dictionary<long, SmallGrid>();
        foreach (SmallGrid piece in pass.Open)
        {
            PieceModel live = pass.LiveOf(piece);
            Kit? kit = StraightRuns.IsSingleStraight(live) && !pass.HasMountedDevice(live)
                ? pass.Context.KitFor(piece, false, true)
                : null;
            if (kit == null)
            {
                continue;
            }

            if (!singles.TryGetValue(kit, out List<PieceModel> list))
            {
                list = new List<PieceModel>();
                singles[kit] = list;
            }

            list.Add(live);
            keys[piece.ReferenceId] = KeyOf(piece, kit);
            pieces[piece.ReferenceId] = piece;
        }

        foreach (KeyValuePair<Kit, List<PieceModel>> group in singles)
        {
            foreach (StraightSegment segment in StraightRuns.Plan(group.Value, keys, LengthsOf(group.Key)))
            {
                Merge(pass, segment, group.Key, pieces);
            }
        }
    }

    private static void Merge(CleanPass pass, StraightSegment segment, Kit kit, Dictionary<long, SmallGrid> pieces)
    {
        List<SmallGrid> olds = segment.Singles.ConvertAll(single => pieces[single.Id]);
        Twin? twin = FindLong(pass, segment, kit, olds[0].ThingTransformRotation);
        if (twin == null)
        {
            pass.Plan.Unmatched.Add(new SkippedPiece(olds[0], "no_long_piece", PlanContext.NoTwinMessage(olds[0], kit,
                $"a {segment.Singles.Count}-long straight over the run starting at")));
            olds.ForEach(pass.Claim);
            return;
        }

        PieceModel first = pass.LiveOf(olds[0]);
        pass.Replace(olds, kit, new List<Twin> { twin }, CleanPass.Detail(Operation, first, pass.ConnectedEnds(first)));
    }

    // A long piece's pivot sits in one of its end cells; either tip is tried.
    private static Twin? FindLong(CleanPass pass, StraightSegment segment, Kit kit, Quaternion rotation)
    {
        IReadOnlyList<GridCell> cells = segment.Long.Cells;
        int key = unchecked(kit.Item.PrefabHash * 31 + cells.Count);
        return pass.PlacedTwins.FindAt(key, PieceShapes.CentreOf(cells[0]), rotation, segment.Long, kit) ??
               pass.PlacedTwins.FindAt(key, PieceShapes.CentreOf(cells[cells.Count - 1]), rotation, segment.Long,
                   kit);
    }

    // The kit's long straights, by cell count, from its loaded pieces placed anywhere.
    private List<int> LengthsOf(Kit kit)
    {
        if (_lengths.TryGetValue(kit.Item.PrefabHash, out List<int> known))
        {
            return known;
        }

        List<int> lengths = new List<int>();
        foreach (Structure piece in kit.Pieces)
        {
            PieceModel? model = PieceShapes.Placed(piece, Vector3.zero, Quaternion.identity, 0);
            if (model != null && LongStraights.Split(model) != null && !lengths.Contains(model.Cells.Count))
            {
                lengths.Add(model.Cells.Count);
            }
        }

        _lengths[kit.Item.PrefabHash] = lengths;
        return lengths;
    }

    // Singles merge only with singles of the same prefab, kit, colour and owner: the long piece takes the first
    // single's colour and owner (CreateStructureInstance).
    private static int KeyOf(SmallGrid piece, Kit kit)
    {
        int colour = piece.CustomColor != null ? piece.CustomColor.Index : -1;
        return unchecked(((kit.Item.PrefabHash * 31 + piece.PrefabHash) * 31 + colour) * 31 +
                         piece.OwnerClientId.GetHashCode());
    }
}
