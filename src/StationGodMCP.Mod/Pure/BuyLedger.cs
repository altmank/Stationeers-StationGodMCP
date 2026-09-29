#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// How bought units take up the destination's empty tradable slots, as TradeDataHelper.HandleBuyItem fills them: gas
/// needs none; an item that is neither a Stackable nor an Ingot takes one slot per unit; a stackable unit adds its
/// made quantity to the slot's stack up to the maximum and carries the rest into the next empty slot, so n units take
/// ceil(n * quantity / maximum) slots, and with too few slots the game stops at the last whole unit that fits. The
/// game only ever makes into empty slots, so the room is the number of empty slots.
/// </summary>
internal sealed class BuyRoom
{
    private const double Tolerance = 1e-9;

    private readonly double _quantityPerUnit;
    private readonly double _slotCapacity;

    private BuyRoom(bool needsSlots, double quantityPerUnit, double slotCapacity)
    {
        NeedsSlots = needsSlots;
        _quantityPerUnit = quantityPerUnit;
        _slotCapacity = slotCapacity;
    }

    /// <summary>Gas goes into the pad network's atmosphere.</summary>
    internal static readonly BuyRoom Gas = new BuyRoom(false, 0d, 1d);

    /// <summary>One unit per slot.</summary>
    internal static readonly BuyRoom OnePerSlot = new BuyRoom(true, 1d, 1d);

    internal bool NeedsSlots { get; }

    /// <summary>
    /// A stack: each unit is made with quantityPerUnit (whole units, as the game floors it), a slot holds maxQuantity.
    /// A unit made larger than a slot still takes one slot.
    /// </summary>
    internal static BuyRoom Stacked(double quantityPerUnit, double maxQuantity) =>
        maxQuantity > 0d
            ? new BuyRoom(true, Math.Max(0d, Math.Min(quantityPerUnit, maxQuantity)), maxQuantity)
            : OnePerSlot;

    /// <summary>The whole units that fit into freeSlots empty slots.</summary>
    internal int UnitsFitting(int freeSlots)
    {
        if (!NeedsSlots || (_quantityPerUnit <= 0d && freeSlots > 0))
        {
            return int.MaxValue;
        }

        double units = Math.Floor(freeSlots * _slotCapacity / _quantityPerUnit + Tolerance);
        return units >= int.MaxValue ? int.MaxValue : (int)units;
    }

    /// <summary>The empty slots units take.</summary>
    internal int SlotsFor(int units) =>
        !NeedsSlots || units <= 0
            ? 0
            : _quantityPerUnit <= 0d
                ? 1
                : (int)Math.Ceiling(units * _quantityPerUnit / _slotCapacity - Tolerance);
}

/// <summary>What a buy line would do, judged after the lines before it. A closed set.</summary>
internal abstract class BuyVerdict
{
    private BuyVerdict()
    {
    }

    /// <summary>The whole line goes through.</summary>
    internal sealed class Bought : BuyVerdict
    {
        internal Bought(int quantity, float credits, int stockAfter)
        {
            Quantity = quantity;
            Credits = credits;
            StockAfter = stockAfter;
        }

        internal int Quantity { get; }

        internal float Credits { get; }

        internal int StockAfter { get; }
    }

    /// <summary>The game refuses the line up front and changes nothing.</summary>
    internal sealed class Refused : BuyVerdict
    {
        internal Refused(string code, string message)
        {
            Code = code;
            Message = message;
        }

        internal string Code { get; }

        internal string Message { get; }
    }

    /// <summary>
    /// The room runs out part way: the game buys the units that fit, charges for those and calls the trade
    /// incomplete (trade_failed).
    /// </summary>
    internal sealed class Partial : BuyVerdict
    {
        internal Partial(int quantity, float credits, string message)
        {
            Quantity = quantity;
            Credits = credits;
            Message = message;
        }

        internal int Quantity { get; }

        internal float Credits { get; }

        internal string Message { get; }
    }
}

/// <summary>
/// trader_buy's dry run: the lines of one call added up as a real run would meet them, one after the other. Each line
/// is judged with the game's own checks in the game's order (TradeDataHelper.BuyItem: stock, then credits, then a free
/// slot) against what the lines before it left: the card's credits, each entry's stock and the destination's empty
/// slots. What a line takes is taken, so a later line sees it.
/// </summary>
internal sealed class BuyLedger<TEntry> where TEntry : class
{
    private readonly Dictionary<TEntry, int> _bought = new Dictionary<TEntry, int>();
    private float _credits;
    private int _freeSlots;
    private bool _changed;

    internal BuyLedger(float credits, int freeSlots)
    {
        _credits = credits;
        _freeSlots = freeSlots;
    }

    /// <summary>The card's credits after the lines judged so far.</summary>
    internal float Credits => _credits;

    internal int FreeSlots => _freeSlots;

    /// <summary>Judges the next line and, when it would buy anything, takes what it would.</summary>
    internal BuyVerdict Take(TEntry entry, int stockBefore, int quantity, float cost, BuyRoom room)
    {
        string after = _changed ? " after the earlier lines of this call" : "";
        int stock = stockBefore - (_bought.TryGetValue(entry, out int bought) ? bought : 0);
        if (quantity > stock)
        {
            // Only an earlier line that bought from this entry changed its stock.
            return new BuyVerdict.Refused("insufficient_stock",
                $"The trader has {stock} in stock{(bought > 0 ? " after the earlier lines of this call" : "")}.");
        }

        if (cost > _credits)
        {
            return new BuyVerdict.Refused("insufficient_credits",
                $"This costs {cost:0.00}; the card holds {_credits:0.00}{after}.");
        }

        if (room.NeedsSlots && _freeSlots <= 0)
        {
            return new BuyVerdict.Refused("no_room",
                $"No vending machine on the pad's network, nor the card holder, has an empty tradable slot{after}.");
        }

        int units = Math.Min(quantity, room.UnitsFitting(_freeSlots));
        float charged = units == quantity ? cost : cost * ((float)units / quantity);
        _bought[entry] = bought + units;
        _credits -= charged;
        _freeSlots -= Math.Min(_freeSlots, room.SlotsFor(units));
        _changed = true;
        return units == quantity
            ? new BuyVerdict.Bought(units, charged, stock - units)
            : new BuyVerdict.Partial(units, charged,
                $"Room for only {units} of {quantity}{after}: the game buys {units}, charges for them and calls "
                + "the trade incomplete.");
    }
}
