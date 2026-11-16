#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>One line of a build item call: a prefab (a whole name or a hash), how many (more than 0) and the slot it goes into.</summary>
internal sealed record ItemLine(PrefabPattern Prefab, double Quantity, SlotRequest Slot);

/// <summary>
/// build item's call, checked without the game: the lines to make (one prefab and quantity, or an items list of up to
/// MaximumLines lines, never both) and where they go: to (a holder's id or "player", each line with an optional slot)
/// or at (a point in metres, one prefab only), exactly one of them. A line's quantity is 1 when left out.
/// </summary>
internal sealed class ItemSpawnOrder
{
    /// <summary>The most lines one items list holds.</summary>
    internal const int MaximumLines = 64;

    private static readonly string[] LineOptions = { "prefab", "quantity", "slot" };

    private ItemSpawnOrder(IReadOnlyList<ItemLine> lines, bool fromList, string? to, Metres? at)
    {
        Lines = lines;
        FromList = fromList;
        To = to;
        At = at;
    }

    /// <summary>The lines in order; one for the prefab and quantity form.</summary>
    public IReadOnlyList<ItemLine> Lines { get; }

    /// <summary>Whether the lines came from items (the reply's rows then carry line numbers and no one prefab).</summary>
    public bool FromList { get; }

    /// <summary>The holder's id or "player"; null when the items go to a point.</summary>
    public string? To { get; }

    /// <summary>The point the items appear at; null when they go into a holder.</summary>
    public Metres? At { get; }

    internal static ItemSpawnOrder? Of(PrefabFilter? prefab, double? quantity, string? to, JToken? slot,
        Newtonsoft.Json.Linq.JToken? at, JToken? items, List<OptionProblem> problems)
    {
        int before = problems.Count;
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

        List<ItemLine> lines = new List<ItemLine>();
        if (items != null)
        {
            ListLines(items, prefab, quantity, slot, at, holder, lines, problems);
        }
        else
        {
            if (slot != null && holder == null)
            {
                problems.Add(new OptionProblem("slot", "goes with to: a slot of the holder"));
            }

            if (Line(prefab, "prefab", quantity, "quantity", slot, "slot", problems) is { } line)
            {
                lines.Add(line);
            }
        }

        return problems.Count == before ? new ItemSpawnOrder(lines, items != null, holder, point) : null;
    }

    private static void ListLines(JToken items, PrefabFilter? prefab, double? quantity, JToken? slot, JToken? at,
        string? holder, List<ItemLine> lines, List<OptionProblem> problems)
    {
        foreach ((string name, bool given) in new[]
                 {
                     ("prefab", prefab != null), ("quantity", quantity != null), ("slot", slot != null),
                     ("at", at != null)
                 })
        {
            if (given)
            {
                problems.Add(new OptionProblem(name,
                    name == "at"
                        ? "goes without items: a list goes into a holder (to)"
                        : "goes without items: each items line gives its own prefab, quantity and slot"));
            }
        }

        if (holder == null && at == null)
        {
            return;
        }

        if (items is not JArray entries || entries.Count == 0)
        {
            problems.Add(new OptionProblem("items", "is a list of {prefab, quantity, slot}, at least one"));
            return;
        }

        if (entries.Count > MaximumLines)
        {
            problems.Add(new OptionProblem("items", $"holds at most {MaximumLines} lines; this has {entries.Count}"));
            return;
        }

        for (int index = 0; index < entries.Count; index++)
        {
            string path = $"items[{index}]";
            if (entries[index] is not JObject entry)
            {
                problems.Add(new OptionProblem(path, "is an object {prefab, quantity, slot}"));
                continue;
            }

            int before = problems.Count;
            foreach (JProperty key in entry.Properties())
            {
                if (System.Array.IndexOf(LineOptions, key.Name) < 0)
                {
                    problems.Add(new OptionProblem(path + "." + key.Name,
                        "is no items line option (prefab, quantity, slot)", NearestNames.Of(key.Name, LineOptions)));
                }
            }

            PrefabFilter? entryPrefab = entry["prefab"] is { } token && PrefabPattern.Of(token) is { } pattern
                ? new PrefabFilter(new[] { pattern })
                : null;
            JToken? quantityToken = entry["quantity"];
            double? entryQuantity = OptionReader.NumberOf(quantityToken);
            if (quantityToken != null && entryQuantity == null)
            {
                problems.Add(new OptionProblem(path + ".quantity", "must be more than 0"));
            }

            if (problems.Count == before &&
                Line(entryPrefab, path + ".prefab", entryQuantity, path + ".quantity", entry["slot"], path + ".slot",
                    problems) is { } line)
            {
                lines.Add(line);
            }
        }
    }

    private static ItemLine? Line(PrefabFilter? prefab, string prefabPath, double? quantity, string quantityPath,
        JToken? slot, string slotPath, List<OptionProblem> problems)
    {
        int before = problems.Count;
        PrefabPattern? pattern = null;
        if (prefab == null)
        {
            problems.Add(new OptionProblem(prefabPath, "is needed: the item's prefab name or hash"));
        }
        else if (prefab.Patterns.Count == 1 && prefab.Patterns[0] is PrefabPattern.Whole or PrefabPattern.Hash)
        {
            pattern = prefab.Patterns[0];
        }
        else
        {
            problems.Add(new OptionProblem(prefabPath, "is one item's whole name or its hash, not a * pattern or a list"));
        }

        double amount = quantity ?? 1.0;
        if (!(amount > 0.0) || double.IsInfinity(amount))
        {
            problems.Add(new OptionProblem(quantityPath, "must be more than 0"));
        }

        SlotRequest? request = SlotRequest.Parse(slot, slotPath, problems);
        return problems.Count == before ? new ItemLine(pattern!, amount, request!) : null;
    }
}

/// <summary>
/// What build item may make (a cheat: the cheat switch, CheatSwitch, admits it), a whole count of an item
/// that counts whole (every item but a consumable's fractional amount), and at most MaximumStacks full stacks in one
/// call, each at most the item's full stack.
/// </summary>
internal static class ItemSpawnRule
{
    /// <summary>The most stacks one call makes: enough for any build's kits, small enough to stay one frame's work.</summary>
    internal const int MaximumStacks = 64;

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
