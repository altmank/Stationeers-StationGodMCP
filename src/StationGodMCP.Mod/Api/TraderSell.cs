#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Trading;
using GameString = Assets.Scripts.Localization2.GameString;

namespace StationGodMCP.Api;

/// <summary>
/// trader_sell's lines, run so that no unit is sold twice.
///
/// The game's sell (CODE, TradeDataHelper.HandleSellItem) walks the pad network's vending machines and then the card
/// holder's contents, takes whole stacks with OnServer.Destroy and trims the last one. Destroy only marks the thing
/// (IsBeingDestroyed); it leaves its slot at the end of the frame, and HandleSellItem does not skip a marked thing.
/// Every line of one call runs in the same frame, so a second line of the same entry found the stack the first had
/// sold and was paid for it again (LIVE 2026-09-29: five lines of 1 iron ore paid 5 credits and destroyed 1 ore).
/// A gas sale is an AtmosphericEventInstance.CreateRemove, applied at the next atmospherics tick, so a second gas line
/// saw the same gas. So every line is first checked against what the lines before it claimed (the trader's wanted
/// count, the goods unit by unit, the pad network's gas in moles), and then the lines of one entry are sold together
/// in one game call. Two different entries that accept the same goods are the one case left: before each game call
/// the game's own walk is replayed, and a sale that would take a thing already being destroyed (or one thing twice,
/// when the card holder is also a vending machine on the pad's network) is refused with sell_separately.
/// </summary>
internal static class SellRun
{
    internal static BatchResultView Run(TradeRequest request, TraderContact contact, CreditCard card,
        float stressPenalty)
    {
        SellStock stock = SellStock.Take(contact, card);
        LineOutcomes outcomes = new LineOutcomes(request.Items.Count);
        List<SellGroup> groups = new List<SellGroup>();
        for (int index = 0; index < request.Items.Count; index++)
        {
            TradeItemRequest item = request.Items[index];
            if (!TradeLineRun.TryFind(TradeSide.Selling, contact, item, out TransactionDataInstance? entry,
                    out ApiException? refusal))
            {
                outcomes.Add(new NotTradedView(index, null, 0, 0f, refusal!));
                continue;
            }

            TradeLine line = TradeLineRun.LineOf(entry!, TradeSide.Selling, stressPenalty);
            SellGroup group = GroupOf(groups, (BuyDataInstance)entry!);
            refusal = group.Refusal(item.Quantity, stock);
            if (refusal != null)
            {
                outcomes.Add(new NotTradedView(index, line, 0, 0f, refusal));
                continue;
            }

            group.Add(new SellLine(index, line, item.Quantity), stock);
        }

        foreach (SellGroup group in groups)
        {
            if (request.DryRun)
            {
                group.Succeeded(outcomes);
            }
            else
            {
                group.Sell(outcomes, card, contact, stock);
            }
        }

        return outcomes.Build();
    }

    private static SellGroup GroupOf(List<SellGroup> groups, BuyDataInstance entry)
    {
        SellGroup? group = groups.Find(candidate => ReferenceEquals(candidate.Entry, entry));
        if (group == null)
        {
            group = new SellGroup(entry);
            groups.Add(group);
        }

        return group;
    }
}

/// <summary>One requested sell line that passed its checks.</summary>
internal sealed class SellLine
{
    internal SellLine(int index, TradeLine line, int quantity)
    {
        Index = index;
        Line = line;
        Quantity = quantity;
    }

    internal int Index { get; }

    internal TradeLine Line { get; }

    internal int Quantity { get; }

    internal float Credits => Quantity * Line.CreditsEach;
}

/// <summary>The lines of one call that sell the same trader entry: checked one by one, sold in one game call.</summary>
internal sealed class SellGroup
{
    private readonly List<SellLine> _lines = new List<SellLine>();
    private readonly int _wantedBefore;
    private int _claimed;

    internal SellGroup(BuyDataInstance entry)
    {
        Entry = entry;
        _wantedBefore = entry.Required;
    }

    internal BuyDataInstance Entry { get; }

    // The game's own checks (BuyDataInstance.Required, TradeDataHelper.GetSellItemQuantity), less the earlier lines.
    internal ApiException? Refusal(int quantity, SellStock stock)
    {
        string after = _claimed > 0 ? " after the earlier lines of this call" : "";
        int wanted = _wantedBefore - _claimed;
        if (quantity > wanted)
        {
            return ApiErrors.Refused("not_wanted", $"The trader wants only {wanted} more{after}.");
        }

        int available = stock.Available(Entry);
        return quantity > available
            ? ApiErrors.Refused("insufficient_available",
                $"Only {available} that the trader accepts are {SellStock.Where(Entry)}{after}.")
            : null;
    }

    internal void Add(SellLine line, SellStock stock)
    {
        _lines.Add(line);
        _claimed += line.Quantity;
        stock.Claim(Entry, line.Quantity);
    }

    internal void Sell(LineOutcomes outcomes, CreditCard card, TraderContact contact, SellStock stock)
    {
        if (_lines.Count == 0)
        {
            return;
        }

        if (stock.WouldResell(Entry, _claimed))
        {
            Failed(outcomes, ApiErrors.Refused("sell_separately",
                "An earlier line of this call sold goods the game would take again for this entry (they leave their "
                + "slots only at the end of the frame); sell this entry in a call of its own."));
            return;
        }

        float cost = 0f;
        foreach (SellLine line in _lines)
        {
            cost += line.Credits;
        }

        if (TradeSide.Selling.Execute(Entry, card, contact, _claimed, cost, out GameString? error))
        {
            Succeeded(outcomes);
            return;
        }

        string message = error != null ? error.DisplayString : "The game refused the trade.";
        Failed(outcomes, ApiErrors.Refused("trade_failed", message));
    }

    // Each line with the trader's wanted count after it, as a dry run and a done sale report them.
    internal void Succeeded(LineOutcomes outcomes)
    {
        int wanted = _wantedBefore;
        foreach (SellLine line in _lines)
        {
            wanted -= line.Quantity;
            outcomes.Add(new TradedView(line.Index, line.Line, buying: false, line.Quantity, line.Credits,
                wanted));
        }
    }

    // The game's sell changes nothing when it refuses (HandleSellItem and HandleSellGasMix check before they take).
    private void Failed(LineOutcomes outcomes, ApiException error)
    {
        foreach (SellLine line in _lines)
        {
            outcomes.Add(new NotTradedView(line.Index, line.Line, 0, 0f, error));
        }
    }
}

/// <summary>Each line's result, put back in the order the lines were given.</summary>
internal sealed class LineOutcomes
{
    private readonly BatchItemView?[] _views;

    internal LineOutcomes(int count)
    {
        _views = new BatchItemView?[count];
    }

    internal void Add(BatchItemView view) => _views[view.Index] = view;

    internal BatchResultView Build()
    {
        BatchBuilder batch = new BatchBuilder(_views.Length);
        for (int index = 0; index < _views.Length; index++)
        {
            BatchItemView view = _views[index] ?? throw new InvalidOperationException($"Line {index} has no result.");
            if (view.Ok)
            {
                batch.Succeeded(view);
            }
            else
            {
                batch.Failed(view);
            }
        }

        return batch.Build();
    }
}

/// <summary>
/// What a sale can take, as the game's sell finds it: the goods on the pad network's vending machines and in the card
/// holder's inventory (TradeDataHelper.HandleSellItem; the trade window's own count, GetSellItemQuantity, reads the
/// local player instead, which is the card holder only when the card is theirs), and the pad network's gas.
/// </summary>
internal sealed class SellStock
{
    private readonly TraderContact _contact;
    private readonly ITradableInventory _holder;
    private readonly List<DynamicThing> _counted;
    private readonly SellClaims _claims = new SellClaims();

    private SellStock(TraderContact contact, ITradableInventory holder, List<DynamicThing> counted)
    {
        _contact = contact;
        _holder = holder;
        _counted = counted;
    }

    // Counted as GetSellItemQuantity counts: each thing once, only enabled ones not being destroyed.
    internal static SellStock Take(TraderContact contact, CreditCard card)
    {
        ITradableInventory holder = SellCard.HolderOf(card);
        List<DynamicThing> counted = new List<DynamicThing>();
        HashSet<DynamicThing> seen = new HashSet<DynamicThing>();
        foreach (DynamicThing thing in GameList(contact, holder))
        {
            if (thing != null && thing.enabled && !thing.IsBeingDestroyed && seen.Add(thing))
            {
                counted.Add(thing);
            }
        }

        return new SellStock(contact, holder, counted);
    }

    internal static string Where(BuyDataInstance entry) =>
        entry.BuyingItem == null
            ? "in the pad network's atmosphere"
            : "on the pad's network and with the card holder";

    internal int Available(BuyDataInstance entry)
    {
        if (entry.BuyingItem != null)
        {
            return _claims.Available(Goods(entry, _counted));
        }

        Atmosphere? atmosphere = PadAtmosphere();
        return atmosphere != null && entry.BuyConditionsMet(atmosphere.GasMixture)
            ? _claims.GasUnits(atmosphere.GasMixture.GetTotalMoles().ToFloat(), MolesPerUnit(entry))
            : 0;
    }

    internal void Claim(BuyDataInstance entry, int units)
    {
        if (entry.BuyingItem != null)
        {
            _claims.Claim(Goods(entry, _counted), units);
        }
        else
        {
            _claims.ClaimMoles(units * (double)MolesPerUnit(entry));
        }
    }

    /// <summary>Whether the game's sell of units would take a thing already sold in this call (gas never does).</summary>
    internal bool WouldResell(BuyDataInstance entry, int units) =>
        entry.BuyingItem != null && SellPick.Repeats(Goods(entry, GameList(_contact, _holder)), units);

    private Atmosphere? PadAtmosphere() => _contact.ConnectedPad?.LandingPadNetwork?.Atmosphere;

    // HandleSellItem's list, live and as the game builds it: duplicates kept, nothing skipped.
    private static List<DynamicThing> GameList(TraderContact contact, ITradableInventory holder)
    {
        List<DynamicThing> things = contact.ConnectedPad.LandingPadNetwork != null
            ? contact.ConnectedPad.LandingPadNetwork.GetNetworkInventory()
            : new List<DynamicThing>();
        things.AddRange(holder.GetContents());
        return things;
    }

    private static List<SellGood> Goods(BuyDataInstance entry, List<DynamicThing> things)
    {
        List<SellGood> goods = new List<SellGood>(things.Count);
        foreach (DynamicThing thing in things)
        {
            if (thing is ITradable tradable)
            {
                goods.Add(new SellGood(thing.ReferenceId, (int)Math.Floor(tradable.GetTradableQuantity),
                    entry.BuyConditionsMet(tradable), thing.IsBeingDestroyed));
            }
        }

        return goods;
    }

    // TradeDataHelper.GetQuantityConditionValue: the moles one unit of a gas line is.
    private static float MolesPerUnit(BuyDataInstance entry)
    {
        foreach (ConditionData condition in entry.BuyData.Conditions)
        {
            switch (condition)
            {
                case MoleCondition moles:
                    return moles.Value;
                case QuantityCondition quantity:
                    return quantity.Quantity;
            }
        }

        return 1f;
    }
}

/// <summary>
/// The card a sale may use. TradeDataHelper.SellItem hands HandleSellItem the card's root parent as an
/// ITradableInventory, which only a player (Human) or a vending machine is, and HandleSellItem reads its contents
/// without a null check: with a card in a locker, lying loose, or anywhere else, the game's sell throws.
/// </summary>
internal static class SellCard
{
    internal static void Require(CreditCard card)
    {
        if (!(card.RootParent is ITradableInventory))
        {
            Thing holder = card.RootParent;
            string where = holder == null || holder == card
                ? "lies loose"
                : $"is in {holder.PrefabName} {holder.ReferenceId}";
            throw ApiErrors.Refused("card_not_usable",
                $"Credit card {card.ReferenceId} {where}. The game's sell takes goods from the card holder too and "
                + "works only with a card a player carries or a vending machine holds.");
        }
    }

    internal static ITradableInventory HolderOf(CreditCard card) =>
        card.RootParent as ITradableInventory
        ?? throw ApiErrors.Refused("card_not_usable", $"Credit card {card.ReferenceId} has no usable holder.");
}
