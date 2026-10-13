#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Networks;
using Objects.Electrical;
using StationGodMCP.Pure;
using Trading;

namespace StationGodMCP.Api.Shared.Game.Trading;

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
