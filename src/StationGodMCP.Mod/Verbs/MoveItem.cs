#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Chutes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// move item (catalogue move.json): items into a slot or the player's hands with the game's own moves (MovePlanner),
/// or into or out of a vault or silo store (VaultDeposits, VaultWithdrawals, VaultTransfers, SiloDeposits,
/// SiloWithdrawals). The route (ItemMoveRoutes) is settled first: slot or moves make a slot move; a vault or silo as
/// source or to makes a store move. Typed ids act at once; force (a cheat), every filter (a spread, prefab
/// included) and a store move that names no things by typed ids or item lines report until confirm
/// (ItemMoveGiven.Acts).
/// </summary>
internal static class MoveItem
{
    private const string PlayerWord = "player";

    internal static VerbOutcome Run(MoveItemOptions options)
    {
        string label = MoveItemOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        List<string> given = ItemMoveGiven.Of(options);
        bool slotMove = options.Slot != null || options.Moves != null;
        if (options.Source != null && slotMove)
        {
            problems.Add(new OptionProblem("source", "goes without slot and moves: a slot move takes ids"));
        }

        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        string? route;
        try
        {
            route = ItemMoveRoutes.Of(slotMove, options.Source is { } source ? KindOfSource(source, label) : null,
                options.To is { } to ? KindOf(to, label) : StoreKind.None, problems);
        }
        catch (ApiException refused)
        {
            return new VerbOutcome.Refused(VerbError.Of(refused));
        }

        if (route != null)
        {
            ItemMoveGiven.RouteProblems(route, options, given, problems);
        }

        if (route == null || problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        ReportConfirm report = new ReportConfirm(true, ItemMoveGiven.Acts(route, options, given),
            options.Report.Rows);
        return route switch
        {
            ItemMoveRoutes.Slot => Slot(options, report, label),
            ItemMoveRoutes.IntoVault or ItemMoveRoutes.IntoSilo => Deposit(route, options, given, report, label),
            ItemMoveRoutes.VaultToVault => Transfer(options, report, label),
            _ => Take(route, options, report, label)
        };
    }

    private static VerbOutcome Slot(MoveItemOptions options, ReportConfirm report, string label)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        SlotMoveOrders? orders = SlotMoveOrders.Of(options, problems, out VerbError? missing);
        if (orders == null)
        {
            return new VerbOutcome.Refused(missing ?? OptionProblem.Refusal(label, problems));
        }

        bool force = options.Force == true;
        if (orders.AnswersRows)
        {
            return MoveEach(orders.Orders, label);
        }

        return WriteCheck<MovePlan>.Run(() =>
            {
                HostOnly.Require();
                return MovePlanner.TryPlan(Move(orders.Orders[0], force, label), out MovePlan? plan,
                    out ApiException? refusal)
                    ? plan!
                    : throw refusal!;
            })
            .Outcome(report,
                plan => new VerbOutcome.Answered(ItemMoveReplies.Routed(ItemMoveRoutes.Slot, plan.Apply(0))),
                plan => ItemMoveReplies.SlotReport(new[]
                {
                    ItemMoveReplies.SlotTarget(new ThingId(plan.Item.ReferenceId), plan.Item.DisplayName,
                        plan.FromSlot, plan.ToSlot, plan.MergedInto, force)
                }, new List<ReportProblem>()));
    }

    // Each move is checked right before it is made, after the moves before it; each fails alone.
    private static VerbOutcome MoveEach(List<SlotMoveOrder> orders, string label)
    {
        try
        {
            HostOnly.Require();
        }
        catch (ApiException refused)
        {
            return new VerbOutcome.Refused(VerbError.Of(refused));
        }

        BatchBuilder batch = new BatchBuilder(orders.Count);
        foreach (SlotMoveOrder order in orders)
        {
            ThingId? item = null;
            try
            {
                ItemMove move = Move(order, force: false, label);
                item = move.Item;
                batch.Succeeded(MovePlanner.TryPlan(move, out MovePlan? plan, out ApiException? refusal)
                    ? plan!.Apply(order.Index)
                    : throw refusal!);
            }
            catch (ApiException refused)
            {
                batch.Failed(new ItemNotMovedView(order.Index, item, refused));
            }
        }

        return new VerbOutcome.Answered(ItemMoveReplies.Routed(ItemMoveRoutes.Slot, batch.Build()));
    }

    private static ItemMove Move(SlotMoveOrder order, bool force, string label) =>
        new ItemMove(TargetIds.Of(order.Item, label), order.Quantity, HolderId(order.To, label),
            order.Slot is { } index ? new SlotChoice.Exact(index) : new SlotChoice.Auto(), order.Merge, force,
            order.AllowInUse);

    private static VerbOutcome Deposit(string route, MoveItemOptions options, List<string> given,
        ReportConfirm report, string label)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        StoreDepositOrder? order = StoreDepositOrder.Of(options, given, problems, out VerbError? missing);
        if (order == null)
        {
            return new VerbOutcome.Refused(missing ?? OptionProblem.Refusal(label, problems));
        }

        bool vault = route == ItemMoveRoutes.IntoVault;
        VaultMaterial kind = VaultMaterial.Of(options.Category ?? "any") ?? VaultMaterial.Of("any")!;
        bool inUse = ItemMoveGiven.Allows(options, "in_use");
        return Store(route, report, act =>
        {
            ThingId store = TargetIds.Of(options.To!, label);
            DepositAsk ask = Ask(order, options, vault, label);
            return vault
                ? VaultDeposits.Deposit(store, ask, kind, act)
                : SiloDeposits.Deposit(store, ask, inUse, act);
        }, ItemMoveReplies.DepositReport);
    }

    private static DepositAsk Ask(StoreDepositOrder order, MoveItemOptions options, bool vault, string label)
    {
        if (order.Lines is { } lines)
        {
            List<DepositEntry> entries = new List<DepositEntry>(lines.Count);
            foreach (DepositLine line in lines)
            {
                entries.Add(new DepositEntry(TargetIds.Of(line.Id, label), line.Quantity));
            }

            return DepositAsk.ById(entries);
        }

        Selector selector = options.Selector;
        ItemFilter filter = ItemFilter.Of(selector.Prefab, selector.Name, options.Location, selector.Within,
            selector.AroundPlayerM, PointArea.Anywhere);
        int atMost = options.AtMost is { } most ? (int)most : vault ? DepositLimits.Vault : DepositLimits.Silo;
        return DepositAsk.Filtered(filter, atMost);
    }

    private static VerbOutcome Transfer(MoveItemOptions options, ReportConfirm report, string label)
    {
        if (VaultTransferOrder.SameVault(options) is { } same)
        {
            return new VerbOutcome.Refused(same);
        }

        List<OptionProblem> problems = new List<OptionProblem>();
        VaultTransferOrder? order = VaultTransferOrder.Of(options, problems, out VerbError? missing);
        if (order == null)
        {
            return new VerbOutcome.Refused(missing ?? OptionProblem.Refusal(label, problems));
        }

        return Store(ItemMoveRoutes.VaultToVault, report,
            act => VaultTransfers.Transfer(TargetIds.Of(options.Source!, label), TargetIds.Of(options.To!, label),
                order.Asks, act),
            ItemMoveReplies.TransferReport);
    }

    private static VerbOutcome Take(string route, MoveItemOptions options, ReportConfirm report, string label)
    {
        bool vault = route == ItemMoveRoutes.OutOfVault;
        List<OptionProblem> problems = new List<OptionProblem>();
        StoreTakeOrder? order = StoreTakeOrder.Of(options, vault, problems, out VerbError? missing);
        if (order == null)
        {
            return new VerbOutcome.Refused(missing ?? OptionProblem.Refusal(label, problems));
        }

        bool ground = ItemMoveGiven.Allows(options, "ground");
        string? name = (order.Prefab as PrefabPattern.Whole)?.Name;
        int? hash = (order.Prefab as PrefabPattern.Hash)?.Value;
        return Store(route, report, act =>
        {
            ThingId store = TargetIds.Of(options.Source!, label);
            Thing holder = Holder(options.To!, label);
            return vault
                ? VaultWithdrawals.Withdraw(store, name, hash, order.Reagent, order.Quantity, holder, ground, act)
                : SiloWithdrawals.Withdraw(store,
                    name != null ? SiloPrefabChoice.Named(name) : SiloPrefabChoice.Hashed(hash!.Value),
                    order.Quantity, holder, ground, act);
        }, reply => ItemMoveReplies.TakeReport(reply, vault ? "vault" : "silo"));
    }

    // A store move: checked and described without acting for a report; done when it acts.
    private static VerbOutcome Store(string route, ReportConfirm report, System.Func<bool, object> move,
        System.Func<JObject, JObject> describe)
    {
        if (report.Acts)
        {
            try
            {
                return new VerbOutcome.Answered(ItemMoveReplies.Routed(route, move(true)));
            }
            catch (ApiException refused)
            {
                return new VerbOutcome.Refused(VerbError.Of(refused));
            }
        }

        return WriteCheck<JObject>.Run(() => ItemMoveReplies.Routed(route, move(false)))
            .Outcome(report, reply => new VerbOutcome.Answered(reply), describe);
    }

    // "player" is the player's body; anything else a thing's id.
    private static ThingId HolderId(string to, string label) =>
        string.Equals(to, PlayerWord, System.StringComparison.OrdinalIgnoreCase)
            ? new ThingId(PlayerOrigin.RequireHuman().ReferenceId)
            : TargetIds.Of(to, label);

    private static Thing Holder(string to, string label) =>
        string.Equals(to.Trim(), PlayerWord, System.StringComparison.OrdinalIgnoreCase)
            ? PlayerOrigin.RequireHuman()
            : TargetIds.Thing(to, label);

    // What to names: a vault, a silo, or anything else (the player, a holder, or nothing yet found).
    private static StoreKind KindOf(string to, string label)
    {
        if (string.Equals(to.Trim(), PlayerWord, System.StringComparison.OrdinalIgnoreCase) || to.Trim().Length == 0)
        {
            return StoreKind.None;
        }

        if (!GameLookup.TryFindThing(TargetIds.Of(to, label), out Thing thing) || thing.IsBeingDestroyed)
        {
            return StoreKind.None;
        }

        return thing is Silo ? StoreKind.Silo
            : IngotVaults.IsVault(thing) || IngotVaults.IsRemote(thing) ? StoreKind.Vault
            : StoreKind.None;
    }

    // source names a store: a silo, or else a vault (whose lookup refuses anything that is not one).
    private static StoreKind KindOfSource(string source, string label) =>
        KindOf(source, label) == StoreKind.Silo ? StoreKind.Silo : StoreKind.Vault;
}
