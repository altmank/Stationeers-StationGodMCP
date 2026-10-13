#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>What kind of store an id names, as the game finds it: none, an Ingot (or Remote) Vault, or an SDB Silo.</summary>
internal enum StoreKind
{
    None,
    Vault,
    Silo
}

/// <summary>
/// move item's routes (catalogue move.json, item): with slot or moves, a slot move; otherwise a vault or silo as to or
/// source makes a store move; everything else is a slot move. The reply's first key route names the one that ran.
/// </summary>
internal static class ItemMoveRoutes
{
    internal const string Slot = "slot";
    internal const string IntoVault = "into_vault";
    internal const string OutOfVault = "out_of_vault";
    internal const string VaultToVault = "vault_to_vault";
    internal const string IntoSilo = "into_silo";
    internal const string OutOfSilo = "out_of_silo";

    /// <summary>
    /// The route: slotMove (slot or moves given) makes a slot move; a source store goes out of it (vault to vault when
    /// to is a vault too); a store as to goes into it. A store with another store is refused, but vault to vault.
    /// </summary>
    internal static string? Of(bool slotMove, StoreKind? source, StoreKind to, List<OptionProblem> problems)
    {
        if (slotMove)
        {
            return Slot;
        }

        switch (source)
        {
            case StoreKind.Silo when to != StoreKind.None:
            case StoreKind.Vault when to == StoreKind.Silo:
                problems.Add(new OptionProblem("to", "names a store: a move goes into or out of one store at a " +
                    "time, and vault to vault is the only move between stores"));
                return null;
            case StoreKind.Silo:
                return OutOfSilo;
            case StoreKind.Vault:
                return to == StoreKind.Vault ? VaultToVault : OutOfVault;
        }

        return to switch
        {
            StoreKind.Vault => IntoVault,
            StoreKind.Silo => IntoSilo,
            _ => Slot
        };
    }

    /// <summary>The route in words, for a problem: "a slot move", "a move into a vault" and so on.</summary>
    internal static string Words(string route) => route switch
    {
        IntoVault => "a move into a vault",
        OutOfVault => "a move out of a vault",
        VaultToVault => "a move from vault to vault",
        IntoSilo => "a move into a silo",
        OutOfSilo => "a move out of a silo",
        _ => "a slot move"
    };

    /// <summary>Whether the route takes the option.</summary>
    internal static bool Takes(string route, string option) => route switch
    {
        Slot => option is "id" or "quantity" or "to" or "slot" or "merge" or "force" or "allow" or "moves",
        IntoVault => option is "id" or "items" or "to" or "at_most" || IsFilter(option),
        IntoSilo => option is "id" or "items" or "to" or "at_most" or "allow" || (IsFilter(option) && option != "category"),
        OutOfVault => option is "source" or "prefab" or "reagent" or "quantity" or "to" or "allow",
        VaultToVault => option is "source" or "to" or "items" or "prefab" or "reagent" or "quantity",
        OutOfSilo => option is "source" or "prefab" or "quantity" or "to" or "allow",
        _ => false
    };

    /// <summary>The allow word the route takes: in_use into a slot or a silo, ground out of a store; null for none.</summary>
    internal static string? AllowWord(string route) => route switch
    {
        Slot or IntoSilo => "in_use",
        OutOfVault or OutOfSilo => "ground",
        _ => null
    };

    /// <summary>The filters that pick things for a move into a store.</summary>
    internal static bool IsFilter(string option) =>
        option is "prefab" or "name" or "location" or "within" or "near" or "radius" or "category";
}

/// <summary>
/// The options a move item call gives, by name: the ones the call wrote (merge only when false, force only when true,
/// location and category only when other than "any", as their defaults mean no filter).
/// </summary>
internal static class ItemMoveGiven
{
    private const string AnyWord = "any";

    internal static List<string> Of(MoveItemOptions options)
    {
        Selector selector = options.Selector;
        List<string> given = new List<string>();
        Add(given, "id", selector.Ids != null);
        Add(given, "quantity", options.Quantity != null);
        Add(given, "to", options.To != null);
        Add(given, "slot", options.Slot != null);
        Add(given, "merge", options.Merge == false);
        Add(given, "force", options.Force == true);
        Add(given, "allow", options.Allow != null);
        Add(given, "moves", options.Moves != null);
        Add(given, "source", options.Source != null);
        Add(given, "prefab", selector.Prefab != null);
        Add(given, "reagent", options.Reagent != null);
        Add(given, "items", options.Items != null);
        Add(given, "name", selector.Name != null);
        Add(given, "location", !IsAny(options.Location));
        Add(given, "within", selector.Within != null);
        Add(given, "near", selector.Near != null);
        Add(given, "radius", selector.Radius != null);
        Add(given, "category", !IsAny(options.Category));
        Add(given, "at_most", options.AtMost != null);
        return given;
    }

    /// <summary>
    /// Whether the call acts without confirm (PLAN 2.0; B2-2 c, d): force is a cheat and every filter a spread
    /// (prefab whenever given, D-A1-9), so either reports until confirm; typed ids act at once. A move into a store
    /// that names its things by neither typed ids nor item lines is a spread too, whatever location and category
    /// say: it would take the nearest things of any kind.
    /// </summary>
    internal static bool Acts(string route, MoveItemOptions options, List<string> given)
    {
        if (options.Report.Confirm)
        {
            return true;
        }

        if (route is ItemMoveRoutes.IntoVault or ItemMoveRoutes.IntoSilo && !NamesThings(options))
        {
            return false;
        }

        foreach (string option in given)
        {
            if (option == "force" || ItemMoveRoutes.IsFilter(option))
            {
                return false;
            }
        }

        return true;
    }

    // Typed ids, or item lines (items as a list of lines).
    private static bool NamesThings(MoveItemOptions options) =>
        options.Selector.Ids != null || options.Items is { Type: JTokenType.Array };

    /// <summary>Every option given that the route does not take, and an allow word it does not take, as problems.</summary>
    internal static void RouteProblems(string route, MoveItemOptions options, List<string> given,
        List<OptionProblem> problems)
    {
        foreach (string option in given)
        {
            if (ItemMoveRoutes.Takes(route, option))
            {
                continue;
            }

            problems.Add(new OptionProblem(option, route == ItemMoveRoutes.Slot && ItemMoveRoutes.IsFilter(option)
                ? "picks things for a move into a store; a slot move takes ids"
                : "is not for " + ItemMoveRoutes.Words(route)));
        }

        if (options.Allow is { } allow && ItemMoveRoutes.Takes(route, "allow"))
        {
            string word = ItemMoveRoutes.AllowWord(route)!;
            foreach (string asked in allow)
            {
                if (asked != word)
                {
                    problems.Add(new OptionProblem("allow", $"takes only {word} for {ItemMoveRoutes.Words(route)}"));
                }
            }
        }
    }

    /// <summary>Whether allow holds the word.</summary>
    internal static bool Allows(MoveItemOptions options, string word)
    {
        if (options.Allow is { } allow)
        {
            foreach (string asked in allow)
            {
                if (asked == word)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void Add(List<string> given, string option, bool written)
    {
        if (written)
        {
            given.Add(option);
        }
    }

    private static bool IsAny(string? word) =>
        word == null || string.Equals(word.Trim(), AnyWord, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A call that names nothing to move: missing_target, with the options that would name it.</summary>
internal static class ItemMoveMissing
{
    internal const string MissingTarget = "missing_target";

    internal static VerbError Of(string label, string text, params string[] options) =>
        new VerbError(MissingTarget, $"{label}: {text}.", new JObject { ["options"] = new JArray(options) });
}

/// <summary>One slot move as the options give it: the item, the holder ("player" or an id), the slot (null for auto),
/// how many off the stack (null for the whole item), merge, and whether a part in use may be taken.</summary>
internal sealed class SlotMoveOrder
{
    internal SlotMoveOrder(int index, string item, string to, int? slot, int? quantity, bool merge, bool allowInUse)
    {
        Index = index;
        Item = item;
        To = to;
        Slot = slot;
        Quantity = quantity;
        Merge = merge;
        AllowInUse = allowInUse;
    }

    public int Index { get; }

    public string Item { get; }

    public string To { get; }

    public int? Slot { get; }

    public int? Quantity { get; }

    public bool Merge { get; }

    public bool AllowInUse { get; }
}

/// <summary>
/// The slot moves a call asks for (B2-2 e, 2b-2): id (one, or a list) with to, or moves [{id, to, slot, quantity, merge,
/// allow}] in order. The top-level to, slot, merge and allow fill an entry that leaves them out; quantity does not.
/// One id answers one move; a list or moves answers rows. Every problem with the options is listed at once.
/// </summary>
internal sealed class SlotMoveOrders
{
    private const string AutoWord = "auto";

    private static readonly string[] EntryOptions = { "id", "to", "slot", "quantity", "merge", "allow" };

    private SlotMoveOrders(List<SlotMoveOrder> orders, bool answersRows)
    {
        Orders = orders;
        AnswersRows = answersRows;
    }

    public List<SlotMoveOrder> Orders { get; }

    public bool AnswersRows { get; }

    /// <summary>The moves; null with the problems added, or with nothing named at all (missing is then set).</summary>
    internal static SlotMoveOrders? Of(MoveItemOptions options, List<OptionProblem> problems, out VerbError? missing)
    {
        missing = null;
        string label = MoveItemOptions.Spec.Label;
        int before = problems.Count;
        PerIdList? ids = options.Selector.Ids;
        int? slot = SlotOf(options.Slot, "slot", problems);
        int? quantity = QuantityOf(options.Quantity, "quantity", problems);
        bool merge = options.Merge ?? true;
        bool inUse = ItemMoveGiven.Allows(options, "in_use");
        if (options.Moves != null)
        {
            if (ids != null)
            {
                problems.Add(new OptionProblem("id", "goes without moves: each moves entry names its own item"));
            }

            if (options.Quantity != null)
            {
                problems.Add(new OptionProblem("quantity", "goes with id; each moves entry gives its own"));
            }

            if (options.Force == true)
            {
                problems.Add(new OptionProblem("force", "is for one move by id, with slot as an index"));
            }

            List<SlotMoveOrder> listed = Entries(options, slot, merge, inUse, problems);
            return problems.Count == before ? new SlotMoveOrders(listed, true) : null;
        }

        if (ids == null)
        {
            missing = ItemMoveMissing.Of(label, "give id with to, or moves: [{id, to}]", "id", "moves");
            return null;
        }

        if (options.Force == true)
        {
            if (ids.GivenAsList)
            {
                problems.Add(new OptionProblem("force", "is for one move by id, with slot as an index"));
            }
            else if (slot == null && problems.Count == before)
            {
                problems.Add(new OptionProblem("force", "needs slot as an index, not \"auto\""));
            }
        }

        if (problems.Count > before)
        {
            return null;
        }

        if (options.To is not { } to || to.Trim().Length == 0)
        {
            missing = ItemMoveMissing.Of(label, "give to: the holder that gets the item", "to");
            return null;
        }

        List<SlotMoveOrder> orders = new List<SlotMoveOrder>(ids.Ids.Count);
        foreach (string id in ids.Ids)
        {
            orders.Add(new SlotMoveOrder(orders.Count, id, to.Trim(), slot, quantity, merge, inUse));
        }

        return new SlotMoveOrders(orders, ids.GivenAsList);
    }

    private static List<SlotMoveOrder> Entries(MoveItemOptions options, int? slot, bool merge, bool inUse,
        List<OptionProblem> problems)
    {
        List<SlotMoveOrder> orders = new List<SlotMoveOrder>();
        if (options.Moves is not JArray entries)
        {
            problems.Add(new OptionProblem("moves", "is a list of {id, to, slot, quantity, merge, allow}"));
            return orders;
        }

        string? to = options.To?.Trim();
        for (int index = 0; index < entries.Count; index++)
        {
            string path = $"moves[{index}]";
            if (entries[index] is not JObject entry)
            {
                problems.Add(new OptionProblem(path, "is an object {id, to, slot, quantity, merge, allow}"));
                continue;
            }

            int before = problems.Count;
            foreach (JProperty key in entry.Properties())
            {
                if (Array.IndexOf(EntryOptions, key.Name) < 0)
                {
                    problems.Add(new OptionProblem(path + "." + key.Name,
                        "is no moves entry option (id, to, slot, quantity, merge, allow)",
                        NearestNames.Of(key.Name, EntryOptions)));
                }
            }

            string? id = TextOf(entry["id"], path + ".id", "the item's id", problems);
            string? entryTo = entry["to"] != null || to == null
                ? TextOf(entry["to"], path + ".to", "the holder that gets the item, an id or \"player\"", problems)
                : to;
            int? entrySlot = entry["slot"] != null ? SlotOf(entry["slot"], path + ".slot", problems) : slot;
            int? entryQuantity = QuantityOf(OptionReader.NumberOf(entry["quantity"]), path + ".quantity", problems);
            if (entry["quantity"] != null && entry["quantity"]!.Type is not (JTokenType.Integer or JTokenType.Float))
            {
                problems.Add(new OptionProblem(path + ".quantity", "is a whole number, at least 1"));
            }

            bool entryMerge = merge;
            if (entry["merge"] is { } mergeToken)
            {
                if (mergeToken.Type == JTokenType.Boolean)
                {
                    entryMerge = (bool)mergeToken;
                }
                else
                {
                    problems.Add(new OptionProblem(path + ".merge", "is true or false"));
                }
            }

            bool entryInUse = entry["allow"] != null ? AllowOf(entry["allow"]!, path + ".allow", problems) : inUse;
            if (problems.Count == before)
            {
                orders.Add(new SlotMoveOrder(index, id!, entryTo!, entrySlot, entryQuantity, entryMerge, entryInUse));
            }
        }

        return orders;
    }

    // A slot index, or "auto" (null).
    private static int? SlotOf(JToken? slot, string path, List<OptionProblem> problems)
    {
        switch (slot)
        {
            case null:
                return null;
            case { Type: JTokenType.String } when string.Equals(((string)slot!).Trim(), AutoWord,
                StringComparison.OrdinalIgnoreCase):
                return null;
            case { Type: JTokenType.Integer } when (long)slot >= 0 && (long)slot <= int.MaxValue:
                return (int)(long)slot;
            default:
                problems.Add(new OptionProblem(path, "is a slot index (0 or more) or \"auto\""));
                return null;
        }
    }

    // A slot move counts whole items.
    private static int? QuantityOf(double? quantity, string path, List<OptionProblem> problems)
    {
        if (quantity is not { } given)
        {
            return null;
        }

        if (given < 1 || given > int.MaxValue || Math.Abs(given - Math.Round(given)) > 0)
        {
            problems.Add(new OptionProblem(path, "is a whole number, at least 1: a slot move counts whole items"));
            return null;
        }

        return (int)given;
    }

    private static bool AllowOf(JToken allow, string path, List<OptionProblem> problems)
    {
        bool inUse = false;
        if (allow is JArray words)
        {
            foreach (JToken word in words)
            {
                if (word.Type == JTokenType.String && (string)word! == "in_use")
                {
                    inUse = true;
                }
                else
                {
                    problems.Add(new OptionProblem(path, "takes only in_use for a slot move"));
                }
            }
        }
        else
        {
            problems.Add(new OptionProblem(path, "is a list: [\"in_use\"]"));
        }

        return inUse;
    }

    // A non-empty string: "is needed" when left out, "is" when given as anything else.
    private static string? TextOf(JToken? token, string path, string what, List<OptionProblem> problems)
    {
        string? text = token?.Type == JTokenType.String ? ((string)token!).Trim() : null;
        if (string.IsNullOrEmpty(text))
        {
            problems.Add(new OptionProblem(path, token == null
                ? "is needed: " + what
                : "is " + what + ", a non-empty string"));
            return null;
        }

        return text;
    }
}

/// <summary>
/// The things a deposit names (into a vault or a silo): id (one or a list, each whole), items [{id, quantity}], or the
/// filters (prefab, name, location, within, near "player" with radius, and category into a vault); one form a call.
/// </summary>
internal sealed class StoreDepositOrder
{
    private static readonly string[] EntryOptions = { "id", "quantity" };

    private StoreDepositOrder(List<DepositLine>? lines)
    {
        Lines = lines;
    }

    /// <summary>The things by id; null for the filter form.</summary>
    public List<DepositLine>? Lines { get; }

    internal bool Filtered => Lines == null;

    /// <summary>The order; null with the problems added, or with nothing named (missing is then set).</summary>
    internal static StoreDepositOrder? Of(MoveItemOptions options, List<string> given, List<OptionProblem> problems,
        out VerbError? missing)
    {
        missing = null;
        int before = problems.Count;
        bool filtered = false;
        foreach (string option in given)
        {
            filtered |= ItemMoveRoutes.IsFilter(option);
        }

        PerIdList? ids = options.Selector.Ids;
        if (ids != null && options.Items != null)
        {
            problems.Add(new OptionProblem("items", "goes without id: name the things one way"));
        }

        if (filtered && (ids != null || options.Items != null))
        {
            problems.Add(new OptionProblem(ids != null ? "id" : "items",
                "goes without the filters (prefab, name, location, within, near, category): name the things one way"));
        }

        problems.AddRange(options.Selector.PlayerRadiusProblems());
        List<DepositLine>? lines = null;
        if (options.Items != null)
        {
            lines = LinesOf(options.Items, problems);
        }
        else if (ids != null)
        {
            lines = new List<DepositLine>(ids.Ids.Count);
            foreach (string id in ids.Ids)
            {
                lines.Add(new DepositLine(id, null));
            }
        }
        else if (!filtered)
        {
            missing = ItemMoveMissing.Of(MoveItemOptions.Spec.Label,
                "name the things to store: id, items [{id, quantity}], or the filters", "id", "items", "prefab",
                "name", "location", "within", "near", "category");
            return null;
        }

        return problems.Count == before ? new StoreDepositOrder(lines) : null;
    }

    private static List<DepositLine> LinesOf(JToken items, List<OptionProblem> problems)
    {
        List<DepositLine> lines = new List<DepositLine>();
        if (items is not JArray entries)
        {
            problems.Add(new OptionProblem("items", "is a list of {id, quantity} here; \"all\" is for a move from " +
                "vault to vault"));
            return lines;
        }

        for (int index = 0; index < entries.Count; index++)
        {
            string path = $"items[{index}]";
            if (entries[index] is not JObject entry)
            {
                problems.Add(new OptionProblem(path, "is an object {id, quantity}"));
                continue;
            }

            int before = problems.Count;
            foreach (JProperty key in entry.Properties())
            {
                if (Array.IndexOf(EntryOptions, key.Name) < 0)
                {
                    problems.Add(new OptionProblem(path + "." + key.Name, "is no items entry option here (id, quantity)",
                        NearestNames.Of(key.Name, EntryOptions)));
                }
            }

            string? id = entry["id"]?.Type == JTokenType.String ? ((string)entry["id"]!).Trim() : null;
            if (string.IsNullOrEmpty(id))
            {
                problems.Add(new OptionProblem(path + ".id", "is the thing's id, a non-empty string"));
            }

            double? quantity = OptionReader.NumberOf(entry["quantity"]);
            if (entry["quantity"] != null && (quantity is not { } amount || amount <= 0))
            {
                problems.Add(new OptionProblem(path + ".quantity", "is a number above 0: how much of the stack"));
            }

            if (problems.Count == before)
            {
                lines.Add(new DepositLine(id!, quantity));
            }
        }

        return lines;
    }
}

/// <summary>One thing a deposit names: its id, and how much of it (null for all).</summary>
internal sealed class DepositLine
{
    internal DepositLine(string id, double? quantity)
    {
        Id = id;
        Quantity = quantity;
    }

    public string Id { get; }

    public double? Quantity { get; }
}

/// <summary>
/// The stored thing a move out of a store names: prefab, one whole name or hash (out of a vault, or reagent instead),
/// and the amount, quantity, which both need.
/// </summary>
internal sealed class StoreTakeOrder
{
    private StoreTakeOrder(PrefabPattern? prefab, string? reagent, double quantity)
    {
        Prefab = prefab;
        Reagent = reagent;
        Quantity = quantity;
    }

    /// <summary>A Whole or a Hash pattern; null when reagent names the line.</summary>
    public PrefabPattern? Prefab { get; }

    public string? Reagent { get; }

    public double Quantity { get; }

    /// <summary>The order; null with the problems added, or with nothing named (missing is then set).</summary>
    internal static StoreTakeOrder? Of(MoveItemOptions options, bool vault, List<OptionProblem> problems,
        out VerbError? missing)
    {
        missing = null;
        int before = problems.Count;
        PrefabPattern? prefab = OnePrefab(options.Selector.Prefab, "prefab", problems);
        string? reagent = string.IsNullOrWhiteSpace(options.Reagent) ? null : options.Reagent!.Trim();
        if (prefab != null && reagent != null)
        {
            problems.Add(new OptionProblem("reagent", "goes without prefab: name the stored thing one way"));
        }

        if (options.Quantity is not { } quantity || quantity <= 0)
        {
            problems.Add(new OptionProblem("quantity", vault
                ? "is needed out of a vault, above 0: grams of ingot or a count of ore or ice"
                : "is needed out of a silo, above 0: an item count"));
        }

        if (prefab == null && reagent == null && options.Selector.Prefab == null && problems.Count == before)
        {
            missing = vault
                ? ItemMoveMissing.Of(MoveItemOptions.Spec.Label, "name what to take: prefab or reagent", "prefab",
                    "reagent")
                : ItemMoveMissing.Of(MoveItemOptions.Spec.Label, "name what to take: prefab", "prefab");
            return null;
        }

        if (options.To is not { } to || to.Trim().Length == 0)
        {
            if (problems.Count == before)
            {
                missing = ItemMoveMissing.Of(MoveItemOptions.Spec.Label,
                    "give to: the holder that gets the items (\"player\" for the player)", "to");
            }

            return null;
        }

        return problems.Count == before ? new StoreTakeOrder(prefab, reagent, options.Quantity!.Value) : null;
    }

    /// <summary>One whole name or hash; a list or a pattern with * is a problem.</summary>
    internal static PrefabPattern? OnePrefab(PrefabFilter? filter, string path, List<OptionProblem> problems)
    {
        if (filter == null)
        {
            return null;
        }

        if (filter.Patterns.Count != 1 || filter.Patterns[0] is PrefabPattern.Parts)
        {
            problems.Add(new OptionProblem(path, "names one stored thing here: its whole name or hash"));
            return null;
        }

        return filter.Patterns[0];
    }
}

/// <summary>
/// The lines a move from vault to vault asks for: items [{prefab, reagent, quantity}] (one of prefab and reagent each;
/// quantity left out moves the whole line), items "all" for every line, or a top-level prefab or reagent with quantity
/// as one line.
/// </summary>
internal sealed class VaultTransferOrder
{
    private const string AllWord = "all";

    private static readonly string[] EntryOptions = { "prefab", "reagent", "quantity" };

    private VaultTransferOrder(List<TransferAsk>? asks)
    {
        Asks = asks;
    }

    /// <summary>The lines asked for; null moves every line.</summary>
    public List<TransferAsk>? Asks { get; }

    /// <summary>same_vault when source and to are one vault's id: nothing to move.</summary>
    internal static VerbError? SameVault(MoveItemOptions options) =>
        options.Source is { } source && options.To is { } to
        && ThingId.TryRead(new JValue(source), out ThingId from) && ThingId.TryRead(new JValue(to), out ThingId into)
        && from.Equals(into)
            ? new VerbError("same_vault", "source and to name the same vault; nothing to move.")
            : null;

    /// <summary>The order; null with the problems added, or with nothing named (missing is then set).</summary>
    internal static VaultTransferOrder? Of(MoveItemOptions options, List<OptionProblem> problems,
        out VerbError? missing)
    {
        missing = null;
        int before = problems.Count;
        bool topLevel = options.Selector.Prefab != null || options.Reagent != null;
        if (options.Items != null)
        {
            if (topLevel)
            {
                problems.Add(new OptionProblem(options.Selector.Prefab != null ? "prefab" : "reagent",
                    "goes without items: a top-level prefab or reagent with quantity is one line"));
            }

            if (options.Quantity != null)
            {
                problems.Add(new OptionProblem("quantity", "goes with a top-level prefab or reagent; each items line " +
                    "gives its own"));
            }

            List<TransferAsk>? asks = AsksOf(options.Items, problems);
            return problems.Count == before ? new VaultTransferOrder(asks) : null;
        }

        if (!topLevel)
        {
            missing = ItemMoveMissing.Of(MoveItemOptions.Spec.Label,
                "name the lines to move: items [{prefab, reagent, quantity}] or \"all\"", "items");
            return null;
        }

        PrefabPattern? prefab = StoreTakeOrder.OnePrefab(options.Selector.Prefab, "prefab", problems);
        string? reagent = string.IsNullOrWhiteSpace(options.Reagent) ? null : options.Reagent!.Trim();
        if (options.Selector.Prefab != null && reagent != null)
        {
            problems.Add(new OptionProblem("reagent", "goes without prefab: name the stored thing one way"));
        }

        if (options.Quantity is <= 0)
        {
            problems.Add(new OptionProblem("quantity", "is above 0, or left out for the whole line"));
        }

        return problems.Count == before
            ? new VaultTransferOrder(new List<TransferAsk> { Ask(prefab, reagent, options.Quantity) })
            : null;
    }

    private static List<TransferAsk>? AsksOf(JToken items, List<OptionProblem> problems)
    {
        if (items.Type == JTokenType.String)
        {
            if (!string.Equals(((string)items!).Trim(), AllWord, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new OptionProblem("items", "is a list of lines, or \"all\" for every line"));
            }

            return null;
        }

        List<TransferAsk> asks = new List<TransferAsk>();
        if (items is not JArray entries || entries.Count == 0)
        {
            problems.Add(new OptionProblem("items", "is a list of at least one line, or \"all\" for every line"));
            return asks;
        }

        for (int index = 0; index < entries.Count; index++)
        {
            string path = $"items[{index}]";
            if (entries[index] is not JObject entry)
            {
                problems.Add(new OptionProblem(path, "is an object {prefab, reagent, quantity}"));
                continue;
            }

            int before = problems.Count;
            foreach (JProperty key in entry.Properties())
            {
                if (Array.IndexOf(EntryOptions, key.Name) < 0)
                {
                    problems.Add(new OptionProblem(path + "." + key.Name,
                        "is no items entry option here (prefab, reagent, quantity)", NearestNames.Of(key.Name, EntryOptions)));
                }
            }

            PrefabPattern? prefab = null;
            if (entry["prefab"] is { } named)
            {
                prefab = PrefabPattern.Of(named);
                if (prefab is null or PrefabPattern.Parts)
                {
                    problems.Add(new OptionProblem(path + ".prefab", "is a stored thing's whole name or hash"));
                }
            }

            string? reagent = entry["reagent"]?.Type == JTokenType.String ? ((string)entry["reagent"]!).Trim() : null;
            if (entry["reagent"] != null && string.IsNullOrEmpty(reagent))
            {
                problems.Add(new OptionProblem(path + ".reagent", "is a reagent's name, a non-empty string"));
            }

            if ((entry["prefab"] != null) == (entry["reagent"] != null))
            {
                problems.Add(new OptionProblem(path, "names one stored thing: prefab or reagent"));
            }

            double? quantity = OptionReader.NumberOf(entry["quantity"]);
            if (entry["quantity"] != null && (quantity is not { } amount || amount <= 0))
            {
                problems.Add(new OptionProblem(path + ".quantity", "is above 0, or left out for the whole line"));
            }

            if (problems.Count == before)
            {
                asks.Add(Ask(prefab, reagent, quantity));
            }
        }

        return asks;
    }

    private static TransferAsk Ask(PrefabPattern? prefab, string? reagent, double? quantity) =>
        new TransferAsk((prefab as PrefabPattern.Whole)?.Name, (prefab as PrefabPattern.Hash)?.Value, reagent,
            quantity);
}
