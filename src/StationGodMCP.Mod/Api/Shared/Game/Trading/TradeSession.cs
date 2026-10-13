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
using Objects.Electrical;
using Objects.Items;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using TraderUI;
using Trading;

namespace StationGodMCP.Api.Shared.Game.Trading;

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

    /// <summary>Checks and trades (or, for a report, prices) every line of the request.</summary>
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
        // sees what the lines before it did. A report adds the lines up the same way (BuyDryRun).
        internal override BatchResultView Lines(TradeRequest request, TraderContact contact, CreditCard card,
            float stressPenalty)
        {
            if (request.DryRun)
            {
                return BuyDryRun.Run(request, contact, card, stressPenalty);
            }

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
    internal TradeItemRequest(string? prefabName, string? name, int quantity)
    {
        PrefabName = prefabName;
        Name = name;
        Quantity = quantity;
    }

    internal string? PrefabName { get; }

    internal string? Name { get; }

    internal int Quantity { get; }
}

/// <summary>
/// A trade call's one trade: the contact, the card, the lines in order, and whether it only prices them (a report: no
/// dry_run key; the call's confirm decides).
/// </summary>
internal sealed class TradeRequest
{
    internal TradeRequest(ThingId contact, ThingId? card, List<TradeItemRequest> items, bool dryRun)
    {
        Contact = contact;
        Card = card;
        Items = items;
        DryRun = dryRun;
    }

    internal ThingId Contact { get; }

    internal ThingId? Card { get; }

    internal List<TradeItemRequest> Items { get; }

    /// <summary>True on a report: every line is checked and priced, and nothing is traded.</summary>
    internal bool DryRun { get; }
}

/// <summary>Runs one trade: the trader, pad and card checked, then each line in order.</summary>
internal static class TradeSession
{
    /// <summary>Refused not_host on a multiplayer client: only the host trades (what trade buy and sell check first).</summary>
    internal static void RequireHost()
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host trades.");
        }
    }

    /// <summary>The one trade of a request, its contact and card already resolved: the card's own check, then each line.</summary>
    internal static TradeView Run(TradeRequest request, TradeSide side, TraderContact contact, CreditCard card)
    {
        side.RequireUsable(card);
        float creditsBefore = card.Currency;
        DeliverySnapshot delivery = DeliverySnapshot.Take(contact, card);
        BatchResultView results = side.Lines(request, contact, card, StressPenalty());
        List<DeliveredView> delivered = side.IsBuying && !request.DryRun
            ? delivery.Delivered()
            : new List<DeliveredView>();
        Atmosphere? gas = contact.ConnectedPad.LandingPadNetwork?.Atmosphere;
        float creditsAfter = request.DryRun
            ? TradeView.PredictedCredits(creditsBefore, results.Results, side.IsBuying)
            : card.Currency;
        return new TradeView(request.Contact, Text.Plain(contact.DataInstance.DisplayName),
            new ThingId(card.ReferenceId), creditsBefore, creditsAfter, results, delivered,
            gas == null ? (ThingId?)null : new ThingId(gas.ReferenceId));
    }

    /// <summary>
    /// The card an option names: the id the text gives, or card_not_found when the text is no reference id (the card
    /// option as the trade whats read it). Left out, null: the card the player carries.
    /// </summary>
    internal static ThingId? CardId(string? text)
    {
        if (text == null)
        {
            return null;
        }

        return ThingId.TryRead(new JValue(text), out ThingId id)
            ? id
            : throw ApiErrors.Refused("card_not_found",
                $"'{text}' is not a credit card: card is the card's reference id.");
    }

    // As the trade window: a contact trading at a ready pad (LandingPadCenter.AttackWith opens it only then, and
    // refuses a pad that is off or in error).
    internal static TraderContact RequireTradingContact(ThingId id)
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

        // The pad's On follows its power (LandingPadCenter.RefreshPadPower: on while a finished data and power
        // connection on its network is powered), so off is the game's "unpowered" (CanTraderLand).
        if (pad is LandingPadCenter center && (!center.OnOff || center.Error != 0))
        {
            throw ApiErrors.Refused("pad_unavailable", !center.OnOff
                ? "The trader's landing pad is off: unpowered (no powered data and power connection on its network)."
                : "The trader's landing pad has an error.");
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

    // The card given, or the one the player carries (Human.GetCreditCard), as the trade window picks it.
    internal static CreditCard RequireCard(ThingId? id)
    {
        if (id.HasValue)
        {
            if (!GameLookup.TryFindThing(id.Value, out Thing found) || !(found is CreditCard given))
            {
                throw ApiErrors.Refused("card_not_found", $"Reference id {id.Value} is not a credit card.");
            }

            return given;
        }

        PlayerOrigin player = PlayerOrigin.Current();
        Human? human = player.Player;
        CreditCard? carried = human != null ? human.GetCreditCard() : null;
        if (carried == null)
        {
            throw ApiErrors.Refused("no_credit_card", human != null
                ? $"The player ({human.DisplayName}) carries no credit card; pass card."
                : $"{player.Absence} No player carries a credit card; pass card.");
        }

        return carried!;
    }

    // DifficultySetting.RespawnStressTradePenalty while the player has respawn stress, else 1 (TradeItem).
    private static float StressPenalty()
    {
        Human? human = PlayerOrigin.Current().Player;
        return human != null && human.ExperiencingRespawnStress && DifficultySetting.Current != null
            ? (float)DifficultySetting.Current.RespawnStressTradePenalty
            : 1f;
    }
}

/// <summary>
/// trade buy's report: every line priced and checked in order against what the lines before it would leave (the
/// card's credits, the trader's stock, the empty tradable slots), so it reports the lines a real run would refuse.
/// </summary>
internal static class BuyDryRun
{
    internal static BatchResultView Run(TradeRequest request, TraderContact contact, CreditCard card,
        float stressPenalty)
    {
        BuyLedger<TransactionDataInstance> ledger =
            new BuyLedger<TransactionDataInstance>(card.Currency, DeliverySnapshot.Take(contact, card).EmptySlots());
        BatchBuilder batch = new BatchBuilder(request.Items.Count);
        for (int index = 0; index < request.Items.Count; index++)
        {
            TradeItemRequest item = request.Items[index];
            if (!TradeLineRun.TryFind(TradeSide.Buying, contact, item, out TransactionDataInstance? entry,
                    out ApiException? refusal))
            {
                batch.Failed(new NotTradedView(index, null, 0, 0f, refusal!));
                continue;
            }

            TradeLine line = TradeLineRun.LineOf(entry!, TradeSide.Buying, stressPenalty);
            BuyVerdict verdict = ledger.Take(entry!, TradeSide.Buying.Limit(entry!), item.Quantity,
                item.Quantity * line.CreditsEach, RoomOf((SellDataInstance)entry!));
            switch (verdict)
            {
                case BuyVerdict.Bought bought:
                    batch.Succeeded(new TradedView(index, line, buying: true, bought.Quantity, bought.Credits,
                        bought.StockAfter));
                    break;
                case BuyVerdict.Refused refused:
                    batch.Failed(new NotTradedView(index, line, 0, 0f,
                        ApiErrors.Refused(refused.Code, refused.Message)));
                    break;
                case BuyVerdict.Partial partial:
                    batch.Failed(new NotTradedView(index, line, partial.Quantity, partial.Credits,
                        ApiErrors.Refused("trade_failed", partial.Message)));
                    break;
            }
        }

        return batch.Build();
    }

    // HandleBuyGasMix when the entry sells no item; HandleBuyItem stacks only a Stackable or an Ingot, each unit made
    // with the quantity its actions give it (floored), one slot per unit for anything else.
    private static BuyRoom RoomOf(SellDataInstance entry)
    {
        if (entry.SellingItem == null)
        {
            return BuyRoom.Gas;
        }

        Thing prefab = entry.SellingItem.Prefab;
        if (!(prefab is Stackable || prefab is Ingot) || !(prefab is IQuantity stack))
        {
            return BuyRoom.OnePerSlot;
        }

        return BuyRoom.Stacked(Math.Floor(MadeQuantity(entry, stack)), stack.GetMaxQuantity);
    }

    // The prefab's quantity after SellDataInstance.ExecuteActions: the trade's actions, then the item's own.
    private static double MadeQuantity(SellDataInstance entry, IQuantity stack)
    {
        double quantity = stack.GetQuantity;
        List<ActionData> actions = new List<ActionData>(entry.SellData.TradeActions);
        actions.AddRange(entry.SellingItem.Actions);
        foreach (ActionData action in actions)
        {
            quantity = action switch
            {
                QuantityAction set => Math.Min(set.Value, stack.GetMaxQuantity),
                PercentAction percent when stack is Consumable consumable => percent.Value * consumable.MaxQuantity,
                _ => quantity,
            };
        }

        return quantity;
    }
}

/// <summary>
/// One line of a real purchase: find the entry, check it, then buy it through the game. Finding an entry and its
/// price serve the report (BuyDryRun) and trade sell (SellRun) too.
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

        Execute(batch, index, side, entry!, line, item.Quantity, cost, card, contact);
    }

    private static void Execute(BatchBuilder batch, int index, TradeSide side, TransactionDataInstance entry,
        TradeLine line, int quantity, float cost, CreditCard card, TraderContact contact)
    {
        int limitBefore = side.Limit(entry);
        float creditsBefore = card.Currency;
        bool done = side.Execute(entry, card, contact, quantity, cost, out GameString? error);
        int traded = limitBefore - side.Limit(entry);
        // Signed as the report reports it: a negative-price line (the trader pays you to take it) spends less than 0.
        float credits = side.IsBuying ? creditsBefore - card.Currency : card.Currency - creditsBefore;
        if (done)
        {
            batch.Succeeded(new TradedView(index, line, side.IsBuying, traded, credits, side.Limit(entry)));
            return;
        }

        string message = (error != null ? Text.Plain(error.DisplayString) : null) ?? "The game refused the trade.";
        batch.Failed(new NotTradedView(index, line, traded, credits, ApiErrors.Refused("trade_failed", message)));
    }

    // By the entry's name as trade inventory gives it first, prefab only to break a tie (a trader can sell two
    // entries of one prefab, e.g. two ItemGasCanisterEmpty); by prefab alone when no name is given. An entry that
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
                $"{matches.Count} of the trader's entries are {Wanted(item)}; give both name and prefab."),
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

    internal bool HasEmptySlot() => EmptySlots() > 0;

    internal int EmptySlots() => _before.FindAll(thing => thing == null).Count;

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
