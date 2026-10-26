#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>Where a slot on a player is, for move item to: "player" with slot auto.</summary>
internal enum PlayerSlotPlace
{
    /// <summary>A slot of something the player wears (a belt, backpack, suit) or of what is stored in it.</summary>
    Worn,

    /// <summary>One of the player's hands.</summary>
    Hand,

    /// <summary>A body slot that is not a hand (helmet, suit, back, belt, uniform): wearing, not storing.</summary>
    Body,

    /// <summary>A slot of something held in a hand (a tool's battery slot): not storage either.</summary>
    InHand
}

/// <summary>
/// Which of a player's slots move item's auto search uses, and in what order, as a player puts an item away: first the
/// slots of what the player wears (a mining belt's Tool slots, a backpack's), then a free hand. Body slots (wearing a
/// helmet, a suit) and the slots of a tool in hand are left out: a slot given by index still reaches them. The game's
/// quick moves skip the hands (not swappable), and a hand is where a player puts what it picks up, so the hands are
/// judged by the slot rules alone. When nothing takes the item, the refusal names what each fitting slot said.
/// </summary>
internal static class PlayerSlotRule
{
    internal const int ShownReasons = 6;

    /// <summary>Whether auto may use the slot, and its order (lower first); null when auto leaves it out.</summary>
    internal static int? Order(PlayerSlotPlace place) => place switch
    {
        PlayerSlotPlace.Worn => 0,
        PlayerSlotPlace.Hand => 1,
        _ => null
    };

    /// <summary>Whether the quick-move swappable rule applies there (not to a hand, which a player fills itself).</summary>
    internal static bool NeedsSwappable(PlayerSlotPlace place) => place != PlayerSlotPlace.Hand;

    /// <summary>
    /// The no_free_slot message: what has no slot, then each reason given (the slots whose class fits come first, as
    /// the caller lists them), at most ShownReasons and a count of the rest.
    /// </summary>
    internal static string NoSlot(string target, string item, bool merge, IReadOnlyList<string> reasons)
    {
        string head = $"{target} has no slot that takes {item}" +
                      (merge ? " (its matching stacks have no room for all of it)" : string.Empty);
        if (reasons.Count == 0)
        {
            return head + ".";
        }

        List<string> shown = new List<string>();
        for (int index = 0; index < reasons.Count && index < ShownReasons; index++)
        {
            shown.Add(reasons[index]);
        }

        string more = reasons.Count > ShownReasons ? $"; and {reasons.Count - ShownReasons} more" : string.Empty;
        return head + ": " + string.Join("; ", shown) + more + ".";
    }
}
