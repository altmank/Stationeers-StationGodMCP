#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Networks;
using Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>What clean_chutes does with a chute an item rides in.</summary>
internal enum RidingPolicy
{
    /// <summary>The piece stays, and with it whatever the item moves on into; the rest of the run goes ahead.</summary>
    Skip,

    /// <summary>The run is refused while any piece it would remove or replace carries an item.</summary>
    Refuse
}

/// <summary>A chute piece the run would change that has an item riding in it, and what happens to it.</summary>
internal sealed class RidingChute
{
    internal RidingChute(SmallGrid piece, DynamicThing item, string held)
    {
        Piece = piece;
        Item = item;
        Held = held;
    }

    internal SmallGrid Piece { get; }

    internal DynamicThing Item { get; }

    /// <summary>kept (a dead piece left in place) or not_simplified (a junction, overflow or splitter left whole).</summary>
    internal string Held { get; }
}

/// <summary>
/// clean_chutes' one operation: every selected chute piece that serves no path is removed and every junction,
/// overflow or splitter that loses a branch becomes the plain piece its remaining ends need (ChuteCleanup on the whole
/// networks the selection is on, ChuteSurroundings). Pieces come from and go back to Kit (Chute); a replacement keeps
/// the old piece's paint and owner (the swap builds it from the old piece). A piece with an item riding in it is never
/// removed or replaced: the game destroys what rides in a chute with it.
/// </summary>
internal sealed class RemoveDeadChutes : ICleanOperation
{
    internal const string ItemsRidingCode = "items_riding";
    internal const string RemoveOperation = "remove_dead_chute";
    internal const string ReduceOperation = "simplify_junction";
    internal const string Kept = "kept";
    internal const string NotSimplified = "not_simplified";

    private readonly HashSet<long> _keep;
    private readonly RidingPolicy _riding;

    internal RemoveDeadChutes(HashSet<long> keep, RidingPolicy riding)
    {
        _keep = keep;
        _riding = riding;
    }

    public string Name => CleanOperationSet.RemoveDeadChutes;

    public void Plan(CleanPass pass)
    {
        List<SmallGrid> open = pass.Open;
        Kit? kit = pass.Context.Kits.For(ChuteFamily.Chute);
        if (kit == null)
        {
            pass.Plan.Problem("no_kit", "No kit places chute pieces; nothing can be removed or replaced.");
            return;
        }

        ChuteSurroundings around = ChuteSurroundings.Of(open);
        if (around.TooLarge)
        {
            pass.Plan.Problem("too_many_pieces",
                $"The chute networks of these pieces hold more than {ChuteSurroundings.MaximumPieces} pieces; their " +
                "flow cannot be judged in one run.");
            return;
        }

        Dictionary<long, SmallGrid> byId = new Dictionary<long, SmallGrid>(open.Count);
        foreach (SmallGrid piece in open)
        {
            byId[piece.ReferenceId] = piece;
        }

        Dictionary<long, Twin> twins = new Dictionary<long, Twin>();
        ChuteCleanupScene scene = SceneOf(pass, around, byId, kit, twins);
        ChuteCleanupResult result = ChuteCleanup.Plan(scene);
        List<RidingChute> riding = new List<RidingChute>();
        foreach (ChuteVerdict verdict in result.Verdicts)
        {
            Apply(pass, byId[verdict.Id], verdict, kit, twins, riding);
        }

        pass.Plan.Riding = riding;
        if (_riding == RidingPolicy.Refuse && riding.Count > 0)
        {
            pass.Plan.Problem(ItemsRidingCode,
                $"{riding.Count} chute piece(s) this run would remove or replace carry an item (see riding); let " +
                "the items pass or take them out with move_item, or pass riding: skip to leave those pieces.");
        }
    }

    private ChuteCleanupScene SceneOf(CleanPass pass, ChuteSurroundings around, Dictionary<long, SmallGrid> byId,
        Kit kit, Dictionary<long, Twin> twins)
    {
        List<PieceModel> pieces = new List<PieceModel>(around.Before.Count);
        List<long> riding = new List<long>();
        Dictionary<long, string> held = new Dictionary<long, string>();
        foreach (PieceModel model in around.Before.Values)
        {
            pieces.Add(model);
            SmallGrid? piece = Referencable.Find<SmallGrid>(model.Id);
            if (piece != null && ChuteFamily.ItemIn(piece) != null)
            {
                riding.Add(model.Id);
            }
        }

        foreach (SmallGrid piece in byId.Values)
        {
            string? reason = HeldBy(piece);
            if (reason != null)
            {
                held[piece.ReferenceId] = reason;
            }
        }

        return new ChuteCleanupScene(pieces, around.Ports, byId.Keys, held, riding, OutletDevices(around.Ports),
            plain => Plain(pass, byId, kit, twins, plain));
    }

    private string? HeldBy(SmallGrid piece) =>
        _keep.Contains(piece.ReferenceId) ? ChuteCleanupNames.KeepIds
        : ChuteFamily.ItemIn(piece) != null ? ChuteCleanupNames.ItemRiding
        : piece.Indestructable ? "indestructible"
        : RunPlanner.InRocket(piece) ? "rocket_internal"
        : null;

    // Chute devices move items along the chute themselves and drop them out of a port nothing is joined to.
    private static List<long> OutletDevices(List<DevicePort> ports)
    {
        List<long> devices = new List<long>();
        foreach (DevicePort port in ports)
        {
            if (!devices.Contains(port.DeviceId) && Referencable.Find<Thing>(port.DeviceId) is IChute)
            {
                devices.Add(port.DeviceId);
            }
        }

        return devices;
    }

    // Whether Kit (Chute) places a piece with the model; the twin found is kept for the swap.
    private static bool Plain(CleanPass pass, Dictionary<long, SmallGrid> byId, Kit kit, Dictionary<long, Twin> twins,
        PieceModel plain)
    {
        if (!byId.TryGetValue(plain.Id, out SmallGrid piece))
        {
            return false;
        }

        Twin? twin = pass.Context.Twins.Find(piece, plain, kit);
        if (twin == null)
        {
            twins.Remove(plain.Id);
            return false;
        }

        twins[plain.Id] = twin;
        return true;
    }

    private static void Apply(CleanPass pass, SmallGrid piece, ChuteVerdict verdict, Kit kit,
        Dictionary<long, Twin> twins, List<RidingChute> riding)
    {
        PieceModel live = pass.LiveOf(piece);
        switch (verdict)
        {
            case RemovedChute removed:
                pass.Remove(piece, kit, CleanPass.Detail(RemoveOperation, live, pass.ConnectedEnds(live), null,
                    ChuteCleanupNames.Of(removed.Reason)));
                break;
            case ReducedChute reduced:
                pass.Replace(new List<SmallGrid> { piece }, kit, new List<Twin> { twins[piece.ReferenceId] },
                    CleanPass.Detail(ReduceOperation, live, new List<PieceEnd>(reduced.Connected)));
                break;
            case KeptDeadChute kept:
                pass.DeadEnd(piece, pass.ConnectedEnds(live), kept.HeldBy, kept.Detail,
                    ChuteCleanupNames.Of(kept.Reason));
                NoteRiding(piece, kept.HeldBy, Kept, riding);
                break;
            case OpenEndedChute openEnded:
                pass.Claim(piece);
                pass.Plan.Kept.Add(new SkippedPiece(piece, openEnded.HeldBy, OpenEndMessage(piece, openEnded.HeldBy)));
                NoteRiding(piece, openEnded.HeldBy, NotSimplified, riding);
                break;
            case LiveChute:
                // Counted in pieces_total only: a report listing every piece on a path says nothing.
                pass.Claim(piece);
                break;
        }
    }

    private static void NoteRiding(SmallGrid piece, string heldBy, string held, List<RidingChute> riding)
    {
        DynamicThing? item = heldBy == ChuteCleanupNames.ItemRiding ? ChuteFamily.ItemIn(piece) : null;
        if (item != null)
        {
            riding.Add(new RidingChute(piece, item, held));
        }
    }

    private static string OpenEndMessage(SmallGrid piece, string heldBy) =>
        heldBy == ChuteCleanupNames.NoPlainPiece
            ? $"{piece.PrefabName} keeps an end that leads nowhere: Kit (Chute) places no plain piece with its other " +
              "two ends."
            : $"{piece.PrefabName} keeps an end that leads nowhere ({heldBy}).";
}
