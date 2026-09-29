#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Shared.Game.Upgrades;
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
/// (RemovalRule) read the game: being destroyed, indestructible, rocket, broken (allow_broken: nothing given back), the
/// game's own CanDeconstruct (not asked for a broken piece taken with allow_broken, as the game does not), a
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
                plan.Takedowns.Add(new PlannedTakedown(index, piece, kind, RefundOf(piece)));
            }
        }

        BreachedFaces breached = new BreachedFaces();
        HashSet<long> emptied = new HashSet<long>();
        GridFacts grid = new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());
        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            Guard(plan, takedown, seen, breached, emptied, grid);
        }

        Squeeze(plan, emptied);
        HolderRemoved(plan, seen);

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
        string what = thing is Item ? "an item" : thing is DynamicThing ? "a movable thing" : "a thing";
        plan.Problems.Add(new BuildIssueView("not_a_structure",
            $"{thing.DisplayName} ({thing.PrefabName}) is {what} ({where}), not a structure; move_item moves items " +
            "and movable things.", index, id));
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
        BreachedFaces breached, HashSet<long> emptied, GridFacts grid)
    {
        Structure piece = takedown.Piece;
        RemovalFacts facts = new RemovalFacts
        {
            BeingDestroyed = piece.IsBeingDestroyed,
            Indestructible = piece.Indestructable,
            Rocket = RocketParts.Of(piece).PartOfRocket,
            Broken = piece.IsBroken,
            GameRefusal = GameRefusal(piece),
            Mounted = MountedOn(piece) ?? Unsupported(piece, removed, grid),
            GasMoles = piece.InternalAtmosphere != null ? piece.InternalAtmosphere.TotalMoles.ToDouble() : 0.0,
            GasFate = piece is Tank ? GasFate.Released : GasFate.Lost
        };
        if (takedown.Kind == null && piece is Pipe member)
        {
            NetworkGas(member, removed, emptied, facts);
        }

        Items(piece, facts.Items);
        Breach(piece, facts, removed, breached);

        foreach (GuardFinding finding in RemovalRule.Judge(facts, plan.Arguments.Allow))
        {
            plan.Add(finding, takedown.Index, piece.ReferenceId);
        }

        if (takedown.Kind == null && piece is SmallGrid device)
        {
            OpenPorts(plan, takedown, device, removed);
        }
    }

    // What removing it gives back. A broken piece gives nothing: the game deconstructs one (Structure.AttackWith, the
    // BrokenBuildStates branch) with StructureDestroyed(destroyedFromDamage: true), which skips the kit refund
    // (BuildStates[0].Tool.Deconstruct); its build state below 0 would give nothing by MaterialRule anyway, and a piece
    // at full damage not yet swapped to its broken state (still at 0 or above) is on its way there.
    private static List<ItemAmount> RefundOf(Structure piece) =>
        piece.IsBroken ? new List<ItemAmount>() : BuildMaterials.RefundOf(piece);

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

    // A pipe-network member that is not a pipe piece (an in-line tank, a passive vent) holds its network's gas: the
    // game's Pipe.OnDestroy divides that gas among the network's other members, and deletes it with the last one. So
    // when the request removes every member of a network holding gas, the gas goes with them (holds_gas, reported once
    // per network, on its first member in the request).
    private static void NetworkGas(Pipe member, HashSet<long> removed, HashSet<long> emptied, RemovalFacts facts)
    {
        PipeNetwork? network = member.PipeNetwork;
        if (network == null || network.Atmosphere == null || emptied.Contains(network.ReferenceId))
        {
            return;
        }

        foreach (SmallGrid other in RunNetworks.PipeMembers(network))
        {
            if (!other.IsBeingDestroyed && !removed.Contains(other.ReferenceId))
            {
                return;
            }
        }

        double moles = GasSnapshot.Of(network.Atmosphere).TotalMol();
        if (moles < RemovalRule.GasFloorMol)
        {
            return;
        }

        emptied.Add(network.ReferenceId);
        facts.GasMoles += moles;
        facts.GasFate = GasFate.Lost;
        facts.GasWhere = $" in pipe network {network.ReferenceId}, whose last member this removal takes";
    }

    // A pipe-network member that is not a pipe piece (an in-line tank, a passive vent) takes its volume with it while
    // its network's gas stays: Pipe.OnDestroy hands the whole mixture to what is left
    // (NetworkAtmosphereEvent.DivideNetworkAtmosphere), so the same gas fills less room (structures-23: 605 mol N2
    // went from 5.5 MPa to 73.7 MPa in two pipes rated 60.8 MPa). Forecast per network the request shrinks that way,
    // its pipe pieces removed alongside counted too, and refuse over the weakest pipe left as the pipe tools do
    // (would_burst). A network the request empties is holds_gas's (NetworkGas); pipe pieces alone are the remove
    // tool's own check (NetworkGuard).
    private static void Squeeze(RemovePlan plan, HashSet<long> emptied)
    {
        Dictionary<long, List<PlannedTakedown>> byNetwork = new Dictionary<long, List<PlannedTakedown>>();
        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            if (takedown.Piece is Pipe pipe && pipe.PipeNetwork != null)
            {
                long id = pipe.PipeNetwork.ReferenceId;
                if (!byNetwork.TryGetValue(id, out List<PlannedTakedown> members))
                {
                    members = new List<PlannedTakedown>();
                    byNetwork[id] = members;
                }

                members.Add(takedown);
            }
        }

        foreach (KeyValuePair<long, List<PlannedTakedown>> entry in byNetwork)
        {
            PlannedTakedown? member = entry.Value.Find(takedown => takedown.Kind == null);
            PipeNetwork network = ((Pipe)entry.Value[0].Piece).PipeNetwork;
            if (member == null || emptied.Contains(entry.Key) || network.Atmosphere == null)
            {
                continue;
            }

            HashSet<long> gone = new HashSet<long>();
            double removedL = 0.0;
            foreach (PlannedTakedown takedown in entry.Value)
            {
                gone.Add(takedown.Piece.ReferenceId);
                removedL += PipeFamily.VolumeOf(takedown.Piece);
            }

            double? lowest = null;
            foreach (SmallGrid left in RunNetworks.PipeMembers(network))
            {
                if (left is Pipe pipe && !pipe.IsBeingDestroyed && !gone.Contains(pipe.ReferenceId))
                {
                    double rating = pipe.MaxPressure.ToDouble();
                    lowest = lowest.HasValue ? System.Math.Min(lowest.Value, rating) : rating;
                }
            }

            GasSnapshot before = GasSnapshot.Of(network.Atmosphere);
            double leftL = before.VolumeL - removedL;
            if (leftL <= 0.0)
            {
                continue;
            }

            GuardFinding? finding = RemovalRule.Squeeze(new NetworkSqueeze(entry.Key, before.PressureKpa(),
                before.WithVolume(leftL).PressureKpa(), removedL, leftL, lowest));
            if (finding != null)
            {
                plan.Add(finding, member.Index, member.Piece.ReferenceId);
            }
        }
    }

    // A from_id the request removes, or one inside something it removes (a stack in a locker it takes), would take the
    // refund with it: Refunds.Deliver puts it into the holder, which the job then destroys (structures-22).
    private static void HolderRemoved(RemovePlan plan, HashSet<long> removed)
    {
        if (plan.From == null || !plan.Arguments.From.HasValue)
        {
            return;
        }

        Thing? holder = plan.From;
        while (holder != null)
        {
            if (removed.Contains(holder.ReferenceId))
            {
                long id = holder.ReferenceId;
                PlannedTakedown? takedown = plan.Takedowns.Find(item => item.Piece.ReferenceId == id);
                plan.Problems.Add(new BuildIssueView("refund_holder_removed",
                    RemovalRule.HolderRemoved(Name(plan.From), holder == plan.From ? null : Name(holder)),
                    takedown?.Index, new ThingId(plan.From.ReferenceId)));
                return;
            }

            holder = holder is DynamicThing held ? held.ParentSlot?.Parent : null;
        }
    }

    private static string Name(Thing thing) => $"{thing.DisplayName} ({thing.PrefabName} {thing.ReferenceId})";

    // A device mounted on a face the piece holds, or standing on one, left with nothing to rest on once the request is
    // done (MountSupport): the faces a large piece holds (a wall's face; the six faces of a cell a frame fills), every
    // small-grid thing near them that rests on a surface (mounted on a face, or a grid-placed device standing on one;
    // cable, pipe and chute pieces run through cells and are left out), and for each face its back or bottom rests on
    // (MountRect), what holds that face now. Things the request removes too are skipped.
    private static string? Unsupported(Structure piece, HashSet<long> removed, GridFacts grid)
    {
        if (piece is SmallGrid)
        {
            return null;
        }

        List<Vec3> faces = new List<Vec3>();
        foreach (StructureSlot slot in StructureSlots.Live(piece))
        {
            List<GridPoint> held = FaceMath.TrySplitFace(slot.Point, out _, out _)
                ? new List<GridPoint> { slot.Point }
                : FaceMath.FacesOf(slot.Cell);
            foreach (GridPoint face in held)
            {
                faces.Add(new Vec3(face.X / 10.0, face.Y / 10.0, face.Z / 10.0));
            }
        }

        if (faces.Count == 0)
        {
            return null;
        }

        GridController world = GridController.World;
        foreach (NearBody body in NearBodies.Around(Box3.Around(faces), grid, removed, NearKinds.AnyPiece))
        {
            SmallGrid thing = body.Thing;
            MountRect? mount = RestingOn(thing, body);
            if (mount == null)
            {
                continue;
            }

            List<IReadOnlyCollection<long>> holders = new List<IReadOnlyCollection<long>>();
            foreach (GridCell face in mount.Faces())
            {
                holders.Add(FaceHolders(world, new GridPoint(face.X, face.Y, face.Z)));
            }

            if (MountSupport.Loses(holders, piece.ReferenceId, removed))
            {
                return $"{thing.DisplayName} ({thing.PrefabName} {thing.ReferenceId})";
            }
        }

        return null;
    }

    // The surface a small-grid thing rests on: mounted (its back) or a grid-placed device (its bottom); null for a
    // network piece or a thing whose back or bottom is on no face plane.
    private static MountRect? RestingOn(SmallGrid thing, NearBody body)
    {
        bool rests = thing.PlacementType == PlacementSnap.FaceMount ||
                     (thing.PlacementType == PlacementSnap.Grid && !(thing is Piping) && !(thing is Cable) &&
                      !(thing is Chute));
        Quaternion rotation = thing.ThingTransformRotation;
        CubeRotation? turn = CubeRotation.FromQuaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        if (!rests || turn == null || body.Cells.Count == 0)
        {
            return null;
        }

        return MountRect.Of(Box3.OfSmallCells(new List<GridCell>(body.Cells)),
            PlacementLayout.MountOutward(thing, turn), body.Render);
    }

    // Everything holding a face: the structures registered on it and those filling the cells on either side.
    private static List<long> FaceHolders(GridController grid, GridPoint face)
    {
        List<long> holders = new List<long>();
        foreach (Structure structure in new List<Structure>(grid.GetFaceStructures(StructureSlots.GridOf(face))))
        {
            if (structure != null && !structure.IsBeingDestroyed && !(structure is SmallGrid))
            {
                holders.Add(structure.ReferenceId);
            }
        }

        if (FaceMath.TrySplitFace(face, out GridPoint a, out GridPoint b))
        {
            foreach (GridPoint side in new[] { a, b })
            {
                AirBlocker? filler = CellBlocker(grid, side);
                if (filler.HasValue)
                {
                    holders.Add(filler.Value.Id);
                }
            }
        }

        return holders;
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
    // once, so two plates back to back on one face breach when both go, and neither alone. A face an earlier piece's
    // breach already named is left out (BreachedFaces.Unnamed): that opening is reported once.
    private static void Breach(Structure piece, RemovalFacts facts, HashSet<long> removed, BreachedFaces breached)
    {
        if (piece is SmallGrid || piece.CanAirPass)
        {
            return;
        }

        GridController grid = GridController.World;
        Dictionary<GridPoint, List<GridPoint>> opened = new Dictionary<GridPoint, List<GridPoint>>();
        foreach (StructureSlot slot in StructureSlots.Live(piece))
        {
            if (FaceMath.TrySplitFace(slot.Point, out GridPoint a, out GridPoint b))
            {
                if (!Sealed(grid, slot.Point, CellBlocker(grid, a), CellBlocker(grid, b), piece, removed))
                {
                    Open(opened, slot.Point, a);
                    Open(opened, slot.Point, b);
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
                    Open(opened, face, neighbour);
                }
            }
        }

        List<GridPoint> unnamed = breached.Unnamed(opened.Keys);
        List<GridPoint> sides = new List<GridPoint>();
        foreach (GridPoint face in unnamed)
        {
            foreach (GridPoint side in opened[face])
            {
                AddOnce(sides, side);
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
        if (!facts.BreachKpa.HasValue)
        {
            return;
        }

        facts.BreachWhere = string.Format(CultureInfo.InvariantCulture,
            "{0:0.#} kPa in the cell at {1}, {2:0.#} kPa in the cell at {3}", pressures[high],
            Describe(sides[high]), pressures[low], Describe(sides[low]));
        if (facts.BreachKpa.Value >= RemovalRule.BreachKpa)
        {
            breached.Claim(unnamed);
        }
    }

    private static void Open(Dictionary<GridPoint, List<GridPoint>> opened, GridPoint face, GridPoint side)
    {
        if (!opened.TryGetValue(face, out List<GridPoint> sides))
        {
            sides = new List<GridPoint>();
            opened[face] = sides;
        }

        AddOnce(sides, side);
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
                case "no_local_player":
                    // remove_structure gives the refund itself (refund_to and from_id, read in Source); the remove
                    // tool's planner runs here with its own refund off, so it needs no source.
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
