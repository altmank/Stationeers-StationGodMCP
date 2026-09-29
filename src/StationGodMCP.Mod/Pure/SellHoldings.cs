#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// How many units a trader would accept from what is held, counted as the game's sell counts them
/// (TradeDataHelper.GetSellItemQuantity and HandleSellItem): trader_inventory's have.
/// </summary>
internal static class SellHoldings
{
    /// <summary>
    /// Units of goods: whole units of each good the trader's conditions accept, each good once (a vending machine
    /// that holds the card is listed twice), none the game is already destroying.
    /// </summary>
    internal static int Goods(IReadOnlyList<SellGood> goods) => new SellClaims().Available(goods);

    /// <summary>
    /// Gas units over the pad atmospheres whose gas the trader accepts: whole units of each, added up. A sale takes
    /// from one pad's atmosphere, so the moles of two atmospheres never pool into one more unit.
    /// </summary>
    internal static int Gas(IEnumerable<double> acceptedMoles, double molesPerUnit)
    {
        long units = 0;
        foreach (double moles in acceptedMoles)
        {
            units += new SellClaims().GasUnits(moles, molesPerUnit);
        }

        return (int)Math.Min(units, int.MaxValue);
    }
}
