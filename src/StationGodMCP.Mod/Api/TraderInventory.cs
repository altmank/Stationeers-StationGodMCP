#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Networks;
using Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Trading;

namespace StationGodMCP.Api;

/// <summary>
/// trader_inventory: what each trader buys and sells, at what price and how many. Read only.
///
/// The game rolls a contact's whole inventory when the contact appears (TraderData builds
/// TraderDataInstance.BuyDataInstances and SellDataInstances from the trader's XML, its chances and world conditions,
/// then applies the slot's bulk multiplier), so this shows it before the trader is interrogated. Price is the
/// transaction's TransactionData.Value, credits per unit (the game's own debug line prints it as
/// "EUR{Value} x {Required}"); a gas unit is the Moles its conditions name. For each line a trader buys, have is how
/// many units it would accept from what you hold (HeldGoods: what trader_sell would take, with the trader's own
/// conditions applied, so a "Box of ..." line counts only the boxes whose contents it accepts) and, while the trader
/// is landed, sellable is the trade window's own count (TradeDataHelper.GetSellItemQuantity: the pad network's vending
/// machines and the player, or the pad network's gas in units).
/// </summary>
internal static class TraderInventoryApi
{
    internal static TraderInventoryView Handle(Args args)
    {
        ThingId? only = args.OptionalThingId("contact_id");
        List<TraderContact> contacts = Contacts.All();
        if (only.HasValue)
        {
            TraderContact? found = Contacts.Find(only.Value.Value);
            if (found == null)
            {
                throw ApiErrors.Refused("contact_not_found", $"No trader contact has reference id {only.Value}.");
            }

            contacts = new List<TraderContact> { found };
        }

        ITradableInventory? holder = HeldGoods.Holder(args.OptionalThingId("credit_card_id"));
        List<LandingPadNetwork> everyPad = HeldGoods.EveryPadNetwork();
        List<TraderStockView> views = new List<TraderStockView>(contacts.Count);
        foreach (TraderContact contact in contacts)
        {
            TraderDataInstance? data = contact.DataInstance;
            bool landed = TradeSession.IsLandedAt(contact, contact.ConnectedPad);
            HeldGoods held = HeldGoods.For(contact, everyPad, holder);
            views.Add(new TraderStockView(Contacts.Identity(contact), contact.Contacted,
                Buys(data, held, landed ? contact : null), Sells(data)));
        }

        return new TraderInventoryView(views);
    }

    // landed is the contact when it is landed and trading, else null: the game's count reads its pad's network.
    private static List<TraderBuysView> Buys(TraderDataInstance? data, HeldGoods held, TraderContact? landed)
    {
        List<TraderBuysView> buys = new List<TraderBuysView>();
        if (data == null)
        {
            return buys;
        }

        foreach (BuyDataInstance buy in data.BuyDataInstances)
        {
            string? prefab = PrefabOf(buy.GetItemPrefab());
            List<string?> conditions = new List<string?>();
            foreach (ConditionData condition in buy.BuyData.Conditions)
            {
                if (condition != null)
                {
                    conditions.Add(Text.Condition(condition.DebugName));
                }
            }

            TradeItem item = new TradeItem(Text.Plain(buy.DisplayName), prefab, buy.BuyData.Value,
                buy.IsGasTransaction());
            int? sellable = landed != null
                ? (int)GameMembers.TradeSellItemQuantity.Invoke(null, buy, landed)
                : null;
            buys.Add(new TraderBuysView(item, buy.Required, conditions, held.Units(buy), sellable));
        }

        return buys;
    }

    private static List<TraderSellsView> Sells(TraderDataInstance? data)
    {
        List<TraderSellsView> sells = new List<TraderSellsView>();
        if (data == null)
        {
            return sells;
        }

        foreach (SellDataInstance sell in data.SellDataInstances)
        {
            TradeItem item = new TradeItem(Text.Plain(sell.DisplayName), PrefabOf(sell.GetItemPrefab()),
                sell.SellData.Value, sell.IsGasTransaction());
            sells.Add(new TraderSellsView(item, sell.Stock, Text.Plain(sell.ToolTip())));
        }

        return sells;
    }

    private static string? PrefabOf(Thing? prefab) => prefab != null ? prefab.PrefabName : null;
}

/// <summary>
/// What trader_inventory's have counts: what trader_sell would take (SellStock), before the trader lands. The goods on
/// the pad network's vending machines and in the card holder's inventory, and the pad network's gas, each only when
/// the trader's conditions accept it (BuyDataInstance.BuyConditionsMet, as TradeDataHelper.HandleSellItem asks). The
/// pad is the contact's own (ConnectedPad, set from the moment it is called to land); for a contact not called yet,
/// every landing pad's network, since the pad it will land at is not known.
/// </summary>
internal sealed class HeldGoods
{
    private readonly List<DynamicThing> _things;
    private readonly List<Atmosphere> _atmospheres;

    private HeldGoods(List<DynamicThing> things, List<Atmosphere> atmospheres)
    {
        _things = things;
        _atmospheres = atmospheres;
    }

    /// <summary>
    /// Whose inventory counts: the given card's holder, as trader_sell takes goods from it, else the player
    /// (PlayerOrigin: who carries the card the trade window uses). Null without a player: pads only.
    /// </summary>
    internal static ITradableInventory? Holder(ThingId? cardId)
    {
        if (cardId.HasValue)
        {
            CreditCard card = TradeSession.RequireCard(cardId);
            SellCard.Require(card);
            return SellCard.HolderOf(card);
        }

        return PlayerOrigin.Current().Player;
    }

    internal static List<LandingPadNetwork> EveryPadNetwork()
    {
        List<LandingPadNetwork> networks = new List<LandingPadNetwork>();
        foreach (Structure structure in GridController.AllStructuresPool.ToList())
        {
            if (structure is LandingPadCenter center && !center.IsCursor && !center.IsBeingDestroyed
                && center.LandingPadNetwork != null && !networks.Contains(center.LandingPadNetwork))
            {
                networks.Add(center.LandingPadNetwork);
            }
        }

        return networks;
    }

    internal static HeldGoods For(TraderContact contact, List<LandingPadNetwork> everyPad,
        ITradableInventory? holder)
    {
        List<DynamicThing> things = new List<DynamicThing>();
        List<Atmosphere> atmospheres = new List<Atmosphere>();
        foreach (LandingPadNetwork network in PadsOf(contact, everyPad))
        {
            things.AddRange(network.GetNetworkInventory());
            if (network.Atmosphere != null && !atmospheres.Contains(network.Atmosphere))
            {
                atmospheres.Add(network.Atmosphere);
            }
        }

        if (holder != null)
        {
            things.AddRange(holder.GetContents());
        }

        return new HeldGoods(Counted(things), atmospheres);
    }

    /// <summary>Units the trader would accept of this line from what is held; a gas line in its units.</summary>
    internal int Units(BuyDataInstance entry)
    {
        if (entry.BuyingItem != null)
        {
            return SellHoldings.Goods(SellStock.Goods(entry, _things));
        }

        List<double> accepted = new List<double>();
        foreach (Atmosphere atmosphere in _atmospheres)
        {
            if (entry.BuyConditionsMet(atmosphere.GasMixture))
            {
                accepted.Add(atmosphere.GasMixture.GetTotalMoles().ToFloat());
            }
        }

        return SellHoldings.Gas(accepted, SellStock.MolesPerUnit(entry));
    }

    private static List<LandingPadNetwork> PadsOf(TraderContact contact, List<LandingPadNetwork> everyPad)
    {
        ITraderDestination? pad = contact.ConnectedPad;
        if (pad == null)
        {
            return everyPad;
        }

        return pad.LandingPadNetwork != null
            ? new List<LandingPadNetwork> { pad.LandingPadNetwork }
            : new List<LandingPadNetwork>();
    }

    // As GetSellItemQuantity counts: only enabled things not being destroyed.
    private static List<DynamicThing> Counted(List<DynamicThing> things)
    {
        List<DynamicThing> counted = new List<DynamicThing>(things.Count);
        foreach (DynamicThing thing in things)
        {
            if (thing != null && thing.enabled && !thing.IsBeingDestroyed)
            {
                counted.Add(thing);
            }
        }

        return counted;
    }
}
