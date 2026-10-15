#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Networks;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Trading;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;
using Trading;

namespace StationGodMCP.Verbs;

/// <summary>
/// trade inventory (catalogue trade.json): what each trader buys and sells, at what price and how many. id is one
/// contact, a list (one row per contact, each failing alone) or left out for every contact; card is the card whose
/// holder's goods count. Read only.
///
/// The game rolls a contact's whole inventory when the contact appears (TraderData builds
/// TraderDataInstance.BuyDataInstances and SellDataInstances from the trader's XML, its chances and world conditions,
/// then applies the slot's bulk multiplier), so this shows it before the trader is interrogated. Price is the
/// transaction's TransactionData.Value, credits per unit (the game's own debug line prints it as
/// "EUR{Value} x {Required}"); a gas unit is the Moles its conditions name. For each line a trader buys, have is how
/// many units it would accept from what you hold (HeldGoods: what trade sell would take, with the trader's own
/// conditions applied, so a "Box of ..." line counts only the boxes whose contents it accepts) and, while the trader
/// is landed, sellable is the trade window's own count (TradeDataHelper.GetSellItemQuantity: the pad network's vending
/// machines and the player, or the pad network's gas in units).
/// </summary>
internal static class TradeInventory
{
    internal static VerbOutcome Run(TradeInventoryOptions options)
    {
        TradeStockOrder order = TradeStockOrder.Of(options);
        ITradableInventory? holder = HeldGoods.Holder(TradeSession.CardId(order.Card));
        List<LandingPadNetwork> everyPad = HeldGoods.EveryPadNetwork();
        if (order.Ids == null)
        {
            List<TraderContact> contacts = Contacts.All();
            return WorkResult<List<TraderStockView>>.Of(answer => Stocks(contacts, everyPad, holder, answer),
                stocks => new VerbOutcome.Answered(
                    JObject.FromObject(new TraderInventoryView(stocks), ApiJson.Fresh())));
        }

        return IdWork.Of<TraderInventoryView>(order.Ids, id =>
        {
            TraderContact contact = Contacts.Require(id);
            return (contact.ReferenceId.ToString(), answer => One(contact, everyPad, holder, answer));
        });
    }

    // Every contact's stock, a contact per step.
    private static IEnumerable<object?> Stocks(List<TraderContact> contacts, List<LandingPadNetwork> everyPad,
        ITradableInventory? holder, Answer<List<TraderStockView>> answer)
    {
        List<TraderStockView> stocks = new List<TraderStockView>(contacts.Count);
        foreach (TraderContact contact in contacts)
        {
            stocks.Add(Stock(contact, everyPad, holder));
            yield return null;
        }

        answer.Value = stocks;
    }

    private static IEnumerable<object?> One(TraderContact contact, List<LandingPadNetwork> everyPad,
        ITradableInventory? holder, Answer<TraderInventoryView> answer)
    {
        answer.Value = new TraderInventoryView(new List<TraderStockView> { Stock(contact, everyPad, holder) });
        yield break;
    }

    // One contact's stock, as the view the reply carries: the contact, its pads, and the goods held that meet it.
    private static TraderStockView Stock(TraderContact contact, List<LandingPadNetwork> everyPad,
        ITradableInventory? holder)
    {
        TraderDataInstance? data = contact.DataInstance;
        bool landed = TradeSession.IsLandedAt(contact, contact.ConnectedPad);
        HeldGoods held = HeldGoods.For(contact, everyPad, holder);
        return new TraderStockView(Contacts.Identity(contact), contact.Contacted,
            Buys(data, held, landed ? contact : null), Sells(data));
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
