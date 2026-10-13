#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;

namespace StationGodMCP.Pure.Verbs;

/// <summary>The trade whats' text options as the values they are.</summary>
internal static class TradeOrders
{
    /// <summary>The card option as the reference id it is: trimmed; blank is absent (the player's own card).</summary>
    internal static string? Card(string? given) => string.IsNullOrWhiteSpace(given) ? null : given!.Trim();
}

/// <summary>One line of a trade call: what the trader's entry is named by, and how many units.</summary>
internal sealed class TradeItemLine
{
    internal TradeItemLine(string? name, string? prefab, int quantity)
    {
        Name = name;
        Prefab = prefab;
        Quantity = quantity;
    }

    /// <summary>The entry's displayed name, as trade inventory gives it; null when the line names the prefab only.</summary>
    public string? Name { get; }

    /// <summary>The entry's prefab name; null when the line names the displayed name only.</summary>
    public string? Prefab { get; }

    /// <summary>How many units, 1 or more.</summary>
    public int Quantity { get; }
}

/// <summary>
/// trade buy and trade sell's call, checked without the game: the trader contacts to trade with (one id or a list:
/// one row per id, in order), the credit card (card; left out, the one the player carries), the item lines to trade
/// (items, 1 to 32), and confirm: until confirm the call reports what it would trade, and confirm acts at once, each
/// id on its own. The what's label prefixes every refusal; a line is read by name, prefab or both, with a line's own
/// problems naming its path (items[2].prefab).
/// </summary>
internal sealed class TradeOrder
{
    /// <summary>The most lines one call trades (the option's maxItems).</summary>
    internal const int MaximumItems = 32;

    /// <summary>The keys a line takes, as an unknown key is answered with the nearest of them.</summary>
    private static readonly string[] LineKeys = { "name", "prefab", "quantity" };

    /// <summary>What a line looks like, as a refusal about items states it.</summary>
    private const string Form = "1 to 32 lines, each {name or prefab, quantity}";

    private TradeOrder(string label, string mark, PerIdList ids, string? card, List<TradeItemLine> items,
        ReportConfirm report)
    {
        Label = label;
        Mark = mark;
        Ids = ids;
        Card = card;
        Items = items;
        Report = report;
    }

    /// <summary>The call's label, as its refusals prefix it: "trade buy".</summary>
    public string Label { get; }

    /// <summary>The what's undo mark, as its report targets carry it ("[cannot undo: trade]").</summary>
    public string Mark { get; }

    /// <summary>The trader contacts to trade with, in order: one id, or a list of them.</summary>
    public PerIdList Ids { get; }

    /// <summary>The credit card the call names; null: the one the player carries.</summary>
    public string? Card { get; }

    /// <summary>The item lines to trade, in order.</summary>
    public IReadOnlyList<TradeItemLine> Items { get; }

    /// <summary>confirm and rows, as the call gives them.</summary>
    public ReportConfirm Report { get; }

    /// <summary>True when the call trades (confirm: true), false when it only reports.</summary>
    internal bool Acts => Report.Acts;

    /// <summary>
    /// The order trade buy's options give; null with every problem added. The what's id is required, so a call that
    /// reaches this names one contact or a list of them.
    /// </summary>
    internal static TradeOrder? Of(TradeBuyOptions options, List<OptionProblem> problems) =>
        Of(TradeBuyOptions.Spec, options.Selector, options.Card, options.Items, options.Report, problems);

    /// <summary>The order trade sell's options give; null with every problem added.</summary>
    internal static TradeOrder? Of(TradeSellOptions options, List<OptionProblem> problems) =>
        Of(TradeSellOptions.Spec, options.Selector, options.Card, options.Items, options.Report, problems);

    private static TradeOrder? Of(WhatSpec what, Selector selector, string? card, JToken? items, ReportConfirm report,
        List<OptionProblem> problems)
    {
        int before = problems.Count;
        if (selector.Ids == null)
        {
            problems.Add(new OptionProblem("id", "is required: the trader contact, or a list of them"));
        }

        List<TradeItemLine> lines = Lines(items, problems);
        return problems.Count > before
            ? null
            : new TradeOrder(what.Label, what.Write!.UndoMark, selector.Ids!, TradeOrders.Card(card), lines, report);
    }

    // items: 1 to 32 lines, each {name or prefab, quantity}. Every problem is collected; nothing is thrown.
    private static List<TradeItemLine> Lines(JToken? given, List<OptionProblem> problems)
    {
        List<TradeItemLine> lines = new List<TradeItemLine>();
        if (given == null)
        {
            problems.Add(new OptionProblem("items", "is required: " + Form));
            return lines;
        }

        if (!(given is JArray list) || list.Count == 0 || list.Count > MaximumItems)
        {
            problems.Add(new OptionProblem("items", "is " + Form));
            return lines;
        }

        for (int index = 0; index < list.Count; index++)
        {
            if (list[index] is JObject line)
            {
                Add(line, $"items[{index}]", lines, problems);
            }
            else
            {
                problems.Add(new OptionProblem($"items[{index}]", "is an object: {name or prefab, quantity}"));
            }
        }

        return lines;
    }

    // One line: name and prefab as text, quantity a whole number of 1 or more, and a key that is no line key a problem
    // naming the nearest that is (the old prefab_name is prefab now).
    private static void Add(JObject line, string path, List<TradeItemLine> lines, List<OptionProblem> problems)
    {
        string? name = null;
        string? prefab = null;
        int? quantity = null;
        bool named = false;
        bool counted = false;
        foreach (JProperty key in line.Properties())
        {
            if (key.Value.Type == JTokenType.Null)
            {
                continue;      // JSON null is absent, as every option reads it
            }

            switch (key.Name)
            {
                case "name":
                    named = true;
                    name = Word(key.Value);
                    if (name == null)
                    {
                        problems.Add(new OptionProblem(path + ".name", "is the entry's displayed name, as text"));
                    }

                    break;
                case "prefab":
                    named = true;
                    prefab = Word(key.Value);
                    if (prefab == null)
                    {
                        problems.Add(new OptionProblem(path + ".prefab", "is the entry's prefab, as text"));
                    }

                    break;
                case "quantity":
                    counted = true;
                    quantity = Count(key.Value);
                    if (quantity == null)
                    {
                        problems.Add(new OptionProblem(path + ".quantity", "is a whole number from 1 to 2147483647"));
                    }

                    break;
                default:
                    problems.Add(new OptionProblem($"{path}.{key.Name}", "is not a line key",
                        NearestNames.Of(key.Name, LineKeys)));
                    break;
            }
        }

        if (!named)
        {
            problems.Add(new OptionProblem(path, "needs name, prefab or both"));
        }

        if (!counted)
        {
            problems.Add(new OptionProblem(path + ".quantity", "is required: how many units"));
        }

        if (name == null && prefab == null || quantity == null)
        {
            return;      // a problem named what is missing: there is no line to trade
        }

        lines.Add(new TradeItemLine(name, prefab, quantity.Value));
    }

    // A line's text: the text given, trimmed; null for a value that is no text and for text that says nothing.
    private static string? Word(JToken given)
    {
        if (given.Type != JTokenType.String)
        {
            return null;
        }

        string text = ((string)given!).Trim();
        return text.Length > 0 ? text : null;
    }

    // A line's quantity: a whole number of 1 or more, as the old reader took it (1 to int.MaxValue).
    private static int? Count(JToken given) =>
        OptionReader.NumberOf(given) is { } number && number >= 1 && number <= int.MaxValue &&
        number == Math.Floor(number)
            ? (int)number
            : null;
}

/// <summary>
/// trade inventory's call, checked without the game: the trader contacts to show (one id, a list, or left out for
/// every contact) and the card whose holder's goods count (card; left out, the player's).
/// </summary>
internal sealed class TradeStockOrder
{
    private TradeStockOrder(PerIdList? ids, string? card)
    {
        Ids = ids;
        Card = card;
    }

    /// <summary>The contacts to show, in order; null: every contact.</summary>
    public PerIdList? Ids { get; }

    /// <summary>The card the call names; null: the player's own.</summary>
    public string? Card { get; }

    /// <summary>The order trade inventory's options give: no option of its own can be wrong.</summary>
    internal static TradeStockOrder Of(TradeInventoryOptions options) =>
        new TradeStockOrder(options.Selector.Ids, TradeOrders.Card(options.Card));
}
