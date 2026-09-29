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

    /// <summary>
    /// Pipe networks that stay in one piece although the request removes pipe pieces of theirs (and maybe an in-line
    /// tank or passive vent too): every removed member leaves the network before it goes (as the pipe tools remove a
    /// piece that splits nothing), so the network keeps its id and all of its gas.
    /// </summary>
    internal HashSet<long> KeptWhole { get; } = new HashSet<long>();

    /// <summary>
    /// The gas the gas model forecasts the job deletes from each pipe network it takes members from, pipe pieces only
    /// included (TakedownOutcome.LostMol: holds_gas, lifted by allow_contents). The job's gas check expects it gone.
    /// </summary>
    internal List<PlannedGasLoss> GasLosses { get; } = new List<PlannedGasLoss>();

    internal bool Ready => Problems.Count == 0;

    /// <summary>Whether any piece is a pipe network member or has a pipe end (PipeContact).</summary>
    internal bool TouchesPipes =>
        Takedowns.Exists(static takedown => takedown.Kind?.Family is PipeFamily || PipeContact.Touches(takedown.Piece));

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

        List<NetworkRun> runs = NetworkRuns(plan);
        Dictionary<long, NetworkTakedown> gas = GasModel(plan);
        foreach (NetworkTakedown taken in gas.Values)
        {
            if (taken.Outcome.LostMol > 0.0)
            {
                plan.GasLosses.Add(PlannedGasLoss.Of(taken.Network, taken.Outcome.LostMol, taken.Outcome.MolesBefore));
            }
        }

        BreachedFaces breached = new BreachedFaces();
        GridFacts grid = new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());
        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            Guard(plan, takedown, seen, breached, gas, grid);
        }

        Squeeze(plan, gas);
        Divided(plan, gas);
        HolderRemoved(plan, seen);

        foreach (NetworkRun run in runs)
        {
            NetworkGuard(plan, run);
        }

        NetworkPieces(plan);
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
        BreachedFaces breached, Dictionary<long, NetworkTakedown> gas, GridFacts grid)
    {
        Structure piece = takedown.Piece;
        RemovalFacts facts = new RemovalFacts
        {
            BeingDestroyed = piece.IsBeingDestroyed,
            Indestructible = piece.Indestructable,
            Rocket = RocketParts.Of(piece).PartOfRocket,
            Broken = Wrecks.IsBroken(piece),
            GameRefusal = GameRefusal(piece),
            Mounted = MountedOn(piece) ?? Unsupported(piece, removed, grid),
            GasMoles = piece.InternalAtmosphere != null ? piece.InternalAtmosphere.TotalMoles.ToDouble() : 0.0,
            GasFate = piece is Tank ? GasFate.Released : GasFate.Lost
        };
        if (piece is Pipe { PipeNetwork: { } network } &&
            gas.TryGetValue(network.ReferenceId, out NetworkTakedown taken) && taken.First == takedown)
        {
            NetworkGas(taken, facts);
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
    // at full damage not yet swapped to its broken state (still at 0 or above) is on its way there. A burst pipe and a
    // burnt cable are wrecks too (Wrecks.IsBroken): neither gives anything back.
    private static List<ItemAmount> RefundOf(Structure piece) =>
        Wrecks.IsBroken(piece) ? new List<ItemAmount>() : BuildMaterials.RefundOf(piece);

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

    // Each pipe network the request takes anything from (an in-line tank, a passive vent, pipe pieces only), as the
    // job leaves it (PipeTakedown): its members, the game's links among them, the order the job removes them in (the
    // pipe pieces as their remove tool's plan lists them, then the others in reference_ids order), and whether they
    // leave it first (KeptWhole). Its findings go on its first member in the request that is not a pipe piece, else
    // on its first pipe piece.
    private static Dictionary<long, NetworkTakedown> GasModel(RemovePlan plan)
    {
        Dictionary<long, NetworkTakedown> model = new Dictionary<long, NetworkTakedown>();
        List<PlannedTakedown> reported = plan.Takedowns.FindAll(static takedown => takedown.Kind == null);
        reported.AddRange(plan.Takedowns.FindAll(static takedown => takedown.Kind?.Family is PipeFamily));
        foreach (PlannedTakedown takedown in reported)
        {
            if (!(takedown.Piece is Pipe { PipeNetwork: { } network }) || network.Atmosphere == null ||
                model.ContainsKey(network.ReferenceId))
            {
                continue;
            }

            GasSnapshot before = GasSnapshot.Of(network.Atmosphere);
            TakedownOutcome outcome = PipeTakedown.Run(Members(network, out Dictionary<long, SmallGrid> members),
                LinkSurvey.GameLinks(members, new HashSet<long>(members.Keys), new List<long>()),
                RemovalOrder(plan, network), plan.KeptWhole.Contains(network.ReferenceId), before.TotalMol());
            model[network.ReferenceId] = new NetworkTakedown(network.ReferenceId, before, outcome, takedown);
        }

        return model;
    }

    private static List<TakedownMember> Members(PipeNetwork network, out Dictionary<long, SmallGrid> members)
    {
        members = new Dictionary<long, SmallGrid>();
        List<TakedownMember> nodes = new List<TakedownMember>();
        foreach (SmallGrid member in RunNetworks.PipeMembers(network))
        {
            members[member.ReferenceId] = member;
            nodes.Add(new TakedownMember(member.ReferenceId, PipeFamily.VolumeOf(member),
                member is Pipe pipe ? pipe.MaxPressure.ToDouble() : (double?)null));
        }

        return nodes;
    }

    // RemoveWork's order: each run plan's pieces as RunBuilder.Remove takes them, then the other structures.
    private static List<long> RemovalOrder(RemovePlan plan, PipeNetwork network)
    {
        List<long> order = new List<long>();
        foreach (RunPlan runPlan in plan.NetworkPlans)
        {
            foreach (PlannedRemoval removal in runPlan.Removals)
            {
                if (!removal.Assumed && removal.Network != null && removal.Network.ReferenceId == network.ReferenceId)
                {
                    order.Add(removal.Piece.ReferenceId);
                }
            }
        }

        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            if (takedown.Kind == null && takedown.Piece is Pipe { PipeNetwork: { } its } && its == network)
            {
                order.Add(takedown.Piece.ReferenceId);
            }
        }

        return order;
    }

    // The gas the job deletes from a network it takes members from, reported on its first member in the request
    // (holds_gas, allow_contents): all of it when the request removes every member (Pipe.OnDestroy of the last one
    // divides it among nothing), or what a part of a split network holds when it loses its last member (the pipe
    // pieces go first, so a tank or a pipe piece the split leaves on its own goes with the gas it was given).
    private static void NetworkGas(NetworkTakedown taken, RemovalFacts facts)
    {
        TakedownOutcome outcome = taken.Outcome;
        if (outcome.LostMol < RemovalRule.GasFloorMol)
        {
            return;
        }

        facts.GasMoles += outcome.LostMol;
        facts.GasFate = GasFate.Lost;
        facts.GasWhere = outcome.Parts.Count == 0
            ? $" in pipe network {taken.Network}, whose last member this removal takes"
            : string.Format(CultureInfo.InvariantCulture,
                " of the {0:0.###} mol in pipe network {1}: the job's removals split the network (pipe pieces " +
                "first), and a part of it then loses its last member while it still holds gas",
                outcome.MolesBefore, taken.Network);
    }

    // An in-line tank or passive vent takes its volume with it while its network's gas stays (Pipe.OnDestroy hands the
    // whole mixture to what is left; structures-23: 605 mol N2 went from 5.5 MPa to 73.7 MPa in two pipes rated
    // 60.8 MPa). Each network left gets the gas the model gives it (all of it for a network kept whole, a share by
    // volume at each split of the job's order); over its weakest pipe the removal is refused as the pipe tools refuse
    // it (would_burst), naming the network left furthest over its rating.
    private static void Squeeze(RemovePlan plan, Dictionary<long, NetworkTakedown> gas)
    {
        foreach (NetworkTakedown taken in gas.Values)
        {
            double molesBefore = taken.Outcome.MolesBefore;
            GuardFinding? worst = null;
            double worstRatio = 0.0;
            foreach (TakedownPart part in taken.Outcome.Parts)
            {
                if (part.VolumeL <= 0.0 || part.Moles <= 0.0 || molesBefore <= 0.0 || !part.LowestKpa.HasValue)
                {
                    continue;
                }

                double after = taken.Before.WithVolume(part.VolumeL * molesBefore / part.Moles).PressureKpa();
                GuardFinding? finding = RemovalRule.Squeeze(new NetworkSqueeze(taken.Network,
                    taken.Before.PressureKpa(), after, taken.Outcome.RemovedL, part.VolumeL, part.LowestKpa,
                    taken.Outcome.Parts.Count));
                double ratio = after / part.LowestKpa.Value;
                if (finding != null && ratio > worstRatio)
                {
                    worst = finding;
                    worstRatio = ratio;
                }
            }

            if (worst != null)
            {
                plan.Add(worst, taken.First.Index, taken.First.Piece.ReferenceId);
            }
        }
    }

    // A network the job splits by taking an in-line tank or passive vent from it while it holds gas: its contents move
    // between the networks left, which remove_pipes refuses (contents_would_move) and so does this, unless
    // allow_contents (pipes-25). A split by pipe pieces alone is their remove tool's own finding (NetworkGuard).
    private static void Divided(RemovePlan plan, Dictionary<long, NetworkTakedown> gas)
    {
        foreach (NetworkTakedown taken in gas.Values)
        {
            if (taken.First.Kind != null)
            {
                continue;
            }

            List<double> shares = taken.Outcome.Parts.ConvertAll(static part => part.Moles);
            GuardFinding? finding = RemovalRule.Divided(taken.Network, taken.Outcome.MolesBefore, shares,
                plan.Arguments.Allow);
            if (finding != null)
            {
                plan.Add(finding, taken.First.Index, taken.First.Piece.ReferenceId);
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

    // The run tools' planner on each kind's pieces. A pipe network the request also takes an in-line tank or passive
    // vent from is planned apart, with those members forecast as gone (RunRemoval.Alongside), so its split and contents
    // checks see the network the whole job leaves. A pipe network the job neither splits nor empties is kept whole
    // (KeptWhole: RunBuilder.Remove and RemoveOne take each piece out of it first). The gas of every pipe network is
    // the gas model's (GasModel), so a pipe plan's would_burst and holds_contents are not reported again.
    private static List<NetworkRun> NetworkRuns(RemovePlan plan)
    {
        HashSet<long> withMembers = new HashSet<long>();
        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            if (takedown.Kind == null && takedown.Piece is Pipe { PipeNetwork: { } network })
            {
                withMembers.Add(network.ReferenceId);
            }
        }

        List<NetworkRun> runs = new List<NetworkRun>();
        foreach (RunKind kind in Kinds)
        {
            List<PlannedTakedown> pieces = plan.Takedowns.FindAll(takedown => takedown.Kind == kind);
            List<PlannedTakedown> shared = pieces.FindAll(takedown =>
                takedown.Piece is Pipe { PipeNetwork: { } network } && withMembers.Contains(network.ReferenceId));
            pieces.RemoveAll(shared.Contains);
            HashSet<long> sharedNetworks = new HashSet<long>();
            shared.ForEach(takedown => sharedNetworks.Add(((Pipe)takedown.Piece).PipeNetwork.ReferenceId));
            List<ThingId> alongside = plan.Takedowns
                .FindAll(takedown => takedown.Kind == null && takedown.Piece is Pipe { PipeNetwork: { } network } &&
                                     sharedNetworks.Contains(network.ReferenceId))
                .ConvertAll(takedown => new ThingId(takedown.Piece.ReferenceId));
            AddRun(plan, runs, kind, pieces, new List<ThingId>());
            AddRun(plan, runs, kind, shared, alongside);
        }

        foreach (NetworkRun run in runs)
        {
            if (!run.GasByModel || run.Plan.Forecast == null)
            {
                continue;
            }

            HashSet<long> rebuilt = RunBuilder.Rebuilt(run.Plan.Forecast, run.Kind.Family);
            foreach (PlannedRemoval removal in run.Plan.Removals)
            {
                if (!removal.Assumed && removal.Network != null && !rebuilt.Contains(removal.Network.ReferenceId))
                {
                    plan.KeptWhole.Add(removal.Network.ReferenceId);
                }
            }
        }

        return runs;
    }

    private static void AddRun(RemovePlan plan, List<NetworkRun> runs, RunKind kind, List<PlannedTakedown> pieces,
        List<ThingId> alongside)
    {
        if (pieces.Count == 0)
        {
            return;
        }

        List<ThingId> ids = pieces.ConvertAll(takedown => new ThingId(takedown.Piece.ReferenceId));
        RunRequest request = new RunRequest(kind, "remove_structure", null,
            new RunRemoval(ids, new List<GridCell>(), null, alongside),
            new RunOptions(EditAllowance.Nothing, null, false, RunArgs.DefaultListLimit));
        RunPlan runPlan = RunPlanner.Plan(request);
        plan.NetworkPlans.Add(runPlan);
        runs.Add(new NetworkRun(kind, pieces, runPlan, kind.Family is PipeFamily));
    }

    // A run plan's findings: its would_split warns here, contents refusals are lifted by allow_contents, anything else
    // refuses (cannot_remove only where no guard above already refused the piece).
    private static void NetworkGuard(RemovePlan plan, NetworkRun run)
    {
        RunKind kind = run.Kind;
        foreach (LayoutIssue issue in run.Plan.Problems)
        {
            int? index = IndexOf(run.Pieces, issue.Id);
            ThingId? id = issue.Id.HasValue ? new ThingId(issue.Id.Value) : (ThingId?)null;
            string message = $"{kind.RemoveTool}'s check: {issue.Message}";
            switch (issue.Code)
            {
                case "would_burst" when run.GasByModel:
                case UpgradeFamily.HoldsContents when run.GasByModel:
                    // The gas model's would_burst and holds_gas cover these networks, in the job's own order.
                    break;
                case "would_split":
                    plan.Warnings.Add(new BuildIssueView("would_split",
                        $"{kind.RemoveTool}'s check: {RemovalRule.SplitWarning(issue.Message)}", index, id));
                    break;
                case UpgradeFamily.HoldsContents:
                case "contents_would_move":
                    (plan.Arguments.Allow.Contents ? plan.Warnings : plan.Problems).Add(new BuildIssueView(
                        issue.Code, plan.Arguments.Allow.Contents
                            ? $"{kind.RemoveTool}'s check: {RemovalRule.AllowedContents(issue.Message)}"
                            : message + " (allow_contents)", index, id));
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

        foreach (LayoutIssue issue in run.Plan.Warnings)
        {
            plan.Warnings.Add(new BuildIssueView(issue.Code, $"{kind.RemoveTool}'s check: {issue.Message}",
                IndexOf(run.Pieces, issue.Id), issue.Id.HasValue ? new ThingId(issue.Id.Value) : (ThingId?)null));
        }
    }

    private static void NetworkPieces(RemovePlan plan)
    {
        foreach (RunKind kind in Kinds)
        {
            int count = plan.Takedowns.FindAll(takedown => takedown.Kind == kind).Count;
            if (count > 0)
            {
                plan.Warnings.Add(new BuildIssueView("network_piece",
                    $"{count} {kind.Noun} piece(s) are removed as {kind.RemoveTool} removes them (a network kept " +
                    $"whole keeps its id and contents); {kind.RemoveTool} also takes cells and waypoints."));
            }
        }
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

/// <summary>One run tool plan inside a remove_structure request, and whether the gas model owns its gas checks.</summary>
internal sealed class NetworkRun
{
    internal NetworkRun(RunKind kind, List<PlannedTakedown> pieces, RunPlan plan, bool gasByModel)
    {
        Kind = kind;
        Pieces = pieces;
        Plan = plan;
        GasByModel = gasByModel;
    }

    internal RunKind Kind { get; }

    internal List<PlannedTakedown> Pieces { get; }

    internal RunPlan Plan { get; }

    /// <summary>It removes pipe pieces, whose networks' gas the gas model forecasts (GasModel).</summary>
    internal bool GasByModel { get; }
}

/// <summary>A pipe network the request takes members from: its gas now and as the job leaves it.</summary>
internal sealed class NetworkTakedown
{
    internal NetworkTakedown(long network, GasSnapshot before, TakedownOutcome outcome, PlannedTakedown first)
    {
        Network = network;
        Before = before;
        Outcome = outcome;
        First = first;
    }

    internal long Network { get; }

    internal GasSnapshot Before { get; }

    internal TakedownOutcome Outcome { get; }

    /// <summary>
    /// The first of its members in the request that is not a pipe piece, else its first pipe piece: where its findings
    /// are reported.
    /// </summary>
    internal PlannedTakedown First { get; }
}
