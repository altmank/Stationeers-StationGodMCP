#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// build item's call, checked without the game: one prefab (a whole name or a hash), the quantity (1 when left out,
/// more than 0), and where the items go: to (a holder's id or "player", with an optional slot) or at (a point in
/// metres), exactly one of them.
/// </summary>
internal sealed class ItemSpawnOrder
{
    private ItemSpawnOrder(PrefabPattern prefab, double quantity, string? to, int? slot, Metres? at)
    {
        Prefab = prefab;
        Quantity = quantity;
        To = to;
        Slot = slot;
        At = at;
    }

    /// <summary>A whole name or a hash.</summary>
    public PrefabPattern Prefab { get; }

    public double Quantity { get; }

    /// <summary>The holder's id or "player"; null when the items go to a point.</summary>
    public string? To { get; }

    /// <summary>A slot index of to; null fills matching stacks, then empty slots.</summary>
    public int? Slot { get; }

    /// <summary>The point the items appear at; null when they go into a holder.</summary>
    public Metres? At { get; }

    internal static ItemSpawnOrder? Of(PrefabFilter? prefab, double? quantity, string? to, long? slot,
        Newtonsoft.Json.Linq.JToken? at, List<OptionProblem> problems)
    {
        int before = problems.Count;
        PrefabPattern? pattern = null;
        if (prefab == null)
        {
            problems.Add(new OptionProblem("prefab", "is needed: the item's prefab name or hash"));
        }
        else if (prefab.Patterns.Count == 1 && prefab.Patterns[0] is PrefabPattern.Whole or PrefabPattern.Hash)
        {
            pattern = prefab.Patterns[0];
        }
        else
        {
            problems.Add(new OptionProblem("prefab", "is one item's whole name or its hash, not a * pattern or a list"));
        }

        double amount = quantity ?? 1.0;
        if (!(amount > 0.0) || double.IsInfinity(amount))
        {
            problems.Add(new OptionProblem("quantity", "must be more than 0"));
        }

        string? holder = to?.Trim();
        if (holder is { Length: 0 })
        {
            holder = null;
        }

        Metres? point = null;
        if (at != null && PlacementOrders.Position(at, "at", problems, out Metres read))
        {
            point = read;
        }

        if ((holder != null) == (at != null))
        {
            problems.Add(new OptionProblem(holder != null ? "at" : "to",
                holder != null
                    ? "goes without to: the items go into a holder or appear at a point, not both"
                    : "or at is needed: the holder that gets the items, or the point they appear at"));
        }

        if (slot != null && holder == null)
        {
            problems.Add(new OptionProblem("slot", "goes with to: a slot of the holder"));
        }

        if (slot is > int.MaxValue)
        {
            problems.Add(new OptionProblem("slot", "is a slot index"));
        }

        return problems.Count == before
            ? new ItemSpawnOrder(pattern!, amount, holder, slot is { } index ? (int)index : (int?)null, point)
            : null;
    }
}

/// <summary>
/// What build item may make: only in a creative world (no player makes items out of nothing), a whole count of an item
/// that counts whole (every item but a consumable's fractional amount), and at most MaximumStacks full stacks in one
/// call, each at most the item's full stack.
/// </summary>
internal static class ItemSpawnRule
{
    /// <summary>The most stacks one call makes: enough for any build's kits, small enough to stay one frame's work.</summary>
    internal const int MaximumStacks = 64;

    /// <summary>Why items are not made in this world; null in a creative world.</summary>
    internal static string? CreativeRefusal(bool creative) =>
        creative
            ? null
            : "making items is only for creative worlds; take them from storage, a vault or a fabricator instead";

    /// <summary>
    /// Why this quantity is refused for an item whose full stack is fullStack (1 for an item that does not stack);
    /// null when it is made.
    /// </summary>
    internal static string? QuantityRefusal(double quantity, double fullStack, bool countsWhole, string name)
    {
        if (countsWhole && Math.Abs(quantity - Math.Round(quantity)) > StackPlacement.Trace)
        {
            return $"{name} counts whole items; quantity {quantity} is not whole.";
        }

        int stacks = Stacks(quantity, fullStack);
        return stacks > MaximumStacks
            ? $"{quantity} {name} is {stacks} stacks of at most {Shown(fullStack)}; one call makes at most " +
              $"{MaximumStacks} stacks."
            : null;
    }

    /// <summary>How many full stacks the quantity fills (the last may be part of one).</summary>
    internal static int Stacks(double quantity, double fullStack)
    {
        double full = fullStack > StackPlacement.Trace ? fullStack : 1.0;
        double stacks = Math.Ceiling(quantity / full - StackPlacement.Trace);
        return stacks > int.MaxValue ? int.MaxValue : Math.Max(1, (int)stacks);
    }

    /// <summary>The stacks made at a point: every one loose, none more than a full stack.</summary>
    internal static StackPlan AtPoint(double quantity, double fullStack) =>
        StackPlacement.Plan(quantity, fullStack, Array.Empty<double>(), 0, allowGround: true);

    private static string Shown(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
