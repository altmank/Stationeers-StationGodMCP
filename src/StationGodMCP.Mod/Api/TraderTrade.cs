#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using GameString = Assets.Scripts.Localization2.GameString;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Newtonsoft.Json.Linq;
using Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using TraderUI;
using Trading;

namespace StationGodMCP.Api;

/// <summary>
/// trader_buy: buy from a landed trader exactly as the trade window's Buy button does on the host
/// (TraderCanvas.BuyItem, which calls TradeDataHelper.BuyItem). Writes; host only.
///
/// The game's buy (CODE, TraderUI.TradeDataHelper): the window opens only while the pad's trader is ready and trading
/// with this contact (ITraderDestination.IsTraderReady, CurrentTradingContact) on a pad that is on and without error.
/// TradeDataHelper.BuyItem refuses more than SellDataInstance.Stock, no card, or a cost above CreditCard.Currency.
/// Goods go into the first empty tradable slot (ITradableInventory.IsSlotTradable) of the vending machines on the
/// pad's data network (LandingPadNetwork.GetVendorsOnNetwork), then of the card holder's inventory, made one by one
/// straight into the slot (TradeDataHelper.Make, OnServer.MoveToSlot) and stacked up to their maximum; it charges
/// only for what it delivered and says the trade was incomplete when it ran out of room. Gas goes into the pad
/// network's atmosphere (AtmosphericEventInstance.CreateAdd). The price is TransactionData.GetCost per unit, divided
/// by DifficultySetting.RespawnStressTradePenalty while the player has respawn stress (TradeItem.SetTradeAmount).
/// The game keeps no trader currency. The unused RequestTradeMessage path is not involved.
/// </summary>
internal static class TraderBuyApi
{
    internal static TradeView Handle(Args args) => TradeSession.Run(args, TradeSide.Buying);
}

/// <summary>
/// trader_sell: sell to a landed trader exactly as the trade window's Sell button does on the host
/// (TraderCanvas.SellItem, TradeDataHelper.SellItem). The mirror of trader_buy: the trader must still want the item
/// (BuyDataInstance.Required); the goods are taken from the pad network's vending machines and then the card holder's
/// inventory (TradeDataHelper.HandleSellItem destroys whole stacks and trims the last); gas is taken from the pad
/// network's atmosphere. The price is BuyData's GetCost per unit, times the respawn stress penalty. The card must be
/// held by a player or a vending machine (SellCard), and lines of one entry are sold together (SellRun).
/// </summary>
internal static class TraderSellApi
{
    internal static TradeView Handle(Args args) => TradeSession.Run(args, TradeSide.Selling);
}

/// <summary>Which way a trade goes: its entries, its price and limit, and the game call that does it.</summary>
internal abstract class TradeSide
{
    internal static readonly TradeSide Buying = new Buy();
    internal static readonly TradeSide Selling = new Sell();

    private TradeSide()
    {
    }

    internal abstract bool IsBuying { get; }

    internal abstract List<TransactionDataInstance> Entries(TraderDataInstance trader);

    /// <summary>The trader's stock (buying) or how many it still wants (selling).</summary>
    internal abstract int Limit(TransactionDataInstance entry);

    internal abstract float UnitPrice(TransactionDataInstance entry, float stressPenalty);

    internal abstract bool Execute(TransactionDataInstance entry, CreditCard card, TraderContact contact, int amount,
        float cost, out GameString? error);

    /// <summary>Refuses a card this side's game call cannot use.</summary>
    internal abstract void RequireUsable(CreditCard card);

    /// <summary>Checks and trades (or, for a dry run, prices) every line of the request.</summary>
    internal abstract BatchResultView Lines(TradeRequest request, TraderContact contact, CreditCard card,
        float stressPenalty);

    private sealed class Buy : TradeSide
    {
        internal override bool IsBuying => true;

        internal override List<TransactionDataInstance> Entries(TraderDataInstance trader) =>
            new List<TransactionDataInstance>(trader.SellDataInstances);

        internal override int Limit(TransactionDataInstance entry) => ((SellDataInstance)entry).Stock;

        internal override float UnitPrice(TransactionDataInstance entry, float stressPenalty) =>
            ((SellDataInstance)entry).SellData.GetCost() * (1f / stressPenalty);

        internal override bool Execute(TransactionDataInstance entry, CreditCard card, TraderContact contact,
            int amount, float cost, out GameString? error)
        {
            bool done = TradeDataHelper.BuyItem((SellDataInstance)entry, card, contact, amount, cost, out GameString e);
            error = e;
            return done;
        }

        // TradeDataHelper.HandleBuyItem skips a card holder that is not a tradable inventory.
        internal override void RequireUsable(CreditCard card)
        {
        }

        // Line by line: the game's buy makes the goods straight into their slots and charges at once, so each line
        // sees what the lines before it did.
        internal override BatchResultView Lines(TradeRequest request, TraderContact contact, CreditCard card,
            float stressPenalty)
        {
            BatchBuilder batch = new BatchBuilder(request.Items.Count);
            for (int index = 0; index < request.Items.Count; index++)
            {
                TradeLineRun.Run(batch, index, request, contact, card, stressPenalty);
            }

            return batch.Build();
        }
    }

    private sealed class Sell : TradeSide
    {
        internal override bool IsBuying => false;

        internal override List<TransactionDataInstance> Entries(TraderDataInstance trader) =>
            new List<TransactionDataInstance>(trader.BuyDataInstances);

        internal override int Limit(TransactionDataInstance entry) => ((BuyDataInstance)entry).Required;

        internal override float UnitPrice(TransactionDataInstance entry, float stressPenalty) =>
            ((BuyDataInstance)entry).BuyData.GetCost() * stressPenalty;

        internal override bool Execute(TransactionDataInstance entry, CreditCard card, TraderContact contact,
            int amount, float cost, out GameString? error)
        {
            bool done = TradeDataHelper.SellItem((BuyDataInstance)entry, card, contact, amount, cost, out GameString e);
            error = e;
            return done;
        }

        internal override void RequireUsable(CreditCard card) => SellCard.Require(card);

        internal override BatchResultView Lines(TradeRequest request, TraderContact contact, CreditCard card,
            float stressPenalty) =>
            SellRun.Run(request, contact, card, stressPenalty);
    }
}

/// <summary>One requested line: which entry (by prefab name or display name) and how many.</summary>
internal sealed class TradeItemRequest
{
    private TradeItemRequest(string? prefabName, string? name, int quantity)
    {
        PrefabName = prefabName;
        Name = name;
        Quantity = quantity;
    }

    internal string? PrefabName { get; }

    internal string? Name { get; }

    internal int Quantity { get; }

    internal static TradeItemRequest Parse(JToken token, int index)
    {
        if (!(token is JObject entry))
        {
            throw ApiErrors.InvalidArgument($"items[{index}] must be an object.");
        }

        Args args = new Args(entry, $"items[{index}]");
        string? prefab = args.OptionalString("prefab_name");
        string? name = args.OptionalString("name");
        if (prefab == null && name == null)
        {
            throw ApiErrors.InvalidArgument($"items[{index}] needs name, prefab_name or both.");
        }

        int quantity = args.OptionalInt("quantity", 1, int.MaxValue)
            ?? throw ApiErrors.InvalidArgument($"items[{index}].quantity is required.");
        return new TradeItemRequest(prefab, name, quantity);
    }
}

/// <summary>A parsed trade request.</summary>
internal sealed class TradeRequest
{
    internal const int MaximumItems = 32;

    private TradeRequest(ThingId contact, ThingId? card, List<TradeItemRequest> items, bool dryRun)
    {
        Contact = contact;
        Card = card;
        Items = items;
        DryRun = dryRun;
    }

    internal ThingId Contact { get; }

    internal ThingId? Card { get; }

    internal List<TradeItemRequest> Items { get; }

    internal bool DryRun { get; }

    internal static TradeRequest Parse(Args args)
    {
        JArray list = args.Array("items", MaximumItems);
        List<TradeItemRequest> items = new List<TradeItemRequest>(list.Count);
        for (int index = 0; index < list.Count; index++)
        {
            items.Add(TradeItemRequest.Parse(list[index], index));
        }

        return new TradeRequest(
            args.ThingId("reference_id"),
            args.OptionalThingId("credit_card_id"),
            items,
            args.OptionalBool("dry_run") ?? false);
    }
}

/// <summary>Runs one trade request: checks the trader, pad and card, then each line in order.</summary>
internal static class TradeSession
{
    internal static TradeView Run(Args args, TradeSide side)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host trades.");
        }

        TradeRequest request = TradeRequest.Parse(args);
        TraderContact contact = RequireTradingContact(request.Contact);
        CreditCard card = RequireCard(request.Card);
        side.RequireUsable(card);
        float creditsBefore = card.Currency;
        DeliverySnapshot delivery = DeliverySnapshot.Take(contact, card);
        BatchResultView results = side.Lines(request, contact, card, StressPenalty());
        List<DeliveredView> delivered = side.IsBuying && !request.DryRun
            ? delivery.Delivered()
            : new List<DeliveredView>();
        Atmosphere? gas = contact.ConnectedPad.LandingPadNetwork?.Atmosphere;
        return new TradeView(request.Contact, Text.Plain(contact.DataInstance.DisplayName), request.DryRun,
            new ThingId(card.ReferenceId), creditsBefore, card.Currency, results, delivered,
            gas == null ? (ThingId?)null : new ThingId(gas.ReferenceId));
    }

    // As the trade window: a contact trading at a ready pad (LandingPadCenter.AttackWith opens it only then, and
    // refuses a pad that is off or in error).
    private static TraderContact RequireTradingContact(ThingId id)
    {
        TraderContact? contact = null;
        foreach (TraderContact candidate in TraderContact.AllStationContacts)
        {
            if (candidate != null && candidate.ReferenceId == id.Value)
            {
                contact = candidate;
            }
        }

        if (contact == null || contact.DataInstance == null)
        {
            throw ApiErrors.Refused("contact_not_found", $"No trader contact has reference id {id}.");
        }

        ITraderDestination pad = contact.ConnectedPad;
        if (!IsLandedAt(contact, pad))
        {
            throw ApiErrors.Refused("not_landed", "This trader is not landed at a pad; " + LandedNow() + ".");
        }

        if (pad is LandingPadCenter center && (!center.OnOff || center.Error != 0))
        {
            throw ApiErrors.Refused("pad_unavailable", "The trader's landing pad is off or has an error.");
        }

        return contact;
    }

    // Landed and trading: the pad's trader is ready (the shuttle has landed, LandingPadCenter.IsTraderReady) and the
    // pad's CurrentTradingContact is this contact, as LandingPadCenter.AttackWith requires to open the trade window.
    internal static bool IsLandedAt(TraderContact contact, ITraderDestination? pad) =>
        pad != null && pad.IsTraderReady && pad.CurrentTradingContact == contact;

    private static string LandedNow()
    {
        foreach (TraderContact candidate in TraderContact.AllStationContacts)
        {
            if (candidate != null && IsLandedAt(candidate, candidate.ConnectedPad))
            {
                string? name = candidate.DataInstance != null ? Text.Plain(candidate.DataInstance.DisplayName) : null;
                return $"the landed trader is {name} ({candidate.ReferenceId})";
            }
        }

        return "no trader is landed";
    }

    // The card given, or the one the local player carries (Human.GetCreditCard), as the trade window picks it.
    private static CreditCard RequireCard(ThingId? id)
    {
        if (id.HasValue)
        {
            if (!GameLookup.TryFindThing(id.Value, out Thing found) || !(found is CreditCard given))
            {
                throw ApiErrors.Refused("card_not_found", $"Reference id {id.Value} is not a credit card.");
            }

            return given;
        }

        Human human = Human.LocalHuman;
        CreditCard? carried = human != null ? human.GetCreditCard() : null;
        if (carried == null)
        {
            throw ApiErrors.Refused("no_credit_card", "The local player carries no credit card.");
        }

        return carried!;
    }

    // DifficultySetting.RespawnStressTradePenalty while the local player has respawn stress, else 1 (TradeItem).
    private static float StressPenalty()
    {
        Human human = Human.LocalHuman;
        return human != null && human.ExperiencingRespawnStress && DifficultySetting.Current != null
            ? (float)DifficultySetting.Current.RespawnStressTradePenalty
            : 1f;
    }
}

/// <summary>
/// One line of a purchase: find the entry, check it, then buy it through the game or price it. Finding an entry and
/// its price serve trader_sell too (SellRun).
/// </summary>
internal static class TradeLineRun
{
    internal static void Run(BatchBuilder batch, int index, TradeRequest request, TraderContact contact,
        CreditCard card, float stressPenalty)
    {
        TradeSide side = TradeSide.Buying;
        TradeItemRequest item = request.Items[index];
        if (!TryFind(side, contact, item, out TransactionDataInstance? entry, out ApiException? refusal))
        {
            batch.Failed(new NotTradedView(index, null, 0, 0f, refusal!));
            return;
        }

        TradeLine line = LineOf(entry!, side, stressPenalty);
        float cost = item.Quantity * line.CreditsEach;
        refusal = Precheck(entry!, item.Quantity, cost, card, contact);
        if (refusal != null)
        {
            batch.Failed(new NotTradedView(index, line, 0, 0f, refusal));
            return;
        }

        if (request.DryRun)
        {
            int after = side.Limit(entry!) - item.Quantity;
            batch.Succeeded(new TradedView(index, line, side.IsBuying, item.Quantity, cost, after));
            return;
        }

        Execute(batch, index, side, entry!, line, item.Quantity, cost, card, contact);
    }

    private static void Execute(BatchBuilder batch, int index, TradeSide side, TransactionDataInstance entry,
        TradeLine line, int quantity, float cost, CreditCard card, TraderContact contact)
    {
        int limitBefore = side.Limit(entry);
        float creditsBefore = card.Currency;
        bool done = side.Execute(entry, card, contact, quantity, cost, out GameString? error);
        int traded = limitBefore - side.Limit(entry);
        float credits = Math.Abs(card.Currency - creditsBefore);
        if (done)
        {
            batch.Succeeded(new TradedView(index, line, side.IsBuying, traded, credits, side.Limit(entry)));
            return;
        }

        string message = error != null ? error.DisplayString : "The game refused the trade.";
        batch.Failed(new NotTradedView(index, line, traded, credits, ApiErrors.Refused("trade_failed", message)));
    }

    // By the entry's name as trader_inventory gives it first, prefab_name only to break a tie (a trader can sell two
    // entries of one prefab, e.g. two ItemGasCanisterEmpty); by prefab_name alone when no name is given. An entry that
    // is still ambiguous is refused, never picked.
    internal static bool TryFind(TradeSide side, TraderContact contact, TradeItemRequest item,
        out TransactionDataInstance? entry, out ApiException? refusal)
    {
        List<TransactionDataInstance> matches = Candidates(side.Entries(contact.DataInstance), item);
        entry = matches.Count == 1 ? matches[0] : null;
        refusal = matches.Count switch
        {
            1 => null,
            0 => ApiErrors.Refused(
                side.IsBuying ? "not_sold" : "not_wanted",
                $"The trader does not {(side.IsBuying ? "sell" : "buy")} {Wanted(item)}."),
            _ => ApiErrors.Refused(
                "ambiguous_item",
                $"{matches.Count} of the trader's entries are {Wanted(item)}; give both name and prefab_name."),
        };
        return entry != null;
    }

    private static List<TransactionDataInstance> Candidates(List<TransactionDataInstance> entries,
        TradeItemRequest item)
    {
        List<TransactionDataInstance> matches = new List<TransactionDataInstance>();
        foreach (TransactionDataInstance candidate in entries)
        {
            bool named = item.Name == null || NameIs(candidate, item.Name);
            if (candidate != null && named && (item.Name != null || PrefabIs(candidate, item.PrefabName!)))
            {
                matches.Add(candidate);
            }
        }

        return matches.Count > 1 && item.Name != null && item.PrefabName != null
            ? ByPrefab(matches, item.PrefabName)
            : matches;
    }

    private static List<TransactionDataInstance> ByPrefab(List<TransactionDataInstance> entries, string prefabName)
    {
        List<TransactionDataInstance> matches = new List<TransactionDataInstance>();
        foreach (TransactionDataInstance entry in entries)
        {
            if (PrefabIs(entry, prefabName))
            {
                matches.Add(entry);
            }
        }

        return matches;
    }

    private static bool NameIs(TransactionDataInstance entry, string name) =>
        string.Equals(Text.Plain(entry.DisplayName), name, StringComparison.OrdinalIgnoreCase);

    private static bool PrefabIs(TransactionDataInstance entry, string prefabName)
    {
        Thing prefab = entry.GetItemPrefab();
        return prefab != null && string.Equals(prefab.PrefabName, prefabName, StringComparison.OrdinalIgnoreCase);
    }

    private static string Wanted(TradeItemRequest item)
    {
        if (item.Name == null)
        {
            return item.PrefabName!;
        }

        return item.PrefabName != null ? $"'{item.Name}' ({item.PrefabName})" : $"'{item.Name}'";
    }

    internal static TradeLine LineOf(TransactionDataInstance entry, TradeSide side, float stressPenalty)
    {
        Thing prefab = entry.GetItemPrefab();
        return new TradeLine(
            Text.Plain(entry.DisplayName), prefab != null ? prefab.PrefabName : null, entry.IsGasTransaction(),
            side.UnitPrice(entry, stressPenalty));
    }

    // The checks the game makes first, so a refusal changes nothing: the stock, the card's credits and a free slot.
    private static ApiException? Precheck(TransactionDataInstance entry, int quantity, float cost, CreditCard card,
        TraderContact contact)
    {
        int limit = TradeSide.Buying.Limit(entry);
        if (quantity > limit)
        {
            return ApiErrors.Refused("insufficient_stock", $"The trader has {limit} in stock.");
        }

        if (cost > card.Currency)
        {
            return ApiErrors.Refused(
                "insufficient_credits", $"This costs {cost:0.00}; the card holds {card.Currency:0.00}.");
        }

        return RoomRefusal(entry, contact, card);
    }

    // TradeDataHelper.HandleBuyItem needs at least one empty tradable slot for an item (gas needs none).
    private static ApiException? RoomRefusal(TransactionDataInstance entry, TraderContact contact, CreditCard card)
    {
        if (entry.IsGasTransaction() || DeliverySnapshot.Take(contact, card).HasEmptySlot())
        {
            return null;
        }

        return ApiErrors.Refused(
            "no_room", "No vending machine on the pad's network, nor the card holder, has an empty tradable slot.");
    }
}

/// <summary>The tradable slots a purchase can land in, taken before the trade to name the goods after.</summary>
internal sealed class DeliverySnapshot
{
    private readonly List<ITradableInventory> _holders;
    private readonly List<Slot> _slots;
    private readonly List<DynamicThing?> _before;

    private DeliverySnapshot(List<ITradableInventory> holders, List<Slot> slots, List<DynamicThing?> before)
    {
        _holders = holders;
        _slots = slots;
        _before = before;
    }

    // The same holders TradeDataHelper.HandleBuyItem fills: the pad network's vendors, then the card's holder.
    internal static DeliverySnapshot Take(TraderContact contact, CreditCard card)
    {
        List<ITradableInventory> vendors = contact.ConnectedPad.LandingPadNetwork != null
            ? contact.ConnectedPad.LandingPadNetwork.GetVendorsOnNetwork()
            : new List<ITradableInventory>();
        if (card.RootParent is ITradableInventory holder)
        {
            vendors.Add(holder);
        }

        List<ITradableInventory> holders = new List<ITradableInventory>();
        List<Slot> slots = new List<Slot>();
        List<DynamicThing?> before = new List<DynamicThing?>();
        foreach (ITradableInventory vendor in vendors)
        {
            AddSlots(vendor, holders, slots, before);
        }

        return new DeliverySnapshot(holders, slots, before);
    }

    private static void AddSlots(ITradableInventory vendor, List<ITradableInventory> holders, List<Slot> slots,
        List<DynamicThing?> before)
    {
        foreach (Slot slot in vendor.GetSlots())
        {
            if (slot != null && vendor.IsSlotTradable(slot) && !slots.Contains(slot))
            {
                holders.Add(vendor);
                slots.Add(slot);
                before.Add(slot.Get());
            }
        }
    }

    internal bool HasEmptySlot()
    {
        for (int index = 0; index < _before.Count; index++)
        {
            if (_before[index] == null)
            {
                return true;
            }
        }

        return false;
    }

    internal List<DeliveredView> Delivered()
    {
        List<DeliveredView> delivered = new List<DeliveredView>();
        for (int index = 0; index < _slots.Count; index++)
        {
            DynamicThing now = _slots[index].Get();
            if (now != null && now != _before[index])
            {
                delivered.Add(new DeliveredView(
                    new ThingId(_holders[index].ReferenceId), _slots[index].SlotIndex, GameLookup.ViewOf(now),
                    now is IQuantity quantity ? quantity.GetQuantity : 1f));
            }
        }

        return delivered;
    }
}
