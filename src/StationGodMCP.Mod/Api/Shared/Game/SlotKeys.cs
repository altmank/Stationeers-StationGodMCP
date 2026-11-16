#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A slot key (Slot.StringKey) turned into the index of one of a holder's own slots (not nested). A player's hand
/// slots are named left_hand and right_hand (KeyLookup.WithHands), as ScopedTarget.Worn names them. The key is matched exactly
/// (KeyLookup); the game keeps keys neither unique nor non-empty, so none, or several, is a refusal.
/// </summary>
internal static class SlotKeys
{
    /// <summary>The refusal for a key that names no slot or several; null with the slot's index when it names one.</summary>
    internal static ApiException? Resolve(Thing holder, string key, out int index)
    {
        index = -1;
        switch (KeyLookup.Of(KeysOf(holder), key))
        {
            case KeyLookup.Found found:
                index = found.Index;
                return null;
            case KeyLookup.Ambiguous ambiguous:
                return ApiErrors.Refused("ambiguous_slot",
                    $"{ambiguous.Indexes.Count} slots of {Names.Of(holder)} are keyed \"{key}\" (slot indexes " +
                    $"{string.Join(", ", ambiguous.Indexes)}); name one by its index.");
            case KeyLookup.Missing missing:
                return ApiErrors.Refused("slot_not_found",
                    missing.Keys.Count == 0
                        ? $"{Names.Of(holder)} has no slot keyed \"{key}\": none of its slots carries a key; name a " +
                          "slot by its index (inspect contents lists them)."
                        : $"{Names.Of(holder)} has no slot keyed \"{key}\"; its slot keys are " +
                          $"{string.Join(", ", missing.Keys)}.");
            default:
                return ApiErrors.InvalidArgument("Unknown slot key lookup.");
        }
    }

    private static IReadOnlyList<string?> KeysOf(Thing holder)
    {
        List<Slot>? slots = holder.Slots;
        List<string?> keys = new List<string?>(slots?.Count ?? 0);
        for (int index = 0; slots != null && index < slots.Count; index++)
        {
            keys.Add(slots[index]?.StringKey);
        }

        return holder is Human body && slots != null
            ? KeyLookup.WithHands(keys, slots.IndexOf(body.LeftHandSlot), slots.IndexOf(body.RightHandSlot))
            : keys;
    }
}
