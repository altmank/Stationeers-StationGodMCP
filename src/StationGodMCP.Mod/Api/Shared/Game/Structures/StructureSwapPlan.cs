#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Structures;

/// <summary>One replace_walls or replace_frames request as parsed: the family and the arguments.</summary>
internal sealed class StructureSwapRequest
{
    internal StructureSwapRequest(StructureFamily family, StructureSwapArguments arguments)
    {
        Family = family;
        Arguments = arguments;
    }

    internal StructureFamily Family { get; }

    internal StructureSwapArguments Arguments { get; }

    /// <summary>The report's target: the named prefab, or own_prefab.</summary>
    internal string TargetLabel => Arguments.To ?? "own_prefab";
}

/// <summary>A face a wall blocks: its two cells, their pressures and the stress verdict with the new wall.</summary>
internal sealed class WallFace
{
    internal WallFace(GridPoint face, GridPoint a, GridPoint b, StructureFacePressure pressure, string verdict)
    {
        Face = face;
        A = a;
        B = b;
        Pressure = pressure;
        Verdict = verdict;
    }

    internal GridPoint Face { get; }

    internal GridPoint A { get; }

    internal GridPoint B { get; }

    internal StructureFacePressure Pressure { get; }

    /// <summary>ok, stressed, overstressed or shielded.</summary>
    internal string Verdict { get; }
}

/// <summary>A face beside a frame and the face structures (walls) registered on it, recorded before the swap.</summary>
internal sealed class GuardedFace
{
    internal GuardedFace(GridPoint face, HashSet<long> holders)
    {
        Face = face;
        Holders = holders;
    }

    internal GridPoint Face { get; }

    internal HashSet<long> Holders { get; }
}

/// <summary>
/// One piece's swap: the old piece, the registered target prefab, the slots both take, what each blocks, the
/// materials, and what the family's checks found.
/// </summary>
internal sealed class PlannedStructureSwap
{
    internal PlannedStructureSwap(Structure old, Structure target, List<StructureSlot> slots,
        PieceBlocking before, PieceBlocking after)
    {
        Old = old;
        OldId = old.ReferenceId;
        Target = target;
        Slots = slots;
        Before = before;
        After = after;
        Change = AirRule.Judge(before, after);
        FinalState = target.BuildStates.Count - 1;
    }

    internal Structure Old { get; }

    internal long OldId { get; }

    internal Structure Target { get; }

    internal List<StructureSlot> Slots { get; }

    internal PieceBlocking Before { get; }

    internal PieceBlocking After { get; }

    internal AirChange Change { get; }

    internal int FinalState { get; }

    internal List<MaterialLine> Materials { get; } = new List<MaterialLine>();

    /// <summary>replace_walls: each face the wall blocks.</summary>
    internal List<WallFace>? Faces { get; set; }

    internal bool Stressed { get; set; }

    internal List<GridPoint> AffectedCells { get; set; } = new List<GridPoint>();

    /// <summary>Cells that leave their room because the new piece blocks gravity the old one let through.</summary>
    internal HashSet<GridPoint> SealedCells { get; } = new HashSet<GridPoint>();

    /// <summary>Cells whose gas the game divides among their open neighbours: the new piece blocks air.</summary>
    internal List<GridPoint> DividedCells { get; } = new List<GridPoint>();

    internal List<GuardedFace> GuardedFaces { get; } = new List<GuardedFace>();

    internal string ChangeName => Change switch
    {
        AirChange.Seals => "seals",
        AirChange.WouldOpen => "would_open",
        _ => "keeps"
    };
}

/// <summary>Everything the checks found for one request. Ready when no problem was found.</summary>
internal sealed class StructureSwapPlan
{
    internal StructureSwapPlan(StructureSwapRequest request)
    {
        Request = request;
    }

    internal StructureSwapRequest Request { get; }

    internal int Total { get; set; }

    internal List<PlannedStructureSwap> Swaps { get; } = new List<PlannedStructureSwap>();

    internal List<SkippedPiece> Kept { get; } = new List<SkippedPiece>();

    internal List<SkippedPiece> Unmatched { get; } = new List<SkippedPiece>();

    internal List<UpgradeProblemView> Problems { get; } = new List<UpgradeProblemView>();

    internal Thing? From { get; set; }

    /// <summary>Where the refund goes (refund_to resolved); null until the materials are counted.</summary>
    internal RefundReceivers? Refunds { get; set; }

    /// <summary>Every item a swap costs or gives back, by PrefabHash.</summary>
    internal Dictionary<int, Item> Items { get; } = new Dictionary<int, Item>();

    internal List<MaterialTotal> Totals { get; } = new List<MaterialTotal>();

    internal List<ItemStock> Stocks { get; } = new List<ItemStock>();

    internal StructureAirRecord? Air { get; set; }

    internal bool Ready => Problems.Count == 0;

    internal void Problem(string code, string message, Thing? thing = null) =>
        Problems.Add(new UpgradeProblemView(code, message, thing != null ? new ThingId(thing.ReferenceId) : null));

    internal ItemStock? StockOf(int item)
    {
        foreach (ItemStock stock in Stocks)
        {
            if (stock.Item.PrefabHash == item)
            {
                return stock;
            }
        }

        return null;
    }
}
