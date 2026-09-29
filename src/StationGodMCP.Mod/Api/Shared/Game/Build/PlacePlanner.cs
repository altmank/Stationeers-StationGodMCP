#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Localization2;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Structures;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>One placement as planned: the prefab, where and how it stands, its state, look and cost.</summary>
internal sealed class PlannedPlacement
{
    internal PlannedPlacement(PlacementArgs args)
    {
        Args = args;
    }

    internal PlacementArgs Args { get; }

    internal int Index => Args.Index;

    internal Structure? Prefab { get; set; }

    internal Structure? Cursor { get; set; }

    internal CubeRotation? Turn { get; set; }

    internal Quaternion Rotation { get; set; } = Quaternion.identity;

    internal Vector3? Position { get; set; }

    /// <summary>at as resolved (a relative at read against the world, above_floor_m applied); null until read.</summary>
    internal Metres? At { get; set; }

    /// <summary>How at was read, for the reply.</summary>
    internal ResolvedAt? ResolvedAt { get; set; }

    /// <summary>above_floor_m's adjustment, for the reply; null without it.</summary>
    internal string? AboveFloorHow { get; set; }

    /// <summary>The floor above_floor_m measured from; null without it.</summary>
    internal double? FloorY { get; set; }

    /// <summary>How the point was set down on the surface behind it (the point as given was refused); null otherwise.</summary>
    internal string? SetDownHow { get; set; }

    /// <summary>A named facing as resolved: the axis and how it was read.</summary>
    internal string? ResolvedFacing { get; set; }

    internal string? ResolvedFacingHow { get; set; }

    /// <summary>The turn asked for: the rotation as given, or a named facing resolved to an axis.</summary>
    internal RotationSpec Spec { get; set; } = RotationSpec.Default;

    internal int? State { get; set; }

    /// <summary>The colour index to build it in; -1 for the prefab's own.</summary>
    internal int ColorIndex { get; set; } = -1;

    internal List<ItemAmount> Cost { get; } = new List<ItemAmount>();

    /// <summary>Its cable, pipe and chute ports where it would stand; null for anything but a device-like thing.</summary>
    internal List<SurveyPortView>? Ports { get; set; }

    /// <summary>orient's search result; null when the turn was given.</summary>
    internal OrientResultView? Orient { get; set; }

    /// <summary>An earlier placement of the request that puts the frame this one needs below it; null otherwise.</summary>
    internal PlannedPlacement? SupportedBy { get; set; }

    /// <summary>The layout preview at the planned position and turn; null until resolved that far.</summary>

    internal LayoutPreview? Layout { get; set; }

    /// <summary>Resolved far enough to be built: prefab, cursor, position, rotation and state.</summary>

    internal bool Resolved => Prefab != null && Cursor != null && Position.HasValue && State.HasValue;
}

/// <summary>A place_structure run as planned: every placement, the source, the stocks, problems and warnings.</summary>
internal sealed class PlacePlan
{
    internal PlacePlan(PlaceArguments arguments)
    {
        Arguments = arguments;
    }

    internal PlaceArguments Arguments { get; }

    internal List<PlannedPlacement> Placements { get; } = new List<PlannedPlacement>();

    internal List<BuildIssueView> Problems { get; } = new List<BuildIssueView>();

    internal List<BuildIssueView> Warnings { get; } = new List<BuildIssueView>();

    /// <summary>Where materials come from; null when free or when there is none (a problem says so).</summary>
    internal Thing? From { get; set; }

    internal ulong Owner { get; set; }

    internal Dictionary<int, Item> Items { get; } = new Dictionary<int, Item>();

    internal List<ItemStock> Stocks { get; } = new List<ItemStock>();

    internal bool Ready => Problems.Count == 0;

    /// <summary>Whether any placement is a pipe network member or has a pipe end (PipeContact).</summary>
    internal bool TouchesPipes =>
        Placements.Exists(static placement => placement.Prefab != null && PipeContact.Touches(placement.Prefab));

    /// <summary>The grid as the layout checks read it, for the whole run.</summary>
    internal GridFacts Facts { get; } =
        new GridFacts(new CableRunKind(), SmallGridBlock.None, new HashSet<long>());

    internal void Problem(string code, string message, int? index = null) =>
        Problems.Add(new BuildIssueView(code, message, index));

    internal void Warn(string code, string message, int? index = null)
    {
        if (!Warnings.Exists(warning => warning.Code == code && warning.Message == message && warning.Index == index))
        {
            Warnings.Add(new BuildIssueView(code, message, index));
        }
    }
}

/// <summary>
/// place_structure's whole preflight; nothing here changes the game. Each placement: the prefab (a loaded structure
/// some kit builds), the turn (refused where the cursor cannot turn a grid-placed piece that way), the position as
/// the cursor snaps it, the game's own cursor check, the slot a small-grid piece takes, the build state, label and
/// colour, and the cost: the build states up to the chosen one (MaterialRule; state 0 is the kit). Materials come
/// from the source as a kit's placement takes them, or not at all with free in a creative world.
/// </summary>
internal static class PlacePlanner
{
    // The end types grid_survey lists as ports.
    private const int PortTypes = (int)(NetworkType.PowerAndData | NetworkType.Pipe | NetworkType.PipeLiquid |
                                        NetworkType.Chute);

    internal static PlacePlan Plan(PlaceArguments arguments)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host builds.");
        }

        if (GridController.World == null)
        {
            throw ApiErrors.Refused("not_ready", "The world grid is not loaded.");
        }

        PlacePlan plan = new PlacePlan(arguments);
        string? free = PlacementRule.FreeRefusal(arguments.Free, WorldManager.IsCreative());
        if (free != null)
        {
            plan.Problem("not_creative", $"free: {free}.");
        }

        Source(plan);
        BuildCatalogue catalogue = BuildCatalogue.Load();
        List<ColorSwatch> swatches = Singleton<GameManager>.Instance != null
            ? Singleton<GameManager>.Instance.CustomColors
            : new List<ColorSwatch>();
        foreach (PlacementArgs args in arguments.Placements)
        {
            PlannedPlacement placement = new PlannedPlacement(args);
            plan.Placements.Add(placement);
            Resolve(plan, catalogue, swatches, placement);
        }

        Overlaps(plan);
        Clashes(plan);
        CountMaterials(plan);
        return plan;
    }

    /// <summary>The cursor check again for one resolved placement, as things stand now (the job's step before it).</summary>
    internal static string? Recheck(PlannedPlacement placement)
    {
        string? refusal = Check(placement.Prefab!, placement.Cursor!, placement.Position!.Value, placement.Rotation);
        return refusal == null
            ? null
            : refusal + BrokenNote(placement.Prefab!, placement.Position!.Value, placement.Rotation) +
              FrameNote(placement.Position!.Value, refusal);
    }

    private static string? Check(Structure prefab, Structure cursor, Vector3 position, Quaternion rotation)
    {
        HashSet<long> none = new HashSet<long>();
        string? refusal = CursorCheck.Refusal(cursor, position, rotation, none);
        if (refusal != null || !(prefab is SmallGrid piece))
        {
            return refusal;
        }

        Grid3[] cells = CursorCheck.SmallCells(prefab, position, rotation);
        return CursorCheck.RocketCell(cells) ?? CursorCheck.SlotTaken(piece, cells, none);
    }

    // A broken structure still takes its cells and slots (the game only swaps its mesh), so a placement there is
    // refused like any other; say which one and how to clear it.
    private static string BrokenNote(Structure prefab, Vector3 position, Quaternion rotation)
    {
        GridController world = GridController.World;
        if (world == null)
        {
            return string.Empty;
        }

        List<Structure> near = new List<Structure>();
        foreach (Grid3 grid in CursorCheck.SmallCells(prefab, position, rotation))
        {
            SmallCell? cell = world.GetSmallCell(grid);
            if (cell != null)
            {
                AddBroken(near, cell.Chute, cell.Pipe, cell.Device, cell.Cable, cell.Other, cell.Rail as Structure);
            }
        }

        Cell? large = world.GetCell(position);
        if (large?.AllStructures != null)
        {
            AddBroken(near, large.AllStructures.ToArray());
        }

        if (near.Count == 0)
        {
            return string.Empty;
        }

        List<string> names = near.ConvertAll(broken =>
            $"{broken.DisplayName} ({broken.PrefabName} {broken.ReferenceId})");
        return $" (broken there: {string.Join(", ", names)}; a broken structure still takes its place; remove it " +
               "first with remove_structure allow_broken)";
    }

    /// <summary>
    /// The game's "requires a Frame below" (SmallGrid.HasFrameBelow: Battery, MountedSmallGrid, LargeElectrical,
    /// radiators) also refuses a spot inside a frame: the cell the piece stands in must be clear (IsBlockedGrid) and the
    /// cell under it hold a frame. When a frame fills the spot's own cell, say so and where the piece would stand;
    /// empty otherwise.
    /// </summary>
    internal static string FrameNote(Vector3 position, string refusal)
    {
        GridController world = GridController.World;
        if (world == null || !refusal.StartsWith(RequiresFrame, System.StringComparison.Ordinal))
        {
            return string.Empty;
        }

        Objects.Structures.Frame? frame = world.GetCell(position)?.Lookup[StructureElement.Center] as Objects.Structures.Frame;
        return frame == null || frame.IsBeingDestroyed
            ? string.Empty
            : $" (the spot is inside {frame.DisplayName} ({frame.PrefabName} {frame.ReferenceId}); the game wants the " +
              "cell it stands in free and a frame in the cell under it: set it on top of the frame, 2 m higher)";
    }

    private static void AddBroken(List<Structure> found, params Structure?[] structures)
    {
        foreach (Structure? structure in structures)
        {
            if (structure != null && structure.IsBroken && !structure.IsBeingDestroyed && !found.Contains(structure))
            {
                found.Add(structure);
            }
        }
    }

    private static void Resolve(PlacePlan plan, BuildCatalogue catalogue, List<ColorSwatch> swatches,
        PlannedPlacement placement)
    {
        int index = placement.Index;
        Structure? prefab = catalogue.Find(placement.Args.Prefab, out string? issue);
        if (prefab == null)
        {
            plan.Problem("invalid_prefab", issue!, index);
            return;
        }

        placement.Prefab = prefab;
        placement.State = placement.Args.State.Resolve(prefab.BuildStates.Count, out string? stateError);
        if (stateError != null)
        {
            plan.Problem("invalid_build_state", $"{prefab.PrefabName}: {stateError}.", index);
        }

        NetworkPieceNote(plan, prefab, index);
        if (!ReadAt(plan, placement))
        {
            return;
        }

        if (placement.Args.AboveFloorM.HasValue && !FloorFound(plan, placement))
        {
            return;
        }

        if (placement.Args.Orient == null && !Turn(plan, prefab, placement))
        {
            return;
        }

        Structure? cursor = catalogue.CursorOf(prefab);
        if (cursor == null)
        {
            plan.Problem("no_cursor", $"The game has no placement cursor for {prefab.PrefabName} (they are made " +
                                      "when a player's inventory loads; a dedicated server has none).", index);
            return;
        }

        placement.Cursor = cursor;
        if (placement.Args.Orient != null && !Orient(plan, prefab, cursor, placement))
        {
            return;
        }

        if (placement.Args.AboveFloorM.HasValue)
        {
            AboveFloor(plan, placement, cursor, placement.Args.AboveFloorM.Value);
        }

        Vector3 position = Aim(plan, placement, cursor, out string? refusal);
        placement.Position = position;
        AboveFloorResult(placement, position);
        placement.Ports = PortPreview(prefab, position, placement.Rotation);
        if (placement.SupportedBy != null)
        {
            plan.Warn("supported_by_placement",
                $"{prefab.PrefabName} at {Describe(position)}: the game wants a frame below it for support, which " +
                $"placements[{placement.SupportedBy.Index}] ({placement.SupportedBy.Prefab!.PrefabName}) puts there; " +
                "the job checks it again once that stands.", index);
        }

        if (refusal != null)
        {
            plan.Problem("cannot_place",
                $"{prefab.PrefabName} at {Describe(position)}: {refusal}{BrokenNote(prefab, position, placement.Rotation)}" +
                $"{FrameNote(position, refusal)}.",
                index);
        }

        Layout(plan, placement, position);

        Look(plan, swatches, placement);
        if (!plan.Arguments.Free && placement.State.HasValue)
        {
            placement.Cost.AddRange(BuildMaterials.Amounts(prefab, placement.State.Value, plan.Items));
        }
    }

    // at and a named facing read against the world (AtResolver); a refusal there is the placement's problem.
    private static bool ReadAt(PlacePlan plan, PlannedPlacement placement)
    {
        string name = plan.Arguments.Placements.Count > 1 ? $"placements[{placement.Index}].at" : "at";
        try
        {
            ResolvedAt at = AtResolver.Resolve(placement.Args.At, plan.Facts, name);
            placement.ResolvedAt = at;
            placement.At = at.Point;
            placement.Spec = placement.Args.Rotation;
            NamedFacing? named = placement.Args.NamedFacing;
            if (named != null)
            {
                GridStep facing = AtResolver.Facing(named, at, plan.Facts, name.Replace(".at", ".facing"),
                    out string how);
                placement.Spec = new RotationSpec.Facing(facing, named.Up);
                placement.ResolvedFacing = facing.Name;
                placement.ResolvedFacingHow = how;
            }

            return true;
        }
        catch (ApiException refusal)
        {
            plan.Problem(refusal.Code, refusal.Message, placement.Index);
            return false;
        }
    }

    // above_floor_m measures from the floor below at (AtResolver.FloorBelow); with none within reach the placement is
    // refused rather than measured from an invented one (structures-24).
    private static bool FloorFound(PlacePlan plan, PlannedPlacement placement)
    {
        Metres given = placement.ResolvedAt!.Point;
        double? floor = AtResolver.FloorBelow(given, plan.Facts);
        if (floor.HasValue)
        {
            placement.FloorY = floor.Value;
            return true;
        }

        plan.Problem("cannot_place", string.Format(CultureInfo.InvariantCulture,
            "above_floor_m: no floor below ({0:0.##}, {1:0.##}, {2:0.##}) within {3:0} m (no floor plate on a plane " +
            "down to y {4:0}, and no frame under one); give at as a point instead.", given.X, given.Y, given.Z,
            AtResolver.FloorSearchM,
            AtResolver.FirstPlaneBelow(given) - AtResolver.FloorSearchM + 2.0), placement.Index);
        return false;
    }

    // above_floor_m: its bottom (the bottom of the box its meshes fill, what stands on the floor) that high over the
    // floor below at (FloorFound). Aimed once at that height, then moved by what the bottom missed it by; the cursor
    // snaps that to 0.5 m, so the snap below and above are tried too and the one whose bottom lands nearest the height
    // asked is kept (structures-24: 1.3 m gave 1.0 m). The small cells' box is not the measure: a standing device's
    // cells sit a quarter metre below its mesh, so aiming them at the floor lifts the device off it.
    private static void AboveFloor(PlacePlan plan, PlannedPlacement placement, Structure cursor, double above)
    {
        Metres given = placement.ResolvedAt!.Point;
        double target = placement.FloorY!.Value + above;
        placement.At = new Metres(given.X, target, given.Z);
        Vector3 first = Aim(plan, placement, cursor, out _);
        double aim = target + (target - Bodies.RenderBox(placement.Prefab!, first, placement.Rotation).Min.Y);
        double best = aim;
        double bestMiss = double.MaxValue;
        foreach (double nudge in new[] { 0.0, -0.5, 0.5 })
        {
            placement.At = new Metres(given.X, aim + nudge, given.Z);
            Vector3 tried = Aim(plan, placement, cursor, out string? refusal);
            double miss = System.Math.Abs(Bodies.RenderBox(placement.Prefab!, tried, placement.Rotation).Min.Y - target);
            if (refusal == null && miss < bestMiss - 0.001)
            {
                best = aim + nudge;
                bestMiss = miss;
            }
        }

        placement.At = new Metres(given.X, best, given.Z);
        placement.SetDownHow = null;
    }

    // above_floor_m in the reply: where its bottom ended up, which the cursor's 0.5 m snap may put off the height asked.
    private static void AboveFloorResult(PlannedPlacement placement, Vector3 position)
    {
        if (!placement.FloorY.HasValue || !placement.Args.AboveFloorM.HasValue)
        {
            return;
        }

        double floor = placement.FloorY.Value;
        double above = Bodies.RenderBox(placement.Prefab!, position, placement.Rotation).Min.Y - floor;
        double asked = placement.Args.AboveFloorM.Value;
        placement.AboveFloorHow = string.Format(CultureInfo.InvariantCulture,
            "; its bottom {0:0.##} m above the floor at y {1:0.##}", above, floor) +
            (System.Math.Abs(above - asked) > 0.01
                ? string.Format(CultureInfo.InvariantCulture, " ({0:0.##} m asked; the cursor snaps to 0.5 m)", asked)
                : string.Empty);
    }

    // orient: every turn the cursor can give the prefab, aimed and checked as a plain placement would be, scored
    // against the intent (OrientSearch) with the layout preview's conflicts; the best is the placement's turn. A
    // device whose flow a logic Mode reverses is scored both ways, and a Mode write is suggested when that wins.
    private static bool Orient(PlacePlan plan, Structure prefab, Structure cursor, PlannedPlacement placement)
    {
        OrientIntent intent;
        try
        {
            intent = Orienter.Read(placement.Args.Orient!);
        }
        catch (ApiException refusal)
        {
            plan.Problem(refusal.Code, refusal.Message, placement.Index);
            return false;
        }
        System.Func<Vec3, GridStep, bool> roomAhead = Orienter.RoomAhead(plan.Facts);
        bool reversible = Orienter.ReversibleFlow(prefab) && (intent.FlowFrom != null || intent.FlowTo != null);
        List<OrientScore> scores = new List<OrientScore>();
        foreach (CubeRotation turn in CubeRotation.All)
        {
            if (!CursorAllows(prefab, turn))
            {
                continue;
            }

            SetTurn(placement, turn);
            if (placement.Args.AboveFloorM.HasValue)
            {
                AboveFloor(plan, placement, cursor, placement.Args.AboveFloorM.Value);
            }

            Vector3 position = Aim(plan, placement, cursor, out string? refusal);
            LayoutPreview layout = PlacementLayout.Of(prefab, position, placement.Rotation, turn, plan.Facts,
                plan.Arguments.AllowDoorKeepOut, new HashSet<long>());
            OrientCandidate candidate = new OrientCandidate(turn, PlacementLayout.MountOutward(prefab, turn).Opposite,
                turn.Forward, Bodies.V(position),
                Orienter.Ports(prefab, position, placement.Rotation, PortTypes), refusal, layout.Penalty,
                Uprightness.Problem(turn, VisualUp.Of(prefab.PrefabName).LocalUp));
            scores.Add(OrientSearch.Score(candidate, intent, roomAhead));
            if (reversible)
            {
                scores.Add(OrientSearch.Score(candidate, intent, roomAhead, true));
            }
        }

        List<OrientScore> ranked = OrientSearch.Rank(scores);
        OrientScore? best = ranked.Count > 0 && !ranked[0].Excluded ? ranked[0] : null;
        List<OrientChoiceView> alternatives = new List<OrientChoiceView>();
        for (int index = 1; index < ranked.Count && alternatives.Count < 3; index++)
        {
            alternatives.Add(Orienter.ViewOf(ranked[index]));
        }

        ModeFlipView? flip = best != null && best.ReversedFlow
            ? new ModeFlipView("Mode", 1, $"{prefab.PrefabName} moves gas the other way with Mode 1 (Left); the " +
                                          "chosen turn meets the flow only so. Write it after building (write_logic).")
            : null;
        placement.Orient = new OrientResultView(best != null ? Orienter.ViewOf(best) : null, alternatives,
            ranked.Count, flip);
        if (best == null)
        {
            plan.Problem("no_orientation", $"orient: no turn of {prefab.PrefabName} can be built there as asked " +
                                           $"({ranked.Count} tried); see orient.alternatives.", placement.Index);
            return false;
        }

        SetTurn(placement, best.Candidate.Turn);
        return true;
    }

    /// <summary>
    /// Whether place_structure accepts the turn: a grid-placed prefab only as its cursor turns it (a piece the cursor
    /// turns itself, a cable or pipe, any turn); a face-placed or mounted one any turn, the cursor check deciding.
    /// </summary>
    internal static bool CursorAllows(Structure prefab, CubeRotation turn)
    {
        RotationAxis axes = prefab.RotationAxis;
        return prefab.PlacementType != PlacementSnap.Grid || prefab is ISmartRotatable ||
               PlacementRule.CursorCanTurn(turn, (axes & RotationAxis.X) != 0, (axes & RotationAxis.Y) != 0,
                   (axes & RotationAxis.Z) != 0, prefab is IMounted);
    }

    private static void SetTurn(PlannedPlacement placement, CubeRotation turn)
    {
        placement.Turn = turn;
        (double x, double y, double z, double w) = turn.ToQuaternion();
        placement.Rotation = new Quaternion((float)x, (float)y, (float)z, (float)w);
    }

    // Where the cursor would put the piece aimed at `at`, and why it would not build there. The cursor snaps the point
    // its ray lands on, which is a surface: a small-grid device aimed at a floor stands on the floor plane, a mounted one
    // on the face it mounts to. So when the point as given cannot be built, a small-grid device is tried again set down
    // on the surface behind it (CursorAim): along its down for a grid-placed device, along its back for a mounted one.
    // Only onto a surface that is there (a plate on that face, or a frame behind it): a ray through open air lands on
    // nothing, and an occupied spot is not moved into the air below it. A point as given that can be built is kept; a
    // set-down is named in SetDownHow.
    private static Vector3 Aim(PlacePlan plan, PlannedPlacement placement, Structure cursor, out string? refusal)
    {
        GridFacts facts = plan.Facts;
        Structure prefab = placement.Prefab!;
        Metres at = placement.At!.Value;
        placement.SetDownHow = null;
        Vector3 given = CursorCheck.Snap(cursor, new Vector3((float)at.X, (float)at.Y, (float)at.Z),
            placement.Rotation);
        refusal = Check(prefab, cursor, given, placement.Rotation);
        placement.SupportedBy = refusal != null ? PlannedSupport(plan, placement, given, refusal) : null;
        if (placement.SupportedBy != null)
        {
            refusal = null;
            return given;
        }

        GridStep? away = placement.Turn == null ? null : AwayFromSurface(prefab, placement.Turn);
        if (refusal == null || !away.HasValue)
        {
            return given;
        }

        (double x, double y, double z) = CursorAim.OntoFloor(at.X, at.Y, at.Z, away.Value);
        Vector3 surface = CursorCheck.Snap(cursor, new Vector3((float)x, (float)y, (float)z), placement.Rotation);
        if (surface == given)
        {
            return given;
        }

        if (!SurfaceThere(new Vec3(x, y, z), away.Value, facts))
        {
            refusal = $"{refusal}; nothing to set it down on behind it (no plate on the face at " +
                      $"{Describe(new Vector3((float)x, (float)y, (float)z))} and no frame behind that face)";
            return given;
        }

        string? surfaceRefusal = Check(prefab, cursor, surface, placement.Rotation);
        if (surfaceRefusal == null)
        {
            placement.SetDownHow = $"; set down on the surface behind it at {Describe(surface)} (as given at " +
                                   $"{Describe(given)}: {refusal})";
            refusal = null;
            return surface;
        }

        refusal = $"{refusal}; set down on the surface at {Describe(surface)}: {surfaceRefusal}";
        return given;
    }

    // "Requires a Frame below" (SmallGrid.HasFrameBelow: a structure that allows mounting in the cell half the piece's
    // grid size below it) where an earlier placement of this request puts that structure: it does not stand during
    // the preflight, so the job's own check before each placement (Recheck, once the earlier ones stand) decides.
    private static PlannedPlacement? PlannedSupport(PlacePlan plan, PlannedPlacement placement, Vector3 position,
        string refusal)
    {
        if (refusal != RequiresFrame || !(placement.Prefab is SmallGrid piece))
        {
            return null;
        }

        Grid3 cell = new WorldGrid(position - placement.Rotation * Vector3.up * (piece.GridSize / 2f));
        foreach (PlannedPlacement earlier in plan.Placements)
        {
            if (ReferenceEquals(earlier, placement))
            {
                break;
            }

            if (earlier.Prefab != null && !(earlier.Prefab is SmallGrid) && earlier.Prefab.AllowMounting &&
                earlier.Position.HasValue && ((Grid3)new WorldGrid(earlier.Position.Value)).Equals(cell))
            {
                return earlier;
            }
        }

        return null;
    }

    private static string RequiresFrame => Text.Plain(GameStrings.PlacementRequiresFrame.DisplayString);

    // A surface on the face plane a point lies on, facing away: a structure on that face (a floor or wall plate) or a
    // frame filling the 2 m cell behind it.
    private static bool SurfaceThere(Vec3 onPlane, GridStep away, GridFacts facts)
    {
        GridCell behind = LargeCells.Containing(onPlane - Vec3.Of(away) * 1.0);
        GridCell face = away.From(behind, SmallCellCode.Large / 2 / GridStep.CellSize);
        return facts.FrameAt(behind) != null || facts.FaceStructuresAt(face).Exists(structure => !Openings.IsDoor(structure));
    }

    // Which way a small-grid device the cursor sets down on a surface points away from it: its up when grid-placed,
    // its forward when mounted; null for cable, pipe and chute pieces (laid through any cell), face-placed pieces and 2 m
    // devices (those snap to their cell's centre).
    private static GridStep? AwayFromSurface(Structure prefab, CubeRotation turn)
    {
        if (!(prefab is SmallGrid) || NetworkToolOf(prefab) != null || prefab.GridSize >= CursorAim.LargeCell)
        {
            return null;
        }

        return prefab.PlacementType switch
        {
            PlacementSnap.Grid => turn.Up,
            PlacementSnap.FaceMount => turn.Forward,
            _ => null
        };
    }

    // The turn asked for; a grid-placed piece only as the cursor turns it (its RotationAxis, 90 degrees, 180 about x
    // for a mounted piece). A piece the cursor turns itself (ISmartRotatable: cables, pipes) is only warned.
    private static bool Turn(PlacePlan plan, Structure prefab, PlannedPlacement placement)
    {
        int index = placement.Index;
        if (placement.Spec is RotationSpec.OnFace && prefab.PlacementType != PlacementSnap.Face)
        {
            plan.Problem("invalid_rotation", $"{prefab.PrefabName} is not placed on a cell face; use facing or " +
                                             "rotation instead of face.", index);
            return false;
        }

        CubeRotation? turn = placement.Spec.Resolve(out string? error);
        if (turn == null)
        {
            plan.Problem("invalid_rotation", error ?? "The rotation cannot be read.", index);
            return false;
        }

        placement.Turn = turn;
        (double x, double y, double z, double w) = turn.ToQuaternion();
        placement.Rotation = new Quaternion((float)x, (float)y, (float)z, (float)w);
        if (prefab.PlacementType != PlacementSnap.Grid)
        {
            return true;
        }

        RotationAxis axes = prefab.RotationAxis;
        bool turns = PlacementRule.CursorCanTurn(turn, (axes & RotationAxis.X) != 0, (axes & RotationAxis.Y) != 0,
            (axes & RotationAxis.Z) != 0, prefab is IMounted);
        if (turns)
        {
            return true;
        }

        string message = axes == RotationAxis.None
            ? $"{prefab.PrefabName}'s cursor does not turn it (it has no rotation axes), so it cannot stand as {turn}"
            : $"{prefab.PrefabName}'s cursor turns it only about {axes}, which never gives {turn}";
        if (prefab is ISmartRotatable)
        {
            plan.Warn("unusual_rotation", message + "; kept, since the cursor's autoplace may turn it so.", index);
            return true;
        }

        plan.Problem("invalid_rotation", message + ".", index);
        return false;
    }

    // The cable, pipe and chute ports of a device, or of another thing with ends that is not a network piece (an in-line
    // tank, a passive vent), as they would stand there: the ends of the prefab turned and moved there (PieceShapes.Placed,
    // Connection.SetGrids' way), in grid_survey's shape. Null for network pieces and things without ends.
    private static List<SurveyPortView>? PortPreview(Structure prefab, Vector3 position, Quaternion rotation)
    {
        if (!(prefab is SmallGrid) || NetworkToolOf(prefab) != null)
        {
            return null;
        }

        PieceModel? model = PieceShapes.Placed(prefab, position, rotation, 0);
        return model?.Ends.Count > 0
            ? PortCells.Of(model.Ends, PortTypes).ConvertAll(port => new SurveyPortView(port.Index,
                GameLookup.ViewOf(PieceShapes.CentreOf(port.Cell)), port.Toward?.Name ?? "?",
                ((NetworkType)port.Type).ToString(), ((ConnectionRole)port.Role).ToString(), null))
            : null;
    }

    // The layout preview (PlacementLayout): its view on the placement, its problems (in_door_keepout unless allowed)
    // and warnings on the plan; info findings stay in the view only.
    private static void Layout(PlacePlan plan, PlannedPlacement placement, Vector3 position)
    {
        LayoutPreview layout;
        try
        {
            layout = PlacementLayout.Of(placement.Prefab!, position, placement.Rotation, placement.Turn, plan.Facts,
                plan.Arguments.AllowDoorKeepOut, new HashSet<long>());
        }
        catch (System.Exception exception) when (!(exception is ApiException))
        {
            // The preview reads prefab data (bounds, ends) a modded prefab may not have: the placement itself stays
            // checked by the game's cursor, and the failure is named.
            StationGodMod.LogWarning($"Layout preview of {placement.Prefab!.PrefabName} failed: {exception}");
            plan.Warn("layout_preview_failed", $"The layout preview could not be made: {exception.Message}",
                placement.Index);
            return;
        }

        placement.Layout = layout;
        foreach (LayoutConflict conflict in layout.Conflicts)
        {
            if (conflict.Level == ConflictLevel.Problem)
            {
                plan.Problem(conflict.Code, conflict.Message, placement.Index);
            }
            else if (conflict.Level == ConflictLevel.Warning)
            {
                plan.Warn(conflict.Code, conflict.Message, placement.Index);
            }
        }
    }

    // The place tool of a cable, pipe or chute piece; null for anything else (devices on those networks included).
    private static string? NetworkToolOf(Structure prefab) =>
        new CableFamily().IsPiece(prefab) ? "place_cables"
        : new PipeFamily().IsPiece(prefab) ? "place_pipes"
        : new ChuteFamily().IsPiece(prefab) ? "place_chutes"
        : null;

    private static void NetworkPieceNote(PlacePlan plan, Structure prefab, int index)
    {
        string? tool = NetworkToolOf(prefab);
        if (tool != null)
        {
            plan.Warn("network_piece",
                $"{prefab.PrefabName} is a network piece; this places exactly it and the game joins whatever its " +
                $"ends touch, with no would_bridge check. {tool} picks pieces by their connections and guards " +
                "merges.", index);
        }
    }

    private static void Look(PlacePlan plan, List<ColorSwatch> swatches, PlannedPlacement placement)
    {
        Structure prefab = placement.Prefab!;
        int index = placement.Index;
        if (placement.Args.Label != null && !Labels.CanRename(prefab))
        {
            plan.Problem("not_labelable", LabelRule.NotLabelable(prefab.PrefabName, prefab.GetType().Name), index);
        }

        string? color = placement.Args.Color;
        if (color == null)
        {
            return;
        }

        if (prefab.HasColorState || !prefab.IsPaintable || prefab.PaintableMaterial == null ||
            prefab.structureRenderMode != StructureRenderMode.Standard)
        {
            plan.Problem("not_paintable", $"{prefab.PrefabName} cannot be built in a colour (no paintable " +
                                          "material, an animator colour, or a batched structure).", index);
            return;
        }

        ApiException? refusal = PaintColor.Resolve(color, prefab, swatches, out int colorIndex);
        if (refusal != null)
        {
            plan.Problem(refusal.Code, refusal.Message, index);
            return;
        }

        placement.ColorIndex = colorIndex;
    }

    // Two placements at one spot in one slot: the cursor check sees only what stands now, so the job's own check
    // before each build would stop the run at the second one.
    private static void Overlaps(PlacePlan plan)
    {
        Dictionary<PlacementSpot, int> seen = new Dictionary<PlacementSpot, int>();
        foreach (PlannedPlacement placement in plan.Placements)
        {
            if (!placement.Resolved)
            {
                continue;
            }

            PlacementSpot key = SpotOf(placement);
            if (seen.TryGetValue(key, out int first))
            {
                plan.Problem("overlaps_placement",
                    $"It stands where placement {first} stands, in the same slot.", placement.Index);
            }
            else
            {
                seen[key] = placement.Index;
            }
        }
    }

    // Placements of the run that the game would refuse once an earlier one stands (PlanClashes): a shared slot of a
    // small cell, or a 2 m cell a frame fills and another piece registers in. Placements already refused are left out.
    private static void Clashes(PlacePlan plan)
    {
        List<PlannedFootprint> footprints = new List<PlannedFootprint>();
        foreach (PlannedPlacement placement in plan.Placements)
        {
            if (placement.Resolved && !plan.Problems.Exists(problem => problem.Index == placement.Index))
            {
                footprints.Add(FootprintOf(placement));
            }
        }

        foreach (PlanClash clash in PlanClashes.Find(footprints))
        {
            plan.Problem("overlaps_placement", $"The game would refuse it once earlier pieces of this run stand: " +
                                               $"{clash.Message}.", clash.Index);
        }
    }

    private static PlannedFootprint FootprintOf(PlannedPlacement placement)
    {
        Structure prefab = placement.Prefab!;
        Vector3 position = placement.Position!.Value;
        Quaternion rotation = placement.Rotation;
        if (prefab is SmallGrid piece)
        {
            return PlannedFootprint.Small(placement.Index, prefab.PrefabName,
                Bodies.SmallCells(prefab, position, rotation), CursorCheck.SlotName(piece));
        }

        List<GridCell> cells = prefab.PlacementType == PlacementSnap.Grid
            ? Bodies.LargeCells(prefab, position, rotation)
            : new List<GridCell>();
        if (prefab.PlacementType != PlacementSnap.Grid)
        {
            foreach (StructureSlot slot in StructureSlots.Predicted(prefab, position, rotation) ??
                                           new List<StructureSlot>())
            {
                cells.Add(new GridCell(slot.Cell.X, slot.Cell.Y, slot.Cell.Z));
            }
        }

        if (prefab is Wall)
        {
            // Wall.IsSideBlocked: a wall is refused when a frame fills the cell it faces into.
            Vector3 front = position + rotation * Vector3.forward;
            cells.Add(LargeCells.Containing(new Vec3(front.x, front.y, front.z)));
        }

        return PlannedFootprint.Large(placement.Index, prefab.PrefabName, cells,
            prefab.StructureCollisionType == CollisionType.BlockGrid);
    }

    // The slot a placement takes, as the game's slots tell pieces apart: a face-placed piece takes one side of its
    // face (two plates back to back are two slots).
    private static PlacementSpot SpotOf(PlannedPlacement placement)
    {
        Structure prefab = placement.Prefab!;
        Vector3 p = placement.Position!.Value;
        bool small = prefab is SmallGrid;
        string slot = small ? "small:" + prefab.GetType().Name : prefab.PlacementType.ToString();
        GridStep? side = !small && prefab.PlacementType == PlacementSnap.Face ? placement.Turn?.Forward : null;
        return PlacementSpot.Of(p.x, p.y, p.z, slot, side);
    }

    private static void Source(PlacePlan plan)
    {
        Human human = Human.LocalHuman;
        plan.Owner = human != null ? human.OwnerClientId : 0UL;
        if (plan.Arguments.Free)
        {
            return;
        }

        ThingId? from = plan.Arguments.From;
        if (from.HasValue)
        {
            if (GameLookup.TryFindThing(from.Value, out Thing thing) && !thing.IsBeingDestroyed)
            {
                plan.From = thing;
                plan.Owner = thing is Human owner ? owner.OwnerClientId : plan.Owner;
                return;
            }

            plan.Problem(ApiErrors.ThingNotFoundCode, $"No thing with reference id {from.Value} to take materials from.");
            return;
        }

        if (human == null)
        {
            plan.Problem("no_local_player", "There is no local player to take materials from; pass from_id.");
            return;
        }

        plan.From = human;
    }

    private static void CountMaterials(PlacePlan plan)
    {
        if (plan.Arguments.Free)
        {
            return;
        }

        Dictionary<int, int> needed = new Dictionary<int, int>();
        foreach (PlannedPlacement placement in plan.Placements)
        {
            foreach (ItemAmount amount in placement.Cost)
            {
                needed[amount.Prefab.PrefabHash] =
                    (needed.TryGetValue(amount.Prefab.PrefabHash, out int sum) ? sum : 0) + amount.Quantity;
            }
        }

        foreach (KeyValuePair<int, int> item in needed)
        {
            Item prefab = plan.Items[item.Key];
            ItemStock stock = plan.From != null ? ItemStock.In(plan.From, prefab) : ItemStock.Empty(prefab);
            stock.Needed = item.Value;
            plan.Stocks.Add(stock);
            if (plan.From != null && stock.Available < stock.Needed)
            {
                plan.Problem("not_enough_materials",
                    $"{stock.Needed} {prefab.DisplayName} ({prefab.PrefabName}) needed, {stock.Available} held by " +
                    $"{plan.From.DisplayName}.");
            }
        }
    }

    internal static string Describe(Vector3 position) =>
        string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})", position.x, position.y,
            position.z);
}
