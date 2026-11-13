#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure.Verbs;

/// <summary>What a thing named to remove item is, by the game's own class tree.</summary>
internal enum RemovableKind
{
    Item,
    Structure,
    Player,
    Entity,
    Other
}

/// <summary>
/// The facts one id gives remove item, read from the world and held as plain data: what the thing is, its name, whether
/// a player carries it (in hands, inventory or worn, at any depth of slots), and how many items sit in its slots.
/// </summary>
internal sealed class ItemFacts
{
    internal ItemFacts(RemovableKind kind, string name, bool carriedByPlayer, int contents)
    {
        Kind = kind;
        Name = name;
        CarriedByPlayer = carriedByPlayer;
        Contents = contents;
    }

    public RemovableKind Kind { get; }

    public string Name { get; }

    public bool CarriedByPlayer { get; }

    public int Contents { get; }
}

/// <summary>The guards a call lifts with allow: carried (a player's items) and contents (a holder's items go too).</summary>
internal sealed class ItemAllowance
{
    internal ItemAllowance(bool carried, bool contents)
    {
        Carried = carried;
        Contents = contents;
    }

    public bool Carried { get; }

    public bool Contents { get; }

    internal static ItemAllowance None { get; } = new ItemAllowance(false, false);

    internal static ItemAllowance Of(IReadOnlyList<string>? allow)
    {
        bool carried = false;
        bool contents = false;
        foreach (string word in allow ?? System.Array.Empty<string>())
        {
            carried |= word == "carried";
            contents |= word == "contents";
        }

        return new ItemAllowance(carried, contents);
    }
}

/// <summary>Why remove item refuses one id.</summary>
internal enum ItemRefusal
{
    NotAnItem,
    Carried,
    HasContents
}

/// <summary>
/// The decision for one id: removable (with whether its contents go too) or refused with the reason and the words that
/// say what to do. Nothing else is possible.
/// </summary>
internal abstract class ItemVerdict
{
    private ItemVerdict()
    {
    }

    internal sealed class Removable : ItemVerdict
    {
        internal Removable(bool takesContents)
        {
            TakesContents = takesContents;
        }

        /// <summary>The item holds items and the call allowed them to go too.</summary>
        public bool TakesContents { get; }
    }

    internal sealed class Refused : ItemVerdict
    {
        internal Refused(ItemRefusal reason, string message)
        {
            Reason = reason;
            Message = message;
        }

        public ItemRefusal Reason { get; }

        public string Message { get; }
    }
}

/// <summary>remove item's per-id rules and wording, without the game.</summary>
internal static class RemoveItemRule
{
    /// <summary>The most ids one call takes.</summary>
    internal const int MostItems = 256;

    /// <summary>The error code a refused id carries.</summary>
    internal const string RefusedCode = "item_not_removable";

    /// <summary>The refusal for a call naming more ids than one call takes; null when the count is fine.</summary>
    internal static string? TooMany(int count) =>
        count > MostItems
            ? $"names {count.ToString(CultureInfo.InvariantCulture)} items; one call takes at most " +
              $"{MostItems.ToString(CultureInfo.InvariantCulture)}: send them in batches"
            : null;

    /// <summary>
    /// One id's verdict: only an item is removable; a carried one needs allow carried, one holding items needs allow
    /// contents. A thing that is not an item names the verb that takes it.
    /// </summary>
    internal static ItemVerdict Decide(ItemFacts facts, ItemAllowance allow)
    {
        if (facts.Kind != RemovableKind.Item)
        {
            return new ItemVerdict.Refused(ItemRefusal.NotAnItem, $"{facts.Name} is {NotAnItem(facts.Kind)}");
        }

        if (facts.CarriedByPlayer && !allow.Carried)
        {
            return new ItemVerdict.Refused(ItemRefusal.Carried,
                $"{facts.Name} is carried by a player: allow [\"carried\"] deletes it from their hands, inventory or suit");
        }

        if (facts.Contents > 0 && !allow.Contents)
        {
            return new ItemVerdict.Refused(ItemRefusal.HasContents,
                $"{facts.Name} holds {facts.Contents.ToString(CultureInfo.InvariantCulture)} " +
                $"item{(facts.Contents == 1 ? string.Empty : "s")}: allow [\"contents\"] deletes them with it");
        }

        return new ItemVerdict.Removable(facts.Contents > 0);
    }

    /// <summary>Where an item is: "ground", "in {holder} slot {n}", with "carried by {player}" when one carries it.</summary>
    internal static string Where(string? holder, int? slot, string? carrier)
    {
        if (holder == null)
        {
            return "ground";
        }

        if (carrier != null && carrier == holder)
        {
            return $"carried by {carrier}";
        }

        string place = slot is { } index
            ? $"in {holder} slot {index.ToString(CultureInfo.InvariantCulture)}"
            : $"in {holder}";
        return carrier != null ? $"{place}, carried by {carrier}" : place;
    }

    private static string NotAnItem(RemovableKind kind) => kind switch
    {
        RemovableKind.Structure => "a structure, not an item: remove structure takes it down",
        RemovableKind.Player => "a player, not an item: remove item does not delete players",
        RemovableKind.Entity => "an entity, not an item: remove item does not delete players or creatures",
        _ => "not an item: remove item deletes items only"
    };
}
