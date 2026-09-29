#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// One thing a trader sale could take: its reference id, how many whole units it counts for (the game floors
/// ITradable.GetTradableQuantity), whether the trader's conditions accept it, and whether the game is already
/// destroying it (Thing.IsBeingDestroyed: OnServer.Destroy sets it at once, the thing leaves its slot only at the end
/// of the frame).
/// </summary>
internal sealed class SellGood
{
    internal SellGood(long id, int units, bool accepted, bool destroyed)
    {
        Id = id;
        Units = units;
        Accepted = accepted;
        Destroyed = destroyed;
    }

    internal long Id { get; }

    internal int Units { get; }

    internal bool Accepted { get; }

    internal bool Destroyed { get; }
}

/// <summary>
/// What the earlier lines of one trader_sell call have promised, so no unit is counted twice: units per good, taken in
/// the order the game's sell walks the goods, and moles of the pad network's gas.
/// </summary>
internal sealed class SellClaims
{
    private readonly Dictionary<long, int> _units = new Dictionary<long, int>();

    internal double Moles { get; private set; }

    /// <summary>Units still free: each accepted good once, none the game is destroying, less what was claimed.</summary>
    internal int Available(IReadOnlyList<SellGood> goods)
    {
        int available = 0;
        HashSet<long> seen = new HashSet<long>();
        foreach (SellGood good in goods)
        {
            if (Counts(good) && seen.Add(good.Id))
            {
                available += Free(good);
            }
        }

        return available;
    }

    /// <summary>Claims units good by good in the given order, as TradeDataHelper.HandleSellItem takes them.</summary>
    internal void Claim(IReadOnlyList<SellGood> goods, int units)
    {
        int left = units;
        HashSet<long> seen = new HashSet<long>();
        foreach (SellGood good in goods)
        {
            if (left <= 0)
            {
                return;
            }

            if (Counts(good) && seen.Add(good.Id))
            {
                int taken = Math.Min(left, Free(good));
                _units[good.Id] = Claimed(good.Id) + taken;
                left -= taken;
            }
        }
    }

    internal void ClaimMoles(double moles) => Moles += moles;

    /// <summary>
    /// Gas units a line can still sell: the game's count (whole units of molesPerUnit in the atmosphere's total) of
    /// what the earlier lines have not claimed.
    /// </summary>
    internal int GasUnits(double totalMoles, double molesPerUnit)
    {
        if (!(molesPerUnit > 0.0))
        {
            return 0;
        }

        double units = Math.Floor((totalMoles - Moles) / molesPerUnit);
        return units > 0.0 ? (int)Math.Min(units, int.MaxValue) : 0;
    }

    private static bool Counts(SellGood good) => good.Accepted && !good.Destroyed && good.Units > 0;

    private int Free(SellGood good) => Math.Max(0, good.Units - Claimed(good.Id));

    private int Claimed(long id) => _units.TryGetValue(id, out int claimed) ? claimed : 0;
}

/// <summary>What the game's own sell would take, walked exactly as TradeDataHelper.HandleSellItem walks it.</summary>
internal static class SellPick
{
    /// <summary>
    /// Whether selling units from the goods as the game lists them (the pad network's vending machines, then the card
    /// holder's contents, duplicates kept: a holder on the network is listed twice) would take a good already being
    /// destroyed or the same good twice: the game would then pay for units it does not remove.
    /// </summary>
    internal static bool Repeats(IReadOnlyList<SellGood> goods, int units)
    {
        int left = units;
        HashSet<long> taken = new HashSet<long>();
        foreach (SellGood good in goods)
        {
            if (left == 0)
            {
                return false;
            }

            // A good of no whole unit is taken for nothing, so taking it again costs nothing either.
            if (!good.Accepted || good.Units <= 0)
            {
                continue;
            }

            bool again = !taken.Add(good.Id);
            if (good.Destroyed || again)
            {
                return true;
            }

            left = left >= good.Units ? left - good.Units : 0;
        }

        return false;
    }
}
