#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Api.Shared;

/// <summary>The prefab a silo withdrawal names: exactly one of prefab_name and prefab_hash.</summary>
internal sealed class SiloPrefabChoice
{
    private SiloPrefabChoice(string? name, int? hash)
    {
        Name = name;
        Hash = hash;
    }

    internal string? Name { get; }

    internal int? Hash { get; }

    internal static SiloPrefabChoice Of(Args args)
    {
        string? name = args.OptionalString("prefab_name");
        int? hash = args.OptionalInt("prefab_hash", int.MinValue, int.MaxValue);
        if ((name != null) == hash.HasValue)
        {
            throw ApiErrors.InvalidArgument("Name what to take with exactly one of prefab_name and prefab_hash.");
        }

        if (name != null && name.Trim().Length == 0)
        {
            throw ApiErrors.InvalidArgument("Argument 'prefab_name' must name a prefab, not be empty.");
        }

        return new SiloPrefabChoice(name?.Trim(), hash);
    }

    /// <summary>
    /// Whether a stored entry is this prefab: its name, any case, or its hash as the silo's logic stack computes it
    /// (Animator.StringToHash of the saved PrefabName, Silo.RebuildContentsStack).
    /// </summary>
    internal bool Matches(string? prefabName, int prefabHash) =>
        Hash.HasValue ? Hash.Value == prefabHash : string.Equals(prefabName, Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The prefab as the request named it, for a refusal.</summary>
    internal string Asked => Name ?? Hash!.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// silo_withdraw's arguments: the silo, the prefab, how many items (a stack counts its quantity, anything else 1), the
/// holder (default the player) and its slot (an index, or null for auto), allow_ground, and the write mode.
/// </summary>
internal sealed class SiloWithdrawRequest
{
    private SiloWithdrawRequest(ThingId silo, SiloPrefabChoice prefab, double quantity, ThingId? to, int? toSlot,
        bool allowGround, bool dryRun)
    {
        Silo = silo;
        Prefab = prefab;
        Quantity = quantity;
        To = to;
        ToSlot = toSlot;
        AllowGround = allowGround;
        DryRun = dryRun;
    }

    internal ThingId Silo { get; }

    internal SiloPrefabChoice Prefab { get; }

    internal double Quantity { get; }

    /// <summary>The holder; null for the player.</summary>
    internal ThingId? To { get; }

    /// <summary>A slot index of the holder; null for auto.</summary>
    internal int? ToSlot { get; }

    internal bool AllowGround { get; }

    internal bool DryRun { get; }

    /// <summary>The request; dryRun is WriteMode's answer for the same arguments.</summary>
    internal static SiloWithdrawRequest Of(Args args, bool dryRun)
    {
        ThingId silo = args.ThingId("silo_id");
        SiloPrefabChoice prefab = SiloPrefabChoice.Of(args);
        double quantity = args.OptionalPositiveDouble("quantity") ??
                          throw ApiErrors.InvalidArgument("Argument 'quantity' is required: how many items to take.");
        return new SiloWithdrawRequest(silo, prefab, quantity, args.OptionalThingId("to_id"), SlotOf(args),
            args.OptionalBool("allow_ground") ?? false, dryRun);
    }

    private static int? SlotOf(Args args)
    {
        if (!args.Has("to_slot") || args.IsWord("to_slot", "auto"))
        {
            return null;
        }

        return args.OptionalInt("to_slot", 0, int.MaxValue) ??
               throw ApiErrors.InvalidArgument("Argument 'to_slot' must be a slot index, or \"auto\".");
    }
}

/// <summary>One thing a deposit names by id, whole (quantity null) or part of a stack.</summary>
internal sealed class SiloDepositPick
{
    internal SiloDepositPick(ThingId id, double? quantity)
    {
        Id = id;
        Quantity = quantity;
    }

    internal ThingId Id { get; }

    internal double? Quantity { get; }
}

/// <summary>
/// silo_deposit's forms: items [{reference_id, quantity}], reference_ids [...], or a filter over every item (the filter's
/// own arguments are ItemFilter's; this reads only that one is given, and limit).
/// </summary>
internal abstract class SiloDepositForm
{
    internal const int MaximumItems = 256;
    internal const int MaximumLimit = Pure.SiloRules.Capacity;
    internal const int DefaultLimit = ReplyDefaults.SiloDepositItems;

    private static readonly string[] FilterArguments =
        { "prefab_contains", "name_contains", "location", "within_id", "near_player_m", "limit" };

    private SiloDepositForm()
    {
    }

    internal static SiloDepositForm Of(Args args)
    {
        if (args.Has("items"))
        {
            args.Reject("items", "reference_ids");
            args.Reject("items", FilterArguments);
            return new ById(Items(args));
        }

        if (args.Has("reference_ids"))
        {
            args.Reject("reference_ids", FilterArguments);
            List<SiloDepositPick> picks = new List<SiloDepositPick>();
            foreach (ThingId id in args.ThingIds("reference_ids", MaximumItems))
            {
                picks.Add(new SiloDepositPick(id, null));
            }

            return new ById(picks);
        }

        bool narrowed = false;
        foreach (string name in FilterArguments)
        {
            narrowed |= name != "limit" && args.Has(name);
        }

        if (!narrowed)
        {
            throw ApiErrors.InvalidArgument(
                "Name the things: items, reference_ids, or a filter (prefab_contains, name_contains, location, " +
                "within_id, near_player_m).");
        }

        if (args.IsWord("location", "machine_stock") || args.IsWord("location", "silo"))
        {
            throw ApiErrors.InvalidArgument(
                "Machine stock and silo entries are not items in the world; location must be any, ground, player or stored.");
        }

        return new ByFilter(args.OptionalInt("limit", 1, MaximumLimit) ?? DefaultLimit);
    }

    /// <summary>A filter that matched more than its limit: only the first limit were taken.</summary>
    internal static void NoteCut(int taken, int matched) =>
        Pure.Shaping.Truncations.Capped("items.results", taken, matched, "limit", MaximumLimit);

    private static List<SiloDepositPick> Items(Args args)
    {
        List<Args?> items = args.Objects("items", MaximumItems);
        List<SiloDepositPick> picks = new List<SiloDepositPick>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            Args item = items[index] ?? throw ApiErrors.InvalidArgument($"items[{index}] must be an object.");
            picks.Add(new SiloDepositPick(item.ThingId("reference_id"), item.OptionalPositiveDouble("quantity")));
        }

        return picks;
    }

    /// <summary>Things by id, in the order given.</summary>
    internal sealed class ById : SiloDepositForm
    {
        internal ById(List<SiloDepositPick> picks)
        {
            Picks = picks;
        }

        internal List<SiloDepositPick> Picks { get; }
    }

    /// <summary>Every item the filter keeps that the silo takes, nearest the player first, up to Limit.</summary>
    internal sealed class ByFilter : SiloDepositForm
    {
        internal ByFilter(int limit)
        {
            Limit = limit;
        }

        internal int Limit { get; }
    }
}

/// <summary>container_contents on a silo: which page of its stored entries (entries_offset, entries_limit).</summary>
internal static class SiloEntriesPage
{
    internal const int Maximum = Pure.SiloRules.Capacity;

    internal static PageRequest Of(Args args) =>
        PageRequest.Of(args.OptionalInt("entries_offset", 0, Maximum) ?? 0,
            args.OptionalInt("entries_limit", 1, Maximum) ?? ReplyDefaults.SiloEntries, Maximum);

    /// <summary>The page held back entries: how many of the total, and how to get the rest.</summary>
    internal static void Note(PageRequest page, int returned, int total)
    {
        int next = page.Offset + returned;
        string more = "pass entries_limit (max " + Maximum.ToString(CultureInfo.InvariantCulture) + ")" +
                      (next < total ? " or entries_offset " + next.ToString(CultureInfo.InvariantCulture) : string.Empty);
        Pure.Shaping.Truncations.Note("silo.entries", returned, total, more);
    }
}
