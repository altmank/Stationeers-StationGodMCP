#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// What a request replaces each piece with. The goal decides, piece by piece, whether it is swapped (and for what),
/// kept, unmatched or reported; the preflight, the link survey, the job and the swap are shared.
/// </summary>
internal abstract class SwapGoal
{
    internal const string TickNote =
        "A confirmed run holds the game tick as a save does (GameManager.PauseGameTick), checks everything again " +
        "once the tick has stopped, swaps every piece in that one frame and lets the tick go after checking the " +
        "result the next frame; no power, atmospherics or logic tick sees a half-built network.";

    /// <summary>The MCP method name.</summary>
    internal abstract string Tool { get; }

    /// <summary>What the pieces become, as the report names it (heavy, super_heavy, insulated, minimal).</summary>
    internal abstract string Target { get; }

    /// <summary>The problem message when no piece is to be swapped.</summary>
    internal abstract string NothingToSwap { get; }

    /// <summary>Whether the report lists each piece's ends and connected ends.</summary>
    internal virtual bool ReportsEnds => false;

    internal abstract List<string> Notes(UpgradeFamily family);

    /// <summary>Puts the member into the plan: a swap, kept, unmatched, a dead end, or a problem.</summary>
    internal abstract void Classify(PlanContext context, SmallGrid member);

    /// <summary>Called once every member was classified: for goals that plan the members together.</summary>
    internal virtual void Finish(PlanContext context, List<SmallGrid> members)
    {
    }
}

/// <summary>
/// upgrade_cables and upgrade_pipes: each piece becomes the piece of the higher grade's coil or kit with the same
/// cells and ends. Costs the replacement's entry quantity; gives back what deconstructing the old piece would.
/// </summary>
internal abstract class GradeUpgrade : SwapGoal
{
    private const string RefundNote =
        "With refund, what deconstructing the old pieces gives back (their build states' entry items) is made at the " +
        "source as ToolUse.SpawnItem makes it: worn belts and backpacks collect what fits, the rest lies on the " +
        "ground at the source.";

    internal override string NothingToSwap => "No piece below the target was found.";

    internal override List<string> Notes(UpgradeFamily family) =>
        new List<string> { family.DeviceNote, TickNote, RefundNote };

    /// <summary>Whether the member is already at or above the target, so it stays as it is.</summary>
    protected abstract bool AtOrAboveTarget(SmallGrid member);

    /// <summary>The grade a piece of this grade becomes; null when there is no upgrade for it.</summary>
    protected abstract Grade? UpgradeOf(Grade source);

    internal override void Classify(PlanContext context, SmallGrid member)
    {
        UpgradeFamily family = context.Family;
        if (!family.IsPiece(member))
        {
            context.Plan.Kept.Add(PlanContext.NotAPiece(family, member));
            return;
        }

        if (AtOrAboveTarget(member))
        {
            context.Plan.Kept.Add(new SkippedPiece(member, "already_at_target",
                $"{member.PrefabName} is already {Target} or better."));
            return;
        }

        Grade? grade = family.GradeOf(member);
        Grade? upgrade = grade != null ? UpgradeOf(grade) : null;
        if (grade == null || upgrade == null)
        {
            context.Plan.Unmatched.Add(PlanContext.SpecialPiece(member, "a coil or kit this tool upgrades"));
            return;
        }

        Kit? source = context.Kits.For(grade);
        Kit? target = context.Kits.For(upgrade);
        if (source == null || target == null || !source.Places(member.PrefabHash))
        {
            context.Plan.Unmatched.Add(new SkippedPiece(member, "no_kit", NoKitMessage(context.Kits, member, grade,
                upgrade)));
            return;
        }

        PlanSwap(context, member, target);
    }

    internal static void PlanSwap(PlanContext context, SmallGrid member, Kit target)
    {
        PieceModel live = PieceShapes.Live(member);
        if (!context.MatchesOwnPrefab(member, live))
        {
            return;
        }

        Twin? twin = context.Twins.Find(member, live, target);
        if (twin == null)
        {
            context.Plan.Unmatched.Add(new SkippedPiece(member, "no_twin", PlanContext.NoTwinMessage(member, target,
                "the same cells and connection ends as")));
            return;
        }

        context.CheckCondition(member, twin);
        context.Plan.Swaps.Add(new PlannedSwap(member, live, target, new List<Twin> { twin },
            new SwapPrice(Kit.CostOf(twin.Prefab), PlanContext.RefundOf(member))));
    }

    private static string NoKitMessage(KitCatalogue kits, SmallGrid member, Grade grade, Grade upgrade)
    {
        if (kits.IsAmbiguous(grade) || kits.IsAmbiguous(upgrade))
        {
            return $"Two kits place {grade.Name} or {upgrade.Name} pieces equally; the kit is ambiguous.";
        }

        Kit? source = kits.For(grade);
        if (kits.For(upgrade) == null)
        {
            return $"No kit places {upgrade.Name} pieces.";
        }

        return source == null
            ? $"No kit places {grade.Name} pieces."
            : $"{member.PrefabName} is not one of the pieces {source.Item.PrefabName} places.";
    }
}

/// <summary>
/// upgrade_cables: the grade is Cable.CableType; a piece below the target becomes the target coil's piece. CableType
/// is read by nothing but placement, merging and the debug drawing (CableFamily).
/// </summary>
internal sealed class CableUpgrade : GradeUpgrade
{
    private readonly Cable.Type _target;

    internal CableUpgrade(Cable.Type target)
    {
        _target = target;
    }

    internal override string Tool => "upgrade_cables";

    internal override string Target => CableFamily.NameOf(_target);

    protected override bool AtOrAboveTarget(SmallGrid member) =>
        member is Cable cable && (int)cable.CableType >= (int)_target;

    protected override Grade? UpgradeOf(Grade source) =>
        source.Level < (int)_target ? new Grade((int)_target, 0, CableFamily.NameOf(_target)) : null;
}

/// <summary>
/// upgrade_pipes: a normal piece becomes insulated with the same content (normal to Insulated, NormalLowVolume to
/// InsulatedLowVolume); gas and liquid never mix, the content is part of the grade (PipeFamily).
/// </summary>
internal sealed class PipeUpgrade : GradeUpgrade
{
    internal override string Tool => "upgrade_pipes";

    internal override string Target => "insulated";

    protected override bool AtOrAboveTarget(SmallGrid member) =>
        member is Piping piping &&
        (piping.PipeType == Piping.Type.Insulated || piping.PipeType == Piping.Type.InsulatedLowVolume);

    protected override Grade? UpgradeOf(Grade source)
    {
        Piping.Type? target = (Piping.Type)source.Level switch
        {
            Piping.Type.normal => Piping.Type.Insulated,
            Piping.Type.NormalLowVolume => Piping.Type.InsulatedLowVolume,
            _ => null
        };
        return target.HasValue
            ? new Grade((int)target.Value, source.Content,
                $"{target.Value} {(Pipe.ContentType)source.Content}".ToLowerInvariant())
            : null;
    }
}

/// <summary>
/// upgrade_pipes to repair: each burst piece becomes a new piece of its own kit with the same cells and ends, joined to
/// the network before the burst piece leaves, so the contents stay (a burst pipe that is the only pipe of its network
/// included). The game offers no repair for a burst pipe; a player deconstructs it and builds a new one, which empties
/// a network of one pipe. Pieces that are not burst stay.
/// </summary>
internal sealed class PipeRepair : SwapGoal
{
    internal override string Tool => "upgrade_pipes";

    internal override string Target => "repair";

    internal override string NothingToSwap => "No burst pipe was found.";

    internal override List<string> Notes(UpgradeFamily family) => new List<string> { family.DeviceNote, TickNote };

    internal override void Classify(PlanContext context, SmallGrid member)
    {
        UpgradeFamily family = context.Family;
        if (!family.IsPiece(member))
        {
            context.Plan.Kept.Add(PlanContext.NotAPiece(family, member));
            return;
        }

        if (!(member is Pipe pipe) || !Wrecks.IsBroken(pipe))
        {
            context.Plan.Kept.Add(new SkippedPiece(member, "not_burst", $"{member.PrefabName} is not burst."));
            return;
        }

        Grade? grade = family.GradeOf(member);
        Kit? kit = grade != null ? context.Kits.For(grade) : null;
        if (kit == null || !kit.Places(member.PrefabHash))
        {
            context.Plan.Unmatched.Add(PlanContext.SpecialPiece(member, "a kit this tool places"));
            return;
        }

        if (!LineMembership.OnItsLine(pipe.PipeNetwork?.ReferenceId, AttachedNetworks(pipe)))
        {
            context.Plan.Problem("off_network",
                $"{member.PrefabName} {member.ReferenceId} is not on the network of the pipes its ends meet, so a " +
                "swap would join its replacement to the network it is on and take the replacement off the line. " +
                context.InPlaceAdvice(member), member);
            return;
        }

        GradeUpgrade.PlanSwap(context, member, kit);
    }

    // The network of every pipe attached at the piece's pipe ends (null for one on none).
    private static List<long?> AttachedNetworks(Pipe pipe)
    {
        List<long?> networks = new List<long?>();
        foreach (Connection end in pipe.OpenEnds ?? new List<Connection>())
        {
            if (end == null)
            {
                continue;
            }

            foreach (Thing attached in EndsReader.AttachedAt(pipe, end))
            {
                if (attached is Pipe other)
                {
                    networks.Add(other.PipeNetwork?.ReferenceId);
                }
            }
        }

        return networks;
    }
}

/// <summary>What a goal classifies against: the plan it fills, the family's kits and the twin searches.</summary>
internal sealed class PlanContext
{
    internal PlanContext(UpgradePlan plan, KitCatalogue kits, TwinFinder twins)
    {
        Plan = plan;
        Kits = kits;
        Twins = twins;
    }

    internal UpgradePlan Plan { get; }

    internal UpgradeFamily Family => Plan.Request.Family;

    internal KitCatalogue Kits { get; }

    internal TwinFinder Twins { get; }

    /// <summary>
    /// The coil or kit of the member's own grade; null, with the member unmatched, when there is none. A piece to
    /// simplify must be one the kit places (its merge replaces pieces of its own); a long straight to split needs only
    /// the kit of its grade, which places the singles.
    /// </summary>
    internal Kit? KitFor(SmallGrid member, bool mustPlaceIt, bool quiet = false)
    {
        Grade? grade = mustPlaceIt ? Family.GradeOf(member) : Family.RunGradeOf(member);
        if (grade == null)
        {
            if (!quiet)
            {
                Plan.Unmatched.Add(SpecialPiece(member, "a coil or kit"));
            }

            return null;
        }

        Kit? kit = Kits.For(grade);
        if (kit != null && (!mustPlaceIt || kit.Places(member.PrefabHash)))
        {
            return kit;
        }

        string why = kit != null
            ? $"{member.PrefabName} is not one of the pieces {kit.Item.PrefabName} places."
            : Kits.IsAmbiguous(grade)
                ? $"Two coils or kits place {grade.Name} pieces equally; the kit is ambiguous."
                : $"No coil or kit places {grade.Name} pieces.";
        if (!quiet)
        {
            Plan.Unmatched.Add(new SkippedPiece(member, "no_kit", why));
        }

        return null;
    }

    /// <summary>
    /// The merge rule (MultiMergeConstructor.Construct): the new pieces' entry quantities less the old pieces', for old
    /// pieces built from the same coil or kit; taken when positive, given back in that item when negative. An old piece
    /// built from anything else gives back what deconstructing it would, and the new pieces are charged in full.
    /// </summary>
    internal static SwapPrice Priced(List<OldPiece> olds, Kit kit, List<Twin> parts)
    {
        int cost = 0;
        foreach (Twin part in parts)
        {
            cost += Kit.CostOf(part.Prefab);
        }

        int credit = 0;
        List<ItemAmount> refund = new List<ItemAmount>();
        foreach (OldPiece old in olds)
        {
            int own = KitEntryOf(old.Piece, kit);
            if (own >= 0)
            {
                credit += own;
            }
            else
            {
                refund.AddRange(RefundOf(old.Piece));
            }
        }

        int difference = EndCleanup.CostDifference(credit, cost);
        if (difference < 0)
        {
            refund.Add(new ItemAmount(kit.Item, -difference));
        }

        return new SwapPrice(System.Math.Max(difference, 0), refund);
    }

    // The entry quantity of the piece's first build state when that state takes the kit's item; -1 otherwise.
    private static int KitEntryOf(SmallGrid piece, Kit kit)
    {
        Structure own = Prefab.Find(piece.PrefabHash) is Structure registered && registered != null
            ? registered
            : piece;
        ToolUse? build = own.BuildStates != null && own.BuildStates.Count > 0 ? own.BuildStates[0].Tool : null;
        Item? entry = build != null ? build.ToolEntry : null;
        return build != null && entry != null && entry.PrefabHash == kit.Item.PrefabHash ? build.EntryQuantity : -1;
    }

    internal static SkippedPiece NotAPiece(UpgradeFamily family, SmallGrid member) =>
        new SkippedPiece(member, "not_a_piece",
            $"{member.PrefabName} is a device on the run, not a {family.NetworkKind} piece; it stays.");

    internal static SkippedPiece SpecialPiece(SmallGrid member, string placedBy) =>
        new SkippedPiece(member, "special_piece", $"{member.PrefabName} is not placed by {placedBy}.");

    /// <summary>
    /// Whether the piece as placed matches its own prefab read at its place; a problem when it does not, since its
    /// replacement cannot then be predicted.
    /// </summary>
    internal bool MatchesOwnPrefab(SmallGrid member, PieceModel live)
    {
        Structure? own = Prefab.Find(member.PrefabHash) as Structure;
        PieceModel? modelled = own != null
            ? PieceShapes.Placed(own, PieceShapes.PlacementOf(member), member.ThingTransformRotation,
                member.ReferenceId)
            : null;
        if (modelled != null && Connectivity.SameShape(modelled, live))
        {
            return true;
        }

        Plan.Problem("shape_model_mismatch",
            $"{member.PrefabName} as placed does not match its own prefab read at its place; its replacement " +
            "cannot be predicted.", member);
        return false;
    }

    internal static string NoTwinMessage(SmallGrid member, Kit target, string relation)
    {
        string message = $"No piece of {target.Item.PrefabName} has {relation} {member.PrefabName}.";
        if (target.Unmodelled.Count == 0)
        {
            return message;
        }

        List<string> names = target.Unmodelled.ConvertAll(static piece => piece.PrefabName);
        return $"{message} These pieces of it could not be read: {string.Join(", ", names)}.";
    }

    internal void CheckCondition(SmallGrid member, Twin twin)
    {
        if (member.CurrentBuildStateIndex < 0)
        {
            Plan.Problem("broken_build_state",
                $"{member.PrefabName} {member.ReferenceId} stands in its broken build state (damage destroyed it), " +
                "which a swap does not replace. " + InPlaceAdvice(member), member);
        }
        else if (!member.IsStructureCompleted)
        {
            Plan.Problem("not_complete", $"{member.PrefabName} is not fully built.", member);
        }

        if (member.Indestructable)
        {
            Plan.Problem("indestructible", $"{member.PrefabName} is indestructible and cannot be replaced.", member);
        }

        if ((member is Cable cable && cable.RocketNetwork != null) ||
            (member is Pipe rocketPipe && rocketPipe.RocketNetwork != null))
        {
            Plan.Problem("rocket_internal",
                $"{member.PrefabName} {member.ReferenceId} is inside a rocket, and this tool swaps no rocket pieces. " +
                InPlaceAdvice(member), member);
        }

        if (twin.Prefab.BuildStates == null || twin.Prefab.BuildStates.Count != 1)
        {
            Plan.Problem("target_needs_building",
                $"{twin.Prefab.PrefabName} is not finished when placed ({twin.Prefab.BuildStates?.Count ?? 0} build " +
                "states).", member);
        }
    }

    /// <summary>
    /// How to replace the piece in one job with the family's place tool instead (ReplaceInPlace): its cell, ends and
    /// grade as it stands, the piece itself in remove_ids.
    /// </summary>
    internal string InPlaceAdvice(SmallGrid member)
    {
        RunKind kind = Family switch
        {
            CableFamily => new CableRunKind(),
            ChuteFamily => new ChuteRunKind(),
            _ => new PipeRunKind()
        };
        Grade? grade = Family.RunGradeOf(member);
        return ReplaceInPlace.Advice(kind.PlaceTool, member.ReferenceId, PieceShapes.Live(member),
            grade != null ? kind.NameOf(grade) : null);
    }

    // What deconstructing the piece gives back (ToolUse.Deconstruct over its build states, BuildMaterials).
    internal static List<ItemAmount> RefundOf(SmallGrid piece) => BuildMaterials.RefundOf(piece);
}
