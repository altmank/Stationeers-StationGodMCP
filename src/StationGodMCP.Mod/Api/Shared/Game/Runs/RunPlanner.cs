#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The whole preflight of the run tools: nothing here changes the game. It reads the pieces to remove, lays the run
/// out (RunLayoutPlanner), picks each cell's piece from the kit (RunCatalogue) and checks it may stand there
/// (PlacementCheck), prices it, counts the coils or kits, forecasts the networks (RunForecastBuilder) and applies
/// the guards: would_bridge and would_split (EditGuards), the kind's own (overload, burst, contents), and the model's
/// reading of the links around the edit, which must match the game's. Every problem is listed; none stops the others
/// from being looked for.
/// </summary>
internal static class RunPlanner
{
    internal const int MaximumRemovals = 1024;

    internal static RunPlan Plan(RunRequest request)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host builds pieces.");
        }

        RunPlan plan = new RunPlan(request);
        ReadRemovals(plan);
        if (request.Build != null)
        {
            LayOut(plan, request.Build);
        }

        CountMaterials(plan);
        if (plan.Cells.Count == 0 && plan.Removals.Count == 0)
        {
            plan.Problem("nothing_to_do", plan.KeptCells.Count > 0
                ? "Every cell of the run already has the piece it needs."
                : "Nothing to build or remove.");
            return plan;
        }

        plan.Forecast = RunForecastBuilder.Build(plan);
        Guard(plan, plan.Forecast);
        request.Kind.CheckEdit(plan);
        return plan;
    }

    private static void ReadRemovals(RunPlan plan)
    {
        RunRemoval removal = plan.Request.Removal;
        RunKind kind = plan.Request.Kind;
        List<SmallGrid> pieces = new List<SmallGrid>();
        HashSet<long> seen = new HashSet<long>();
        foreach (ThingId id in removal.Ids)
        {
            if (!GameLookup.TryFindThing(id, out Thing thing) || thing.IsBeingDestroyed)
            {
                plan.Problem(ApiErrors.ThingNotFoundCode, $"No thing with reference id {id}.", id.Value);
            }
            else if (!(thing is SmallGrid piece) || !kind.Family.IsPiece(thing))
            {
                plan.Problem($"not_a_{kind.Noun}_piece",
                    $"{thing.DisplayName} ({thing.PrefabName}) is not a {kind.Noun} piece.", thing.ReferenceId);
            }
            else if (seen.Add(piece.ReferenceId))
            {
                pieces.Add(piece);
            }
        }

        GridController world = GridController.World;
        foreach (GridCell cell in removal.Cells)
        {
            SmallCell? small = world.GetSmallCell(PieceShapes.Grid(cell));
            SmallGrid? piece = small != null ? kind.SlotOf(small) : null;
            if (piece == null || piece.IsBeingDestroyed)
            {
                plan.Warnings.Add(new LayoutIssue("cell_empty", $"No {kind.Noun} piece stands in {cell}.", cell));
            }
            else if (seen.Add(piece.ReferenceId))
            {
                pieces.Add(piece);
            }
        }

        if (pieces.Count > MaximumRemovals)
        {
            throw ApiErrors.Refused("too_many_pieces", $"{pieces.Count} pieces; at most {MaximumRemovals} per run.");
        }

        ReadAssumed(plan, removal, seen);
        foreach (SmallGrid piece in pieces)
        {
            string? why = CannotRemove(kind, piece);
            if (why != null)
            {
                plan.Problem("cannot_remove", $"{piece.PrefabName} {piece.ReferenceId}: {why}.", piece.ReferenceId);
            }

            plan.Things[piece.ReferenceId] = piece;
            plan.Removals.Add(new PlannedRemoval(piece, PieceShapes.Live(piece), kind.Family.NetworkOf(piece),
                BuildMaterials.RefundOf(piece)));
        }
    }

    internal const string AssumedPresentCode = "assumed_present";

    // assume_removed: a thing already gone is what was assumed; one still standing is checked as gone (the kind's
    // pieces forecast as removed, anything else only freed for placement) and noted, since a real run needs it gone.
    private static void ReadAssumed(RunPlan plan, RunRemoval removal, HashSet<long> seen)
    {
        RunKind kind = plan.Request.Kind;
        foreach (ThingId id in removal.Assumed)
        {
            if (!GameLookup.TryFindThing(id, out Thing thing) || thing.IsBeingDestroyed || !seen.Add(id.Value))
            {
                continue;
            }

            if (!(thing is SmallGrid piece))
            {
                plan.Problem("not_a_small_grid_thing",
                    $"assume_removed: {thing.DisplayName} ({thing.PrefabName}) does not stand on the small grid.",
                    thing.ReferenceId);
                continue;
            }

            plan.AssumedPresent.Add(piece.ReferenceId);
            plan.Things[piece.ReferenceId] = piece;
            if (kind.Family.IsPiece(piece))
            {
                plan.Removals.Add(new PlannedRemoval(piece, PieceShapes.Live(piece), kind.Family.NetworkOf(piece),
                    BuildMaterials.RefundOf(piece), true));
            }
            else
            {
                plan.AssumedOther.Add(piece.ReferenceId);
            }
        }

        if (plan.AssumedPresent.Count > 0)
        {
            plan.Warnings.Add(new LayoutIssue(AssumedPresentCode,
                $"{plan.AssumedPresent.Count} thing(s) in assume_removed still stand; this check treats them as " +
                "gone. A real run is refused until they are removed (remove the " + kind.Noun + " pieces in the " +
                "same job by passing them as remove_ids instead).", null, plan.AssumedPresent[0]));
        }
    }

    // The game refuses to deconstruct a piece with a device mounted on it (Cable.CanDeconstruct, Pipe.CanDeconstruct).
    private static string? CannotRemove(RunKind kind, SmallGrid piece)
    {
        if (piece.Indestructable)
        {
            return "it is indestructible";
        }

        if (InRocket(piece))
        {
            return "it is inside a rocket";
        }

        Device? mounted = piece.SmallCell?.Device;
        return mounted != null && kind.Family.IsMountedOn(mounted)
            ? $"{mounted.PrefabName} {mounted.ReferenceId} is mounted on it; take that off first"
            : kind.Holding(piece);
    }

    /// <summary>Whether the piece is part of a rocket (its network is the rocket's).</summary>
    internal static bool InRocket(SmallGrid piece) =>
        (piece is Cable cable && cable.RocketNetwork != null) || (piece is Pipe pipe && pipe.RocketNetwork != null) ||
        (piece is Chute chute && chute.RocketNetwork != null);

    private static void LayOut(RunPlan plan, RunBuild build)
    {
        RunKind kind = plan.Request.Kind;
        KitCatalogue kits = KitCatalogue.Of(kind.Family);
        Kit? kit = kits.For(build.Grade);
        if (kit == null || kit.Pieces.Count == 0)
        {
            plan.Problem("no_kit", kits.IsAmbiguous(build.Grade)
                ? $"Two coils or kits place {kind.NameOf(build.Grade)} pieces equally."
                : $"No coil or kit places {kind.NameOf(build.Grade)} pieces.");
            return;
        }

        SmallGridBlock mask = kit.Pieces[0] is SmallGrid first ? first.SmallCollisionType : SmallGridBlock.None;
        RunLayout layout = Lay(plan, build, build.Shape, mask);
        Dictionary<GridCell, SmallGrid> split = plan.Request.Options.SplitLong
            ? SplitLongPieces(plan, layout)
            : new Dictionary<GridCell, SmallGrid>();
        if (split.Count > 0)
        {
            layout = Lay(plan, build, build.Shape.WithFills(FillEnds(split)), mask);
        }

        HashSet<long> ignore = plan.IgnoredIds();
        plan.Layout = layout;
        plan.Problems.AddRange(layout.Problems);
        plan.Warnings.AddRange(layout.Warnings);
        FindAir(plan, layout, new GridFacts(kind, mask, ignore));
        Dictionary<int, RunCatalogue> catalogues = new Dictionary<int, RunCatalogue>();
        List<OrientableCell> orientable = new List<OrientableCell>();
        long next = -1;
        foreach (LayoutCell cell in layout.Cells)
        {
            if (cell.Action == CellAction.Keep)
            {
                plan.KeptCells.Add(cell);
                continue;
            }

            SmallGrid? existing = cell.Existing != null && plan.Things.TryGetValue(cell.Existing.Id, out SmallGrid found)
                ? found
                : null;
            SmallGrid? splitFrom = existing == null && split.TryGetValue(cell.Cell, out SmallGrid source)
                ? source
                : null;
            Kit? cellKit = existing != null ? KitOf(kits, kind, existing)
                : splitFrom != null ? GradeKit(kits, kind, splitFrom)
                : kit;
            if (cellKit == null)
            {
                plan.Unchosen.Add(cell);
                plan.Problem("no_kit", $"No coil or kit places {existing!.PrefabName}, so it cannot become a " +
                                       "junction.", existing.ReferenceId, cell.Cell);
                continue;
            }

            RunCatalogue catalogue = CatalogueOf(catalogues, cellKit, build.Cells[0]);
            long id = existing != null ? existing.ReferenceId : next--;
            List<RunChoice> turns = catalogue.Orientations(cell.Ends);
            if (turns.Count > 1)
            {
                orientable.Add(new OrientableCell(cell, catalogue, turns, existing, id,
                    splitFrom != null ? PieceLook.Of(splitFrom) : null));
                continue;
            }

            Choose(plan, cell, catalogue, turns.Count == 1 ? turns[0] : null, existing, id, ignore,
                splitFrom != null ? PieceLook.Of(splitFrom) : null);
        }

        if (orientable.Count == 0)
        {
            return;
        }

        Dictionary<GridCell, RunChoice> picked = kind.Orient(plan, orientable);
        foreach (OrientableCell cell in orientable)
        {
            Choose(plan, cell.Layout, cell.Catalogue, picked[cell.Layout.Cell], cell.Existing, cell.Id, ignore,
                cell.Look);
        }
    }

    internal const string LongSplit = "long_split";

    internal const string ThroughAir = "through_air";

    private const int AirListed = 12;

    // The run's new pieces that no frame or wall holds up: counted in the report and named in one warning.
    private static void FindAir(RunPlan plan, RunLayout layout, GridFacts facts)
    {
        foreach (LayoutCell cell in layout.Cells)
        {
            if (cell.InRun && cell.Action == CellAction.Place && facts.Support(cell.Cell) == CellSupport.Air)
            {
                plan.AirCells.Add(cell.Cell);
            }
        }

        if (plan.AirCells.Count == 0)
        {
            return;
        }

        List<string> listed = new List<string>(AirListed);
        for (int index = 0; index < plan.AirCells.Count && index < AirListed; index++)
        {
            listed.Add(Metres(plan.AirCells[index]));
        }

        string more = plan.AirCells.Count > AirListed ? $" and {plan.AirCells.Count - AirListed} more" : string.Empty;
        plan.Warnings.Add(new LayoutIssue(ThroughAir,
            $"{plan.AirCells.Count} new pieces float in air (on no frame and no wall plane): " +
            $"{string.Join(", ", listed)}{more}. Route over frames or along walls (plan_*_route frames_first), or " +
            "build a frame under them.", plan.AirCells[0]));
    }

    private static string Metres(GridCell cell)
    {
        UnityEngine.Vector3 centre = PieceShapes.CentreOf(cell);
        return string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})", centre.x, centre.y,
            centre.z);
    }

    private static RunLayout Lay(RunPlan plan, RunBuild build, RunShape shape, SmallGridBlock mask)
    {
        RunKind kind = plan.Request.Kind;
        RunSurroundings around = RunSurvey.Around(kind, build.Grade, shape.Cells, build.Extra, mask,
            plan.IgnoredIds(), plan.Things);
        return RunLayoutPlanner.Plan(shape, around, build.Join, build.Extra, kind.ContentOf(build.Grade));
    }

    // Every long straight the layout would have to give a new end in its middle (long_piece) that may be removed
    // and replaced: it joins the removals, and each of its cells is laid again as a single (FillEnds), in the long
    // piece's own grade, owner and colour. Keyed by cell.
    private static Dictionary<GridCell, SmallGrid> SplitLongPieces(RunPlan plan, RunLayout layout)
    {
        RunKind kind = plan.Request.Kind;
        Dictionary<GridCell, SmallGrid> split = new Dictionary<GridCell, SmallGrid>();
        HashSet<long> removed = plan.RemovedIds();
        foreach (LayoutIssue issue in layout.Problems)
        {
            if (plan.Removals.Count >= MaximumRemovals)
            {
                break;
            }

            if (issue.Code != "long_piece" || !issue.Id.HasValue || removed.Contains(issue.Id.Value) ||
                !plan.Things.TryGetValue(issue.Id.Value, out SmallGrid piece) ||
                CannotRemove(kind, piece) != null || RunSurvey.FixedReason(kind, piece) != null)
            {
                continue;
            }

            removed.Add(piece.ReferenceId);
            PieceModel live = PieceShapes.Live(piece);
            plan.Removals.Add(new PlannedRemoval(piece, live, kind.Family.NetworkOf(piece),
                BuildMaterials.RefundOf(piece)));
            foreach (GridCell cell in live.Cells)
            {
                split[cell] = piece;
            }

            plan.Warnings.Add(new LayoutIssue(LongSplit,
                $"{piece.PrefabName} {piece.ReferenceId} ({live.Cells.Count} cells) is split into single pieces in " +
                "the same job so the run can join it (allow_split_long false refuses instead).", null,
                piece.ReferenceId));
        }

        return split;
    }

    // Each cell of a split long straight: ends towards its neighbours in the piece, and the piece's own ends there.
    private static Dictionary<GridCell, EndSet> FillEnds(Dictionary<GridCell, SmallGrid> split)
    {
        Dictionary<GridCell, EndSet> fills = new Dictionary<GridCell, EndSet>();
        Dictionary<long, PieceModel> models = new Dictionary<long, PieceModel>();
        foreach (KeyValuePair<GridCell, SmallGrid> entry in split)
        {
            if (!models.TryGetValue(entry.Value.ReferenceId, out PieceModel model))
            {
                model = PieceShapes.Live(entry.Value);
                models[entry.Value.ReferenceId] = model;
            }

            EndSet ends = EndSet.AtCell(model, entry.Key);
            foreach (GridStep step in GridStep.All)
            {
                if (model.Occupies(step.From(entry.Key)))
                {
                    ends = ends.With(step);
                }
            }

            fills[entry.Key] = ends;
        }

        return fills;
    }

    // The kit of the piece's own grade, whether or not it lists that very piece (a long straight).
    private static Kit? GradeKit(KitCatalogue kits, RunKind kind, SmallGrid piece)
    {
        Grade? grade = kind.Family.RunGradeOf(piece) ?? kind.Family.GradeOf(piece);
        return grade != null ? kits.For(grade) : null;
    }

    private static Kit? KitOf(KitCatalogue kits, RunKind kind, SmallGrid piece)
    {
        Grade? grade = kind.Family.GradeOf(piece);
        Kit? kit = grade != null ? kits.For(grade) : null;
        return kit != null && kit.Places(piece.PrefabHash) ? kit : null;
    }

    private static RunCatalogue CatalogueOf(Dictionary<int, RunCatalogue> catalogues, Kit kit, GridCell reference)
    {
        if (!catalogues.TryGetValue(kit.Item.PrefabHash, out RunCatalogue catalogue))
        {
            catalogue = RunCatalogue.Of(kit, reference);
            catalogues[kit.Item.PrefabHash] = catalogue;
        }

        return catalogue;
    }

    private static void Choose(RunPlan plan, LayoutCell cell, RunCatalogue catalogue, RunChoice? choice,
        SmallGrid? existing, long id, HashSet<long> ignore, PieceLook? look = null)
    {
        PieceModel? model = choice != null ? RunCatalogue.Verified(choice, cell.Cell, cell.Ends, id) : null;
        if (choice == null || model == null)
        {
            plan.Unchosen.Add(cell);
            plan.Problem("no_piece_for_ends",
                $"No piece of {catalogue.Kit.Item.PrefabName} has exactly the ends " +
                $"[{string.Join(", ", cell.Ends.Names())}] ({cell.Ends.Shape}); it has " +
                $"{string.Join(", ", catalogue.Shapes())}.", IdOf(existing), cell.Cell);
            return;
        }

        HashSet<long> gone = new HashSet<long>(ignore);
        if (existing != null)
        {
            gone.Add(existing.ReferenceId);
        }

        string? refusal = PlacementCheck.Refusal(choice.Prefab, PieceShapes.CentreOf(cell.Cell), choice.Rotation,
            gone);
        if (refusal != null)
        {
            plan.Problem("cell_blocked", $"Cell {cell.Cell}: {refusal}.", IdOf(existing), cell.Cell);
        }

        if (choice.Prefab.BuildStates == null || choice.Prefab.BuildStates.Count != 1)
        {
            plan.Problem("target_needs_building",
                $"{choice.Prefab.PrefabName} is not finished when placed.", IdOf(existing), cell.Cell);
        }

        Twin twin = new Twin(choice.Prefab, PieceShapes.CentreOf(cell.Cell), choice.Rotation, model);
        SwapPrice price = existing != null
            ? PlanContext.Priced(new List<OldPiece> { new OldPiece(existing, PieceShapes.Live(existing)) },
                catalogue.Kit, new List<Twin> { twin })
            : new SwapPrice(Kit.CostOf(choice.Prefab), new List<ItemAmount>());
        plan.Cells.Add(new PlannedCell(cell, catalogue.Kit, choice, model, price, existing, look));
    }

    private static long? IdOf(SmallGrid? piece) => piece != null ? piece.ReferenceId : (long?)null;

    private static void CountMaterials(RunPlan plan)
    {
        plan.From = Source(plan);
        Dictionary<int, int> needed = new Dictionary<int, int>();
        List<Kit> kits = new List<Kit>();
        foreach (PlannedCell cell in plan.Cells)
        {
            if (!needed.ContainsKey(cell.Kit.Item.PrefabHash))
            {
                needed[cell.Kit.Item.PrefabHash] = 0;
                kits.Add(cell.Kit);
            }

            needed[cell.Kit.Item.PrefabHash] += cell.Cost;
        }

        foreach (Kit kit in kits)
        {
            ItemStock stock = plan.From != null ? ItemStock.In(plan.From, kit.Item) : ItemStock.Empty(kit.Item);
            stock.Needed = needed[kit.Item.PrefabHash];
            plan.Stocks.Add(stock);
            if (plan.From != null && stock.Available < stock.Needed)
            {
                plan.Problem("not_enough_coils",
                    $"{stock.Needed} {kit.Item.DisplayName} needed, {stock.Available} held by " +
                    $"{plan.From.DisplayName}.", plan.From.ReferenceId);
            }
        }
    }

    private static Thing? Source(RunPlan plan)
    {
        ThingId? from = plan.Request.Options.From;
        if (from.HasValue)
        {
            if (GameLookup.TryFindThing(from.Value, out Thing thing) && !thing.IsBeingDestroyed)
            {
                return thing;
            }

            plan.Problem(ApiErrors.ThingNotFoundCode, $"No thing with reference id {from.Value} to take coils from.",
                from.Value.Value);
            return null;
        }

        Human human = Human.LocalHuman;
        if (human == null)
        {
            plan.Problem("no_local_player", "There is no local player to take coils from; pass from_id.");
        }

        return human;
    }

    private static void Guard(RunPlan plan, RunForecast forecast)
    {
        foreach (long id in forecast.Unreadable)
        {
            plan.Problem("links_unreadable", $"The game's connections of {id} could not be read.", id);
        }

        foreach (Link link in forecast.ModelCheck.Added)
        {
            plan.Problem("connectivity_model_mismatch",
                $"{link.From} to {link.To}: the model links them and the game does not; the forecast cannot be " +
                "trusted here.", link.From);
        }

        foreach (Link link in forecast.ModelCheck.Lost)
        {
            plan.Problem("connectivity_model_mismatch",
                $"{link.From} to {link.To}: the game links them and the model does not; the forecast cannot be " +
                "trusted here.", link.From);
        }

        LostLinks(plan, forecast);
        Loops(plan, forecast);
        plan.Problems.AddRange(EditGuards.Check(forecast.Result, plan.Request.Options.Allow));
        HashSet<int> touched = Touched(plan, forecast);
        foreach (KeyValuePair<int, KindGuard> guard in forecast.Guards)
        {
            if (guard.Value.Code != null && touched.Contains(guard.Key))
            {
                plan.Problem(guard.Value.Code, guard.Value.Message ?? guard.Value.Code);
            }
        }

        LayoutIssue? removal = plan.Request.Kind.RemovalProblem(forecast.Result, forecast.Context);
        if (removal != null)
        {
            plan.Problems.Add(removal);
        }
    }

    internal const string WouldLoop = "would_loop";

    // A warning, not a refusal: a loop may be redundancy the player wants.
    private static void Loops(RunPlan plan, RunForecast forecast)
    {
        if (forecast.Loops.Count == 0)
        {
            return;
        }

        List<string> links = forecast.Loops.ConvertAll(link => $"{link.From}-{link.To}");
        Link first = forecast.Loops[0];
        plan.Warnings.Add(new LayoutIssue(WouldLoop,
            $"The edit joins what is already joined another way: {forecast.Loops.Count} new link(s) close a loop " +
            $"({string.Join(", ", links.GetRange(0, System.Math.Min(links.Count, 8)))}; negative ids are new " +
            "pieces). The network gets a second path between those points; clean_cables or clean_pipes " +
            "remove_loops would cut it again. Keep it only if the redundancy is meant; to reach a second port of a " +
            "device already on the network, route to that port from the network in one plan (several ports in " +
            "from).", null, first.From > 0 ? first.From : first.To > 0 ? first.To : (long?)null));
    }

    // A change only adds ends: every link a changed piece has now must still be there after the edit.
    private static void LostLinks(RunPlan plan, RunForecast forecast)
    {
        HashSet<long> removed = plan.RemovedIds();
        foreach (Link link in forecast.GameBefore)
        {
            if (removed.Contains(link.From) || removed.Contains(link.To))
            {
                continue;
            }

            if (!forecast.After.Contains(link))
            {
                plan.Problem("link_lost",
                    $"After the edit {link.From} would no longer connect to {link.To}.", link.From);
            }
        }
    }

    // Networks after the edit that hold a new or changed piece, or a piece of a network that loses pieces.
    private static HashSet<int> Touched(RunPlan plan, RunForecast forecast)
    {
        HashSet<int> touched = new HashSet<int>();
        foreach (PlannedCell cell in plan.Cells)
        {
            if (forecast.NodeOf.TryGetValue(cell.ForecastId, out long node) &&
                forecast.ComponentOf.TryGetValue(node, out int index))
            {
                touched.Add(index);
            }
        }

        foreach (KeyValuePair<long, long> node in forecast.NodeOf)
        {
            if (node.Key == node.Value && node.Key > 0 && forecast.ComponentOf.TryGetValue(node.Key, out int index))
            {
                touched.Add(index);
            }
        }

        return touched;
    }
}
