#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>The report of a run plan: cells, removals, materials, networks before and after, bridges and splits.</summary>
internal static class RunReports
{
    internal const string DryRun = "dry_run";
    internal const string Scheduled = "scheduled";
    internal const string Refused = "refused";

    internal static RunReportView Of(RunPlan plan, string status, string? jobId)
    {
        RunRequest request = plan.Request;
        List<string>? notes = request.Options.Detail.Notes
            ? new List<string>(request.Kind.Notes) { SwapGoal.TickNote }
            : null;
        RunHeaderView header = new RunHeaderView(request.Tool, status, jobId,
            request.Build != null ? request.Kind.NameOf(request.Build.Grade) : null, notes);
        return new RunReportView(header, Cells(plan), Materials(plan), Networks(plan), Issues(plan, plan.Problems),
            Issues(plan, plan.Warnings));
    }

    private static List<RunIssueView> Issues(RunPlan plan, List<LayoutIssue> issues)
    {
        List<RunIssueView> views = new List<RunIssueView>(issues.Count);
        foreach (LayoutIssue issue in issues)
        {
            views.Add(new RunIssueView(issue.Code, issue.Message, issue.Id.HasValue ? new ThingId(issue.Id.Value) : null,
                issue.Cell.HasValue ? GameLookup.ViewOf(PieceShapes.CentreOf(issue.Cell.Value)) : null));
        }

        return views;
    }

    private static RunCellsView Cells(RunPlan plan)
    {
        int limit = plan.Request.Options.ListLimit;
        List<RunCellView> listed = new List<RunCellView>();
        int placed = 0;
        int changed = 0;
        Dictionary<GridCell, PlannedCell> byCell = new Dictionary<GridCell, PlannedCell>();
        foreach (PlannedCell cell in plan.Cells)
        {
            byCell[cell.Cell] = cell;
            if (cell.IsChange)
            {
                changed++;
            }
            else
            {
                placed++;
            }
        }

        int total = 0;
        if (plan.Layout != null)
        {
            foreach (LayoutCell cell in plan.Layout.Cells)
            {
                total++;
                if (listed.Count < limit)
                {
                    listed.Add(CellView(plan, cell, byCell.TryGetValue(cell.Cell, out PlannedCell planned)
                        ? planned
                        : null));
                }
            }
        }

        List<RunRemovalView> removals = new List<RunRemovalView>(plan.Removals.Count);
        foreach (PlannedRemoval removal in plan.Removals)
        {
            removals.Add(new RunRemovalView(GameLookup.ViewOf(removal.Piece), GameLookup.ViewOf(removal.Piece.Position),
                removal.Network != null ? new ThingId(removal.Network.ReferenceId) : null,
                Amounts(RefundShown.Items(plan.Request.Options.Refund, removal.Refund)),
                removal.Assumed ? true : (bool?)null));
        }

        return new RunCellsView(listed, total, placed, changed, plan.KeptCells.Count, removals,
            plan.Layout != null ? plan.AirCells.Count : (int?)null);
    }

    private static RunCellView CellView(RunPlan plan, LayoutCell cell, PlannedCell? planned)
    {
        string action = cell.Action switch
        {
            CellAction.Place => "place",
            CellAction.Change => "change",
            _ => "keep"
        };
        RunPieceView piece = planned != null
            ? new RunPieceView(planned.Choice.Prefab.PrefabName, Degrees(planned.Choice.Rotation), cell.Ends.Shape,
                cell.Ends.Names(), planned.Cost,
                RefundShown.Count(plan.Request.Options.Refund, RefundCount(planned)))
            : new RunPieceView(cell.Action == CellAction.Keep && cell.Existing != null ? NameOf(plan, cell.Existing.Id)
                : null, null, cell.Ends.Shape, cell.Ends.Names(), 0, 0);
        RunExistingView? existing = null;
        if (cell.Existing != null)
        {
            SmallGrid? thing = plan.Things.TryGetValue(cell.Existing.Id, out SmallGrid found) ? found : null;
            IReferencable? network = thing != null ? plan.Request.Kind.Family.NetworkOf(thing) : null;
            existing = new RunExistingView(new ThingId(cell.Existing.Id), NameOf(plan, cell.Existing.Id) ?? "",
                cell.ExistingEnds.Names(), network != null ? new ThingId(network.ReferenceId) : null);
        }

        List<RunJoinView> joins = new List<RunJoinView>(cell.Joins.Count);
        foreach (LayoutJoin join in cell.Joins)
        {
            ThingView? target = join.TargetId.HasValue && plan.Things.TryGetValue(join.TargetId.Value, out SmallGrid t)
                ? GameLookup.ViewOf(t)
                : join.TargetId.HasValue
                    ? new ThingView(new ThingId(join.TargetId.Value), null, null)
                    : null;
            joins.Add(new RunJoinView(join.Step.Name, join.Kind, target, join.PortIndex));
        }

        RunFlowView? flow = plan.Flow.TryGetValue(cell.Cell, out CellFlow cellFlow)
            ? new RunFlowView(cellFlow.Into.Names(), cellFlow.OutOf.Names())
            : null;
        return new RunCellView(GameLookup.ViewOf(PieceShapes.CentreOf(cell.Cell)), action, piece, existing, joins,
            cell.OpenEnd?.Name, flow);
    }

    private static int RefundCount(PlannedCell cell)
    {
        int count = 0;
        foreach (ItemAmount amount in cell.Refund)
        {
            if (amount.Prefab.PrefabHash == cell.Kit.Item.PrefabHash)
            {
                count += amount.Quantity;
            }
        }

        return count;
    }

    private static string? NameOf(RunPlan plan, long id) =>
        plan.Things.TryGetValue(id, out SmallGrid thing) ? thing.PrefabName : null;

    private static PositionView Degrees(Quaternion rotation)
    {
        Vector3 euler = rotation.eulerAngles;
        return new PositionView(Mathf.Round(euler.x) % 360f, Mathf.Round(euler.y) % 360f, Mathf.Round(euler.z) % 360f);
    }

    private static RunMaterialsView Materials(RunPlan plan)
    {
        List<UpgradeCoilView> needed = new List<UpgradeCoilView>(plan.Stocks.Count);
        foreach (ItemStock stock in plan.Stocks)
        {
            List<UpgradeStackView> stacks = new List<UpgradeStackView>(stock.Stacks.Count);
            foreach (Stackable stack in stock.Stacks)
            {
                Slot? slot = stack.ParentSlot;
                stacks.Add(new UpgradeStackView(new ThingId(stack.ReferenceId), stack.Quantity,
                    slot?.Parent != null ? new ThingId(slot.Parent.ReferenceId) : null, slot?.SlotIndex));
            }

            needed.Add(new UpgradeCoilView(stock.Item.PrefabName, stock.Item.DisplayName, stock.Needed,
                stock.Available, stacks));
        }

        List<ItemAmount> refund = new List<ItemAmount>();
        foreach (PlannedRemoval removal in plan.Removals)
        {
            if (!removal.Assumed)
            {
                refund.AddRange(removal.Refund);
            }
        }

        foreach (PlannedCell cell in plan.Cells)
        {
            refund.AddRange(cell.Refund);
        }

        return new RunMaterialsView(plan.From != null ? GameLookup.ViewOf(plan.From) : null, needed,
            plan.Request.Options.Refund, Amounts(refund), Refunds.Forecast(plan.Refunds, refund));
    }

    internal static List<UpgradeAmountView> Amounts(List<ItemAmount> amounts)
    {
        List<string?> names = new List<string?>();
        List<int> totals = new List<int>();
        foreach (ItemAmount amount in amounts)
        {
            int at = names.IndexOf(amount.Prefab.PrefabName);
            if (at < 0)
            {
                names.Add(amount.Prefab.PrefabName);
                totals.Add(amount.Quantity);
            }
            else
            {
                totals[at] += amount.Quantity;
            }
        }

        List<UpgradeAmountView> views = new List<UpgradeAmountView>(names.Count);
        for (int index = 0; index < names.Count; index++)
        {
            views.Add(new UpgradeAmountView(names[index], totals[index]));
        }

        return views;
    }

    private static RunNetworksView Networks(RunPlan plan)
    {
        RunForecast? forecast = plan.Forecast;
        if (forecast == null)
        {
            return new RunNetworksView(new List<object>(), new List<RunNetworkAfterView>(), new List<RunBridgeView>(),
                new List<RunSplitView>(), null);
        }

        RunKind kind = plan.Request.Kind;
        List<long> ids = new List<long>(forecast.Context.NetworksBefore.Keys);
        ids.Sort();
        List<object> before = new List<object>(ids.Count);
        foreach (long id in ids)
        {
            before.Add(kind.Summary(forecast.Context.NetworksBefore[id]));
        }

        return new RunNetworksView(before, After(plan, forecast), Bridges(plan, forecast), Splits(plan, forecast),
            Links(plan, forecast));
    }

    private static List<RunNetworkAfterView> After(RunPlan plan, RunForecast forecast)
    {
        List<RunNetworkAfterView> after = new List<RunNetworkAfterView>(forecast.Result.Networks.Count);
        HashSet<int> touched = Touched(plan, forecast);
        foreach (ForecastNetwork network in forecast.Result.Networks)
        {
            if (!touched.Contains(network.Index))
            {
                continue;
            }

            List<ThingId> networks = new List<ThingId>(network.NetworksBefore.Count);
            foreach (long id in network.NetworksBefore)
            {
                networks.Add(new ThingId(id));
            }

            List<RunPortView> ports = new List<RunPortView>(network.Ports.Count);
            foreach (ForecastPort port in network.Ports)
            {
                ports.Add(PortView(plan, port, forecast.Result.IsBridging(port)));
            }

            after.Add(new RunNetworkAfterView(network.Index, networks, network.NewPieces.Count, ports,
                forecast.Guards.TryGetValue(network.Index, out KindGuard guard) ? guard.View : null));
        }

        return after;
    }

    // The networks after the edit it changes: new or changed pieces, pieces of two networks, a network losing pieces,
    // or a device port joining it from elsewhere. A device's other network that the edit leaves as it is (read only to
    // see whether the edit bridges the device) is not listed.
    private static HashSet<int> Touched(RunPlan plan, RunForecast forecast)
    {
        HashSet<long> losing = new HashSet<long>();
        foreach (PlannedRemoval removal in plan.Removals)
        {
            if (removal.Network != null)
            {
                losing.Add(removal.Network.ReferenceId);
            }
        }

        HashSet<int> touched = new HashSet<int>();
        foreach (PlannedCell cell in plan.Cells)
        {
            if (forecast.NodeOf.TryGetValue(cell.ForecastId, out long node) &&
                forecast.ComponentOf.TryGetValue(node, out int index))
            {
                touched.Add(index);
            }
        }

        foreach (ForecastNetwork network in forecast.Result.Networks)
        {
            if (network.NewPieces.Count > 0 || network.NetworksBefore.Count != 1 ||
                losing.Contains(network.NetworksBefore[0]) ||
                network.Ports.Exists(port => port.NetworkBefore != network.NetworksBefore[0]))
            {
                touched.Add(network.Index);
            }
        }

        return touched;
    }

    private static RunPortView PortView(RunPlan plan, ForecastPort port, bool bridging) =>
        new RunPortView(plan.Things.TryGetValue(port.DeviceId, out SmallGrid device)
                ? GameLookup.ViewOf(device)
                : new ThingView(new ThingId(port.DeviceId), null, null), port.Index, bridging,
            port.NetworkBefore.HasValue ? new ThingId(port.NetworkBefore.Value) : null);

    private static List<RunBridgeView> Bridges(RunPlan plan, RunForecast forecast)
    {
        HashSet<long> allowed = plan.Request.Options.Allow.Bridge;
        List<RunBridgeView> bridges = new List<RunBridgeView>();
        foreach (ForecastNetwork merge in forecast.Result.Merges)
        {
            bool all = true;
            List<RunBridgeSideView> sides = new List<RunBridgeSideView>(merge.NetworksBefore.Count);
            foreach (long id in merge.NetworksBefore)
            {
                all &= allowed.Contains(id);
                sides.Add(Side(plan, forecast, id));
            }

            bridges.Add(new RunBridgeView("networks", sides, null, new List<int>(), all));
        }

        foreach (ForecastBridge bridge in forecast.Result.Bridges)
        {
            List<RunBridgeSideView> sides = new List<RunBridgeSideView>();
            List<int> ports = new List<int>(bridge.Ports.Count);
            foreach (ForecastPort port in bridge.Ports)
            {
                ports.Add(port.Index);
                if (port.NetworkBefore.HasValue && !sides.Exists(side => side.NetworkId.Value == port.NetworkBefore))
                {
                    sides.Add(Side(plan, forecast, port.NetworkBefore.Value));
                }
            }

            ThingView device = plan.Things.TryGetValue(bridge.Device, out SmallGrid thing)
                ? GameLookup.ViewOf(thing)
                : new ThingView(new ThingId(bridge.Device), null, null);
            bridges.Add(new RunBridgeView("device", sides, device, ports, allowed.Contains(bridge.Device)));
        }

        return bridges;
    }

    private static RunBridgeSideView Side(RunPlan plan, RunForecast forecast, long network) =>
        new RunBridgeSideView(new ThingId(network),
            forecast.Context.NetworksBefore.TryGetValue(network, out IReferencable found)
                ? RunNetworks.Views(plan.Request.Kind.DevicesOf(found))
                : new List<ThingView>());

    private static List<RunSplitView> Splits(RunPlan plan, RunForecast forecast)
    {
        bool allowed = plan.Request.Options.Allow.Split;
        List<RunSplitView> splits = new List<RunSplitView>();
        List<SplitDetail> details = SplitAnalysis.Of(forecast.Result, plan.Roots);
        for (int index = 0; index < forecast.Result.Splits.Count; index++)
        {
            ForecastSplit split = forecast.Result.Splits[index];
            splits.Add(new RunSplitView(new ThingId(split.Network), split.Parts, new List<RunPortView>(), allowed,
                DevicesOf(plan, details[index])));
        }

        // One entry per network the cut ports were on.
        for (int index = forecast.Result.Splits.Count; index < details.Count; index++)
        {
            SplitDetail cutDetail = details[index];
            List<RunPortView> cut = new List<RunPortView>();
            foreach (ForecastPort port in forecast.Result.Cut)
            {
                if (SplitAnalysis.CutEntryOf(details, forecast.Result.Splits.Count, port) == cutDetail)
                {
                    cut.Add(PortView(plan, port, false));
                }
            }

            splits.Add(new RunSplitView(cutDetail.Network.HasValue ? new ThingId(cutDetail.Network.Value) : null,
                new List<int>(), cut, allowed, DevicesOf(plan, cutDetail)));
        }

        return splits;
    }

    private static RunSplitDevicesView DevicesOf(RunPlan plan, SplitDetail detail)
    {
        List<RunSplitPartView> parts = new List<RunSplitPartView>(detail.Parts.Count);
        foreach (SplitPart part in detail.Parts)
        {
            parts.Add(new RunSplitPartView(part.Index, part.Ports.ConvertAll(port => PortView(plan, port, false)),
                part.HoldsRoot));
        }

        return new RunSplitDevicesView(parts, detail.Roots.ConvertAll(id => DeviceView(plan, id)),
            detail.CutOff?.ConvertAll(id => DeviceView(plan, id)));
    }

    private static ThingView DeviceView(RunPlan plan, long id) =>
        plan.Things.TryGetValue(id, out SmallGrid device)
            ? GameLookup.ViewOf(device)
            : GameLookup.TryFindThing(new ThingId(id), out Thing thing)
                ? GameLookup.ViewOf(thing)
                : new ThingView(new ThingId(id), null, null);

    private static RunLinksView Links(RunPlan plan, RunForecast forecast)
    {
        LinkDiff change = Connectivity.Compare(forecast.GameBefore, forecast.After);
        List<UpgradeLinkView> differences = ViewsOf(plan, forecast.ModelCheck.Added);
        differences.AddRange(ViewsOf(plan, forecast.ModelCheck.Lost));
        RunLinksView links = new RunLinksView(forecast.GameBefore.Count, forecast.After.Count,
            ViewsOf(plan, change.Added), ViewsOf(plan, change.Lost), differences);
        return plan.Request.Options.Detail.Links ? links : links.CountsOnly();
    }

    private static List<UpgradeLinkView> ViewsOf(RunPlan plan, List<Link> links)
    {
        List<UpgradeLinkView> views = new List<UpgradeLinkView>(links.Count);
        foreach (Link link in links)
        {
            views.Add(new UpgradeLinkView(Named(plan, link.From), Named(plan, link.To)));
        }

        return views;
    }

    private static ThingView Named(RunPlan plan, long id)
    {
        if (plan.Things.TryGetValue(id, out SmallGrid thing))
        {
            return GameLookup.ViewOf(thing);
        }

        foreach (PlannedCell cell in plan.Cells)
        {
            if (cell.ForecastId == id)
            {
                return new ThingView(new ThingId(id), cell.Choice.Prefab.PrefabName, "new piece");
            }
        }

        return new ThingView(new ThingId(id), null, null);
    }
}
