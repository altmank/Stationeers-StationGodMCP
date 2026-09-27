#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// simplify_junctions: a piece with open ends (nothing connected to them) and two or more connected ends becomes the
/// piece of its own coil or kit with the same cells and only the connected ends, turned as needed: a 3-way junction
/// joining two opposite neighbours becomes a straight, two adjacent ones a corner; a 4-way with three a T, and so on
/// for every piece the coil or kit places (EndCleanup). Pieces an earlier operation removes count as gone.
/// </summary>
internal sealed class SimplifyJunctions : ICleanOperation
{
    internal const string Operation = "simplify_junction";

    public string Name => CleanOperationSet.SimplifyJunctions;

    public void Plan(CleanPass pass)
    {
        foreach (SmallGrid piece in pass.Open)
        {
            ShrinkIfOpenEnded(pass, piece);
        }
    }

    /// <summary>Replaces the piece by its own kit's piece with only its connected ends, when it has open ends.</summary>
    internal static void ShrinkIfOpenEnded(CleanPass pass, SmallGrid piece)
    {
        PieceModel live = pass.LiveOf(piece);
        List<PieceEnd> connected = pass.ConnectedEnds(live);
        if (EndCleanup.Of(live.Ends.Count, connected.Count) == EndUse.Shrink)
        {
            Shrink(pass, piece, live, connected);
        }
    }

    private static void Shrink(CleanPass pass, SmallGrid piece, PieceModel live, List<PieceEnd> connected)
    {
        Kit? kit = pass.Context.KitFor(piece, true);
        if (kit == null)
        {
            pass.Claim(piece);
            return;
        }

        Twin? twin = pass.Context.Twins.Find(piece, EndCleanup.WithEnds(live, connected), kit);
        if (twin == null)
        {
            string ends = string.Join(", ", EndCleanup.DirectionsOf(connected));
            pass.Plan.Unmatched.Add(new SkippedPiece(piece, "no_smaller_piece", PlanContext.NoTwinMessage(piece, kit,
                $"the same cells and only the connected ends ({ends}) of")));
            pass.Claim(piece);
            return;
        }

        pass.Replace(new List<SmallGrid> { piece }, kit, new List<Twin> { twin },
            CleanPass.Detail(Operation, live, connected));
    }
}
