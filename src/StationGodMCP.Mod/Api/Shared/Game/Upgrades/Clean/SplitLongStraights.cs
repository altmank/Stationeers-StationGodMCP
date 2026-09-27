#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// split_long_straights: a 3-, 5- or 10-long straight becomes one single straight of its grade's coil or kit per cell
/// it covers, in the same line (LongStraights). Each single is found by the twin search at its cell's centre, starting
/// from the long piece's rotation.
/// </summary>
internal sealed class SplitLongStraights : ICleanOperation
{
    internal const string Operation = "split_long_straight";

    public string Name => CleanOperationSet.SplitLongStraights;

    public void Plan(CleanPass pass)
    {
        foreach (SmallGrid piece in pass.Open)
        {
            PieceModel live = pass.LiveOf(piece);
            List<PieceModel>? singles = LongStraights.Split(live);
            if (singles != null)
            {
                Split(pass, piece, live, singles);
            }
        }
    }

    private static void Split(CleanPass pass, SmallGrid piece, PieceModel live, List<PieceModel> singles)
    {
        Kit? kit = pass.Context.KitFor(piece, false);
        if (kit == null)
        {
            pass.Claim(piece);
            return;
        }

        List<Twin> parts = new List<Twin>(singles.Count);
        foreach (PieceModel single in singles)
        {
            Twin? part = pass.PlacedTwins.FindAt(piece.PrefabHash, PieceShapes.CentreOf(single.Cells[0]),
                piece.ThingTransformRotation, single, kit);
            if (part == null)
            {
                pass.Plan.Unmatched.Add(new SkippedPiece(piece, "no_single_piece", PlanContext.NoTwinMessage(piece,
                    kit, $"a one-cell straight for cell {single.Cells[0]} in the line of")));
                pass.Claim(piece);
                return;
            }

            parts.Add(part);
        }

        pass.Replace(new List<SmallGrid> { piece }, kit, parts,
            CleanPass.Detail(Operation, live, pass.ConnectedEnds(live)));
    }
}
