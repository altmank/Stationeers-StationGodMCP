#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>One piece to remove: what it is, where it stood, which run kind removes it, and what it gives back.</summary>
internal sealed class PlannedTakedown
{
    internal PlannedTakedown(int index, Structure piece, RunKind? kind, List<ItemAmount> refund)
    {
        Index = index;
        Piece = piece;
        Kind = kind;
        Refund = refund;
        Position = piece.ThingTransformPosition;
        BuildState = piece.CurrentBuildStateIndex;
    }

    internal int Index { get; }

    internal Structure Piece { get; }

    /// <summary>The run kind of a cable, pipe or chute piece (removed as its remove tool does); null otherwise.</summary>
    internal RunKind? Kind { get; }

    internal List<ItemAmount> Refund { get; }

    internal Vector3 Position { get; }

    internal int BuildState { get; }

    internal string KindName => Kind?.Noun ?? "structure";
}

/// <summary>A remove_structure run as planned.</summary>
internal sealed class RemovePlan
{
    internal RemovePlan(RemoveArguments arguments)
    {
        Arguments = arguments;
    }

    internal RemoveArguments Arguments { get; }

    internal List<PlannedTakedown> Takedowns { get; } = new List<PlannedTakedown>();

    /// <summary>The run tools' own plan for each kind's network pieces, which removes them keeping networks whole.</summary>
    internal List<RunPlan> NetworkPlans { get; } = new List<RunPlan>();

    internal List<BuildIssueView> Problems { get; } = new List<BuildIssueView>();

    internal List<BuildIssueView> Warnings { get; } = new List<BuildIssueView>();

    /// <summary>Who takes the refund with refund_to source; null otherwise.</summary>
    internal Thing? From { get; set; }

    internal bool Ready => Problems.Count == 0;

    internal void Add(GuardFinding finding, int index, long id)
    {
        BuildIssueView issue = new BuildIssueView(finding.Code, finding.Message, index, new ThingId(id));
        (finding.Level == GuardLevel.Refusal ? Problems : Warnings).Add(issue);
    }
}

/// <summary>
/// remove_structure's whole preflight; nothing here changes the game. Each id must be a structure. The guards
/// (RemovalRule) read the game: being destroyed, indestructible, rocket, broken, the game's own CanDeconstruct, a
/// mounted device, items in its slots, gas inside, and for a piece that blocks air the pressures of the spaces its
/// removal would join (the cells on both sides of each face it holds, or the open neighbours of each cell it fills,
/// sampled as the game's atmospherics sample them). A device whose port joins something warns that the end will be
/// open. Cable, pipe and chute pieces also go through their remove tool's own planner (RunPlanner): its would_split
/// becomes a warning, its contents refusals are lifted by allow_contents, the rest refuse.
/// </summary>
internal static class RemovePlanner
{
    private static readonly RunKind[] Kinds = { new CableRunKind(), new PipeRunKind(), new ChuteRunKind() };

    internal static RemovePlan Plan(RemoveArguments arguments)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host removes pieces.");
        }

        if (GridController.World == null || AtmosphericsController.World == null)
        {
            throw ApiErrors.Refused("not_ready", "The world grid or atmospherics are not loaded.");
        }

        RemovePlan plan = new RemovePlan(arguments);
        Source(plan);
        HashSet<long> seen = new HashSet<long>();
        for (int index = 0; index < arguments.Ids.Count; index++)
        {
            ThingId id = arguments.Ids[index];
            if (!seen.Add(id.Value))
            {
                continue;
            }

            Structure? piece = Piece(plan, id, index);
            if (piece != null)
            {
                RunKind? kind = KindOf(piece);
                plan.Takedowns.Add(new PlannedTakedown(index, piece, kind, BuildMaterials.RefundOf(piece)));
            }
        }

        BreachedFaces breached = new BreachedFaces();
        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            Guard(plan, takedown, seen, breached);
        }

        foreach (RunKind kind in Kinds)
        {
            NetworkGuard(plan, kind);
        }

        return plan;
    }

    private static Structure? Piece(RemovePlan plan, ThingId id, int index)
    {
        if (!GameLookup.TryFindThing(id, out Thing thing))
        {
            plan.Problems.Add(new BuildIssueView(ApiErrors.ThingNotFoundCode, $"No thing with reference id {id}.",
                index, id));
            return null;
        }

        if (thing is Structure structure)
        {
            return structure;
        }

        string where = thing is DynamicThing { ParentSlot: { } } ? "carried or in a slot" : "loose";
        plan.Problems.Add(new BuildIssueView("not_a_structure",
            $"{thing.DisplayName} ({thing.PrefabName}) is an item ({where}), not a structure; move_item moves items.",
            index, id));
        return null;
    }

    private static RunKind? KindOf(Structure piece)
    {
        foreach (RunKind kind in Kinds)
        {
            if (kind.Family.IsPiece(piece))
            {
                return kind;
            }
        }

        return null;
    }

    private static void Guard(RemovePlan plan, PlannedTakedown takedown, HashSet<long> removed,
        BreachedFaces breached)
    {
        Structure piece = takedown.Piece;
        RemovalFacts facts = new RemovalFacts
        {
            BeingDestroyed = piece.IsBeingDestroyed,
            Indestructible = piece.Indestructable,
            Rocket = RocketParts.Of(piece).PartOfRocket,
            Broken = piece.CurrentBuildStateIndex < 0,
            GameRefusal = GameRefusal(piece),
            Mounted = MountedOn(piece),
            GasMoles = piece.InternalAtmosphere != null ? piece.InternalAtmosphere.TotalMoles.ToDouble() : 0.0,
            GasFate = piece is Tank ? GasFate.Released : GasFate.Lost
        };
        Items(piece, facts.Items);
        List<GridPoint> opened = Breach(piece, facts, removed);
        if (facts.BreachKpa.HasValue && facts.BreachKpa.Value >= RemovalRule.BreachKpa && !breached.Claim(opened))
        {
            // Another piece of this request already reported the breach of these faces (plates back to back).
            facts.BreachKpa = null;
        }

        foreach (GuardFinding finding in RemovalRule.Judge(facts, plan.Arguments.Allow))
        {
            plan.Add(finding, takedown.Index, piece.ReferenceId);
        }

        if (takedown.Kind == null && piece is SmallGrid device)
        {
            OpenPorts(plan, takedown, device, removed);
        }
    }

    private static string? GameRefusal(Structure piece)
    {
        object? answer = GameMembers.StructureCanDeconstruct.Invoke(piece);
        if (!(answer is CanConstructInfo info) || info.CanConstruct)
        {
            return null;
        }

        return string.IsNullOrEmpty(info.ErrorMessage) ? "no reason given" : Text.Plain(info.ErrorMessage);
    }

    private static string? MountedOn(Structure piece)
    {
        foreach (SmallGrid attached in piece.AttachedDevices ?? new List<SmallGrid>())
        {
            if (attached != null && !attached.IsBeingDestroyed)
            {
                return $"{attached.DisplayName} ({attached.PrefabName} {attached.ReferenceId})";
            }
        }

        return null;
    }

    private static void Items(Structure piece, List<string> items)
    {
        if (piece.Slots == null)
        {
            return;
        }

        foreach (Slot slot in piece.Slots)
        {
            DynamicThing? occupant = slot?.Get();
            if (occupant == null || occupant.IsBeingDestroyed)
            {
                continue;
            }

            int quantity = occupant is Stackable stack ? stack.Quantity : 1;
            items.Add(string.Format(CultureInfo.InvariantCulture, "{0} x{1} ({2})", occupant.PrefabName, quantity,
                occupant.ReferenceId));
        }
    }

    // A piece that blocks air joins, when it goes, the cells on both sides of each face it holds, or each cell it
    // fills with its open neighbours, unless the face stays sealed (FaceSeal): something left on the face, or the
    // structure filling a cell beside it (a finished frame), blocks air. Every piece of the request counts as gone at
    // once, so two plates back to back on one face breach when both go, and neither alone. Returns the faces it opens.
    private static List<GridPoint> Breach(Structure piece, RemovalFacts facts, HashSet<long> removed)
    {
        List<GridPoint> opened = new List<GridPoint>();
        if (piece is SmallGrid || piece.CanAirPass)
        {
            return opened;
        }

        GridController grid = GridController.World;
        List<GridPoint> sides = new List<GridPoint>();
        foreach (StructureSlot slot in StructureSlots.Live(piece))
        {
            if (FaceMath.TrySplitFace(slot.Point, out GridPoint a, out GridPoint b))
            {
                if (!Sealed(grid, slot.Point, CellBlocker(grid, a), CellBlocker(grid, b), piece, removed))
                {
                    AddOnce(opened, slot.Point);
                    AddOnce(sides, a);
                    AddOnce(sides, b);
                }

                continue;
            }

            foreach (GridPoint face in FaceMath.FacesOf(slot.Cell))
            {
                if (!FaceMath.TrySplitFace(face, out GridPoint one, out GridPoint two))
                {
                    continue;
                }

                GridPoint neighbour = one.Equals(slot.Cell) ? two : one;
                if (!Sealed(grid, face, null, CellBlocker(grid, neighbour), piece, removed))
                {
                    AddOnce(opened, face);
                    AddOnce(sides, neighbour);
                }
            }
        }

        AtmosphericsController air = AtmosphericsController.World;
        List<double> pressures = new List<double>(sides.Count);
        int low = 0;
        int high = 0;
        for (int index = 0; index < sides.Count; index++)
        {
            Atmosphere? atmosphere = air.SampleGlobalAtmosphere(new WorldGrid(StructureSlots.GridOf(sides[index])));
            pressures.Add(atmosphere != null ? atmosphere.PressureGassesAndLiquids.ToDouble() : 0.0);
            low = pressures[index] < pressures[low] ? index : low;
            high = pressures[index] > pressures[high] ? index : high;
        }

        facts.BreachKpa = RemovalRule.Spread(pressures);
        if (facts.BreachKpa.HasValue)
        {
            facts.BreachWhere = string.Format(CultureInfo.InvariantCulture,
                "{0:0.#} kPa in the cell at {1}, {2:0.#} kPa in the cell at {3}", pressures[high],
                Describe(sides[high]), pressures[low], Describe(sides[low]));
        }

        return opened;
    }

    private static bool Sealed(GridController grid, GridPoint face, AirBlocker? cellA, AirBlocker? cellB,
        Structure piece, HashSet<long> removed)
    {
        List<AirBlocker> onFace = new List<AirBlocker>();
        foreach (Structure structure in new List<Structure>(grid.GetFaceStructures(StructureSlots.GridOf(face))))
        {
            if (structure != null && structure != piece && !structure.IsBeingDestroyed)
            {
                onFace.Add(new AirBlocker(structure.ReferenceId, !structure.CanAirPass));
            }
        }

        HashSet<long> gone = new HashSet<long>(removed) { piece.ReferenceId };
        return FaceSeal.Sealed(onFace, cellA, cellB, gone);
    }

    // The structure filling a 2 m cell (its Center slot), as air sees it; null for an empty cell.
    private static AirBlocker? CellBlocker(GridController grid, GridPoint point)
    {
        Cell? cell = grid.GetCell(StructureSlots.GridOf(point));
        Structure? centre = cell?.Lookup[StructureElement.Center];
        return centre != null && !centre.IsBeingDestroyed
            ? new AirBlocker(centre.ReferenceId, !centre.CanAirPass)
            : (AirBlocker?)null;
    }

    // A device's end that joins something now will be open once it goes (the network piece or device there stays).
    private static void OpenPorts(RemovePlan plan, PlannedTakedown takedown, SmallGrid device, HashSet<long> removed)
    {
        if (device.OpenEnds == null)
        {
            return;
        }

        foreach (Connection end in device.OpenEnds)
        {
            if (end == null)
            {
                continue;
            }

            foreach (Thing attached in EndsReader.AttachedAt(device, end))
            {
                if (removed.Contains(attached.ReferenceId))
                {
                    continue;
                }

                plan.Warnings.Add(new BuildIssueView("port_left_open",
                    $"Its {end.ConnectionType} end joins {attached.DisplayName} ({attached.PrefabName} " +
                    $"{attached.ReferenceId}); that end will be open.", takedown.Index,
                    new ThingId(device.ReferenceId)));
            }
        }
    }

    // The run tools' planner on this kind's pieces: its would_split warns here, contents refusals are lifted by
    // allow_contents, anything else refuses (cannot_remove only where no guard above already refused the piece).
    private static void NetworkGuard(RemovePlan plan, RunKind kind)
    {
        List<PlannedTakedown> pieces = plan.Takedowns.FindAll(takedown => takedown.Kind == kind);
        if (pieces.Count == 0)
        {
            return;
        }

        List<ThingId> ids = pieces.ConvertAll(takedown => new ThingId(takedown.Piece.ReferenceId));
        RunRequest request = new RunRequest(kind, "remove_structure", null,
            new RunRemoval(ids, new List<GridCell>()),
            new RunOptions(EditAllowance.Nothing, null, false, RunArgs.DefaultListLimit));
        RunPlan runPlan = RunPlanner.Plan(request);
        plan.NetworkPlans.Add(runPlan);
        foreach (LayoutIssue issue in runPlan.Problems)
        {
            int? index = IndexOf(pieces, issue.Id);
            ThingId? id = issue.Id.HasValue ? new ThingId(issue.Id.Value) : (ThingId?)null;
            string message = $"{kind.RemoveTool}'s check: {issue.Message}";
            switch (issue.Code)
            {
                case "would_split":
                    plan.Warnings.Add(new BuildIssueView("would_split", message, index, id));
                    break;
                case "holds_contents":
                case "contents_would_move":
                    (plan.Arguments.Allow.Contents ? plan.Warnings : plan.Problems).Add(new BuildIssueView(
                        issue.Code, message + (plan.Arguments.Allow.Contents ? "" : " (allow_contents)"), index, id));
                    break;
                case "cannot_remove" when index.HasValue && AlreadyRefused(plan, index.Value):
                    break;
                default:
                    plan.Problems.Add(new BuildIssueView(issue.Code, message, index, id));
                    break;
            }
        }

        foreach (LayoutIssue issue in runPlan.Warnings)
        {
            plan.Warnings.Add(new BuildIssueView(issue.Code, $"{kind.RemoveTool}'s check: {issue.Message}",
                IndexOf(pieces, issue.Id), issue.Id.HasValue ? new ThingId(issue.Id.Value) : (ThingId?)null));
        }

        plan.Warnings.Add(new BuildIssueView("network_piece",
            $"{pieces.Count} {kind.Noun} piece(s) are removed as {kind.RemoveTool} removes them (a network kept " +
            $"whole keeps its id and contents); {kind.RemoveTool} also takes cells and waypoints."));
    }

    private static int? IndexOf(List<PlannedTakedown> pieces, long? id)
    {
        PlannedTakedown? found = id.HasValue ? pieces.Find(takedown => takedown.Piece.ReferenceId == id.Value) : null;
        return found?.Index;
    }

    private static bool AlreadyRefused(RemovePlan plan, int index) =>
        plan.Problems.Exists(problem => problem.Index == index);

    private static void Source(RemovePlan plan)
    {
        if (plan.Arguments.RefundTo != RefundTo.Source)
        {
            return;
        }

        ThingId? from = plan.Arguments.From;
        if (from.HasValue)
        {
            if (GameLookup.TryFindThing(from.Value, out Thing thing) && !thing.IsBeingDestroyed)
            {
                plan.From = thing;
                return;
            }

            plan.Problems.Add(new BuildIssueView(ApiErrors.ThingNotFoundCode,
                $"No thing with reference id {from.Value} to give the refund to.", null, from.Value));
            return;
        }

        Human human = Human.LocalHuman;
        if (human == null)
        {
            plan.Problems.Add(new BuildIssueView("no_local_player",
                "There is no local player to give the refund to; pass from_id, or refund_to ground or none."));
            return;
        }

        plan.From = human;
    }

    private static void AddOnce(List<GridPoint> cells, GridPoint cell)
    {
        if (!cells.Contains(cell))
        {
            cells.Add(cell);
        }
    }

    private static string Describe(GridPoint cell) => PlacePlanner.Describe(StructureSlots.MetresOf(cell));
}
