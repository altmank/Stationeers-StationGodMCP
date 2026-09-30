#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Util;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>place_structure's and remove_structure's plans as reports.</summary>
internal static class BuildReports
{
    internal const string DryRun = "dry_run";
    internal const string Scheduled = "scheduled";
    internal const string Refused = "refused";

    private static readonly List<string> PlaceNotes = new List<string>
    {
        "Checked as the game's own cursor checks a placement (CanConstruct, a face mount's support, nothing loose " +
        "inside a piece that fills its cell), again just before each piece is built.",
        "A piece is built as a kit builds it (Constructor.SpawnConstruct) and raised to the chosen build state; the " +
        "cost is every build state's items up to that state (state 0 is the kit), taken from the source as a kit's " +
        "placement takes them. Tools, welder fuel and battery charge are not charged.",
        "A real run needs dry_run: false and confirm: true; poll the job with job_id.",
        "Cables, pipes and chutes: place_cables, place_pipes and place_chutes pick pieces by their connections and " +
        "guard merges; walls and frames in place: replace_walls, replace_frames."
    };

    private static readonly List<string> RemoveNotes = new List<string>
    {
        "Gives back what deconstructing by hand does: every build state's items down to the kit; a broken piece " +
        "gives nothing, as the game's deconstruction of a broken thing gives nothing.",
        "Refused: indestructible, a launching or landing rocket's part, the game's own refusal (a fuselage piece: " +
        "nothing on top, no internals left in its cells), a mounted device; unless allowed: broken " +
        "(allow_broken), items or gas inside (allow_contents), a removal joining spaces whose pressures differ by 1 kPa or more " +
        "(allow_breach). The breach check judges the whole request at once: a face stays sealed while anything " +
        "left on it, or a finished frame beside it, blocks air.",
        "An in-line tank or passive vent whose removal squeezes the network left past its weakest pipe is refused " +
        "(would_burst) unless allow_burst, e.g. outdoors: then will_burst names the pipes expected to burst, where " +
        "they leak (a room id or outdoors) and the gas expected out, and the job's gas check expects that release.",
        "Cable, pipe and chute pieces are removed as remove_cables, remove_pipes and remove_chutes remove them; " +
        "their would_split is a warning here.",
        "A real run needs dry_run: false and confirm: true; poll the job with job_id."
    };

    internal static PlaceReportView Of(PlacePlan plan, string status, string? jobId)
    {
        List<PlacementView> placements = new List<PlacementView>(plan.Placements.Count);
        foreach (PlannedPlacement placement in plan.Placements)
        {
            placements.Add(ViewOf(placement));
        }

        List<BuildMaterialView> materials = new List<BuildMaterialView>(plan.Stocks.Count);
        foreach (ItemStock stock in plan.Stocks)
        {
            materials.Add(new BuildMaterialView(stock.Item.PrefabName, stock.Needed, stock.Available));
        }

        return new PlaceReportView(new BuildHeader(status, jobId, plan.Problems, plan.Warnings, PlaceNotes),
            placements, materials, plan.From != null ? GameLookup.ViewOf(plan.From) : null, plan.Arguments.Free);
    }

    internal static RemoveReportView Of(RemovePlan plan, string status, string? jobId)
    {
        List<RemovalView> removals = new List<RemovalView>(plan.Takedowns.Count);
        List<ItemAmount> all = new List<ItemAmount>();
        bool refunds = plan.Arguments.RefundTo.GivesBack;
        foreach (PlannedTakedown takedown in plan.Takedowns)
        {
            // refund_to none gives nothing back, so nothing is listed as given (structures-26).
            List<ItemAmount> refund = refunds ? takedown.Refund : new List<ItemAmount>();
            removals.Add(new RemovalView(takedown.Index, GameLookup.ViewOf(takedown.Piece),
                GameLookup.ViewOf(takedown.Position), takedown.BuildState, takedown.KindName, Amounts(refund)));
            all.AddRange(refund);
        }

        return new RemoveReportView(new BuildHeader(status, jobId, plan.Problems, plan.Warnings, RemoveNotes),
            removals, Amounts(all), RefundArgs.View(plan.Arguments.RefundTo),
            plan.From != null ? GameLookup.ViewOf(plan.From) : null,
            plan.Bursts.ConvertAll(static burst => new BurstView(burst)), RemovalRefundPlan(plan, all));
    }

    // refund_to ground (the single word): every item on the ground where its piece stood; else as Deliver plans it.
    private static RefundPlanView? RemovalRefundPlan(RemovePlan plan, List<ItemAmount> all)
    {
        if (plan.Arguments.RefundTo != RefundRoute.WherePieceStood)
        {
            return Refunds.Forecast(plan.Refunds, all);
        }

        List<RefundDestinationView> destinations = new List<RefundDestinationView>();
        foreach (UpgradeAmountView amount in Amounts(all))
        {
            destinations.Add(new RefundDestinationView(amount.PrefabName, amount.Quantity, RefundTarget.GroundName,
                Refunds.Ground, null, false));
        }

        return new RefundPlanView(RefundArgs.View(plan.Arguments.RefundTo), new List<string>(), destinations);
    }

    internal static PlacementView ViewOf(PlannedPlacement placement)
    {
        Structure? prefab = placement.Prefab;
        PlacementPrefabView prefabView = prefab != null
            ? new PlacementPrefabView(prefab.PrefabName, prefab.PrefabHash, prefab.DisplayName,
                prefab.BuildStates.Count)
            : new PlacementPrefabView(placement.Args.Prefab.ToString(), null, null, null);
        OrientationView? orientation = placement.Turn != null ? OrientationView.Of(placement.Turn) : null;
        string? face = prefab != null && prefab.PlacementType == PlacementSnap.Face && placement.Turn != null
            ? placement.Turn.Forward.Opposite.Name
            : null;
        PlacementSpotView spot = new PlacementSpotView(prefab != null ? SnapName(prefab.PlacementType) : null,
            placement.Position.HasValue ? GameLookup.ViewOf(placement.Position.Value) : null, orientation, face);
        ColorView? color = placement.ColorIndex >= 0 ? ColorOf(placement.ColorIndex) : null;
        return new PlacementView(placement.Index, prefabView, spot,
            new PlacementLookView(placement.State, placement.Args.Label, color), Amounts(placement.Cost),
            placement.Ports, placement.Layout?.View, placement.Orient,
            placement.ResolvedAt != null && placement.At.HasValue
                ? new ResolvedPlacementView(new PointView(placement.At.Value.X, placement.At.Value.Y,
                        placement.At.Value.Z), AtHow(placement) + (placement.AboveFloorHow ?? string.Empty),
                    placement.ResolvedFacing, placement.ResolvedFacingHow)
                : null);
    }

    // How at was read. A point as given that was set down on the surface behind it reads as set down, not "as given;
    // set down ..." (structures-28); a relative at keeps how it was read, then how it was set down.
    private static string AtHow(PlannedPlacement placement)
    {
        string how = placement.ResolvedAt!.How;
        string? setDown = placement.SetDownHow;
        if (setDown == null)
        {
            return how;
        }

        return placement.Args.At is AtArg.Absolute ? setDown.TrimStart(';', ' ') : how + setDown;
    }

    internal static RotationView Euler(Quaternion rotation)
    {
        Vector3 angles = rotation.eulerAngles;
        return new RotationView(Round(angles.x), Round(angles.y), Round(angles.z));
    }

    internal static List<UpgradeAmountView> Amounts(List<ItemAmount> amounts)
    {
        List<UpgradeAmountView> views = new List<UpgradeAmountView>();
        Dictionary<int, int> at = new Dictionary<int, int>();
        List<int> totals = new List<int>();
        List<Item> prefabs = new List<Item>();
        foreach (ItemAmount amount in amounts)
        {
            if (!at.TryGetValue(amount.Prefab.PrefabHash, out int position))
            {
                position = totals.Count;
                at[amount.Prefab.PrefabHash] = position;
                totals.Add(0);
                prefabs.Add(amount.Prefab);
            }

            totals[position] += amount.Quantity;
        }

        for (int index = 0; index < totals.Count; index++)
        {
            views.Add(new UpgradeAmountView(prefabs[index].PrefabName, totals[index]));
        }

        return views;
    }

    private static ColorView? ColorOf(int index)
    {
        GameManager manager = Singleton<GameManager>.Instance;
        List<ColorSwatch>? swatches = manager != null ? manager.CustomColors : null;
        string? name = swatches != null && index < swatches.Count ? swatches[index]?.DisplayName : null;
        return new ColorView(index, name);
    }

    // Quaternion.eulerAngles of a quarter turn lands a hair off 90; whole degrees, 360 folded to 0.
    private static double Round(float degrees)
    {
        double whole = System.Math.Round(degrees);
        return whole >= 360 ? whole - 360 : whole;
    }

    internal static string SnapName(PlacementSnap snap) => snap switch
    {
        PlacementSnap.Grid => "grid",
        PlacementSnap.Face => "face",
        PlacementSnap.FaceMount => "face_mount",
        _ => snap.ToString().ToLowerInvariant()
    };
}
