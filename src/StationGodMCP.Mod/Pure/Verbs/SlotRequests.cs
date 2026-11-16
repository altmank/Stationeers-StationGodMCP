#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// The slot a move or a made item goes into, as the call names it: the first that takes it (Auto), a slot index
/// (Index) or a slot's own key (Key, case-sensitive, never empty). One closed choice, never an index beside a key.
/// </summary>
internal abstract record SlotRequest
{
    private SlotRequest()
    {
    }

    internal sealed record Auto : SlotRequest;

    internal sealed record Index(int Value) : SlotRequest;

    internal sealed record Key(string Value) : SlotRequest;

    private const string AutoWord = "auto";

    /// <summary>
    /// The request a slot option gives: Auto when left out or "auto" (any case), an index for a whole number of 0 or
    /// more, a key for any other non-empty string. Null with the problem added for anything else.
    /// </summary>
    internal static SlotRequest? Parse(JToken? slot, string path, List<OptionProblem> problems)
    {
        switch (slot)
        {
            case null:
                return new Auto();
            case { Type: JTokenType.String }:
                string text = (string)slot!;
                if (string.Equals(text.Trim(), AutoWord, StringComparison.OrdinalIgnoreCase))
                {
                    return new Auto();
                }

                if (text.Length > 0)
                {
                    return new Key(text);
                }

                break;
            case { Type: JTokenType.Integer } when (long)slot >= 0 && (long)slot <= int.MaxValue:
                return new Index((int)(long)slot);
        }

        problems.Add(new OptionProblem(path, "is a slot index (0 or more), \"auto\", or a slot key (not empty)"));
        return null;
    }
}

/// <summary>Which of a holder's slots a key names: one, none (with the keys there are), or several (with their indexes).</summary>
internal abstract record KeyLookup
{
    private KeyLookup()
    {
    }

    internal sealed record Found(int Index) : KeyLookup;

    internal sealed record Missing(IReadOnlyList<string> Keys) : KeyLookup;

    internal sealed record Ambiguous(IReadOnlyList<int> Indexes) : KeyLookup;

    internal const string LeftHand = "left_hand";
    internal const string RightHand = "right_hand";

    /// <summary>
    /// A player's slot keys with its hand slots (by index; -1 for none) named left_hand and right_hand, whatever key
    /// the game gave them.
    /// </summary>
    internal static IReadOnlyList<string?> WithHands(IReadOnlyList<string?> slotKeys, int leftHand, int rightHand)
    {
        string?[] named = new string?[slotKeys.Count];
        for (int index = 0; index < named.Length; index++)
        {
            named[index] = index == leftHand ? LeftHand : index == rightHand ? RightHand : slotKeys[index];
        }

        return named;
    }

    /// <summary>
    /// The slot whose key equals the given one exactly (ordinal, case-sensitive). A slot with no key (null or empty) is
    /// never matched, and an empty key matches nothing. Missing lists the distinct non-empty keys the holder has, in
    /// slot order.
    /// </summary>
    internal static KeyLookup Of(IReadOnlyList<string?> slotKeys, string key)
    {
        List<int> matches = new List<int>();
        List<string> present = new List<string>();
        for (int index = 0; index < slotKeys.Count; index++)
        {
            string? own = slotKeys[index];
            if (string.IsNullOrEmpty(own))
            {
                continue;
            }

            if (!present.Contains(own!))
            {
                present.Add(own!);
            }

            if (key.Length > 0 && string.Equals(own, key, StringComparison.Ordinal))
            {
                matches.Add(index);
            }
        }

        return matches.Count switch
        {
            0 => new Missing(present),
            1 => new Found(matches[0]),
            _ => new Ambiguous(matches)
        };
    }
}
