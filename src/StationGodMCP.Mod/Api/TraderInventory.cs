#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using Trading;

namespace StationGodMCP.Api;

/// <summary>
/// trader_inventory: what each trader buys and sells, at what price and how many. Read only.
///
/// The game rolls a contact's whole inventory when the contact appears (TraderData builds
/// TraderDataInstance.BuyDataInstances and SellDataInstances from the trader's XML, its chances and world conditions,
/// then applies the slot's bulk multiplier), so this shows it before the trader is interrogated. Price is the
/// transaction's TransactionData.Value, credits per unit (the game's own debug line prints it as
/// "EUR{Value} x {Required}"); a gas unit is the Moles its conditions name. For each item a trader buys, have is how
/// many of its prefab exist in the world outside the trader as items (find_items' rules: anywhere, any holder; not
/// machine stock, which no trader can take until it is ejected as ingots; 0 when none, null for gas; the trader's
/// conditions are not applied, so every "Box of ..." line counts all CardboardBox items) and, while the trader
/// is landed, how many it would take now (sellable: the trade window's own count, TradeDataHelper.GetSellItemQuantity:
/// what the pad network's vending machines and the local player hold that meets its conditions, or the pad network's
/// gas in units).
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

        Dictionary<string, double> have = HaveByPrefab();
        List<TraderStockView> views = new List<TraderStockView>(contacts.Count);
        foreach (TraderContact contact in contacts)
        {
            TraderDataInstance? data = contact.DataInstance;
            bool landed = TradeSession.IsLandedAt(contact, contact.ConnectedPad);
            views.Add(new TraderStockView(Contacts.Identity(contact), contact.Contacted,
                Buys(data, have, landed ? contact : null), Sells(data)));
        }

        return new TraderInventoryView(views);
    }

    private static Dictionary<string, double> HaveByPrefab()
    {
        Dictionary<string, double> have = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (ItemRecord record in WorldItems.Collect(ItemFilter.Everything, PlayerOrigin.Current()))
        {
            string prefab = record.Item.PrefabName;
            have[prefab] = (have.TryGetValue(prefab, out double count) ? count : 0.0) + record.Quantity;
        }

        return have;
    }

    // landed is the contact when it is landed and trading, else null: the game's count reads its pad's network.
    private static List<TraderBuysView> Buys(TraderDataInstance? data, Dictionary<string, double> have,
        TraderContact? landed)
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
            // Counted by prefab: 0 when none exist, null only for a line with no item prefab (gas).
            double? count = prefab == null ? null : have.TryGetValue(prefab, out double held) ? held : 0.0;
            int? sellable = landed != null
                ? (int)GameMembers.TradeSellItemQuantity.Invoke(null, buy, landed)
                : null;
            buys.Add(new TraderBuysView(item, buy.Required, conditions, count, sellable));
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
