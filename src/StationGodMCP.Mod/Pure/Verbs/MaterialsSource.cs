#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// Where a build's materials come from and a removal's refunds go (PLAN 2.0, world rules on writes): materials is the
/// holders that pay, tried in order (ids or "player"; left out, the player), refund_to where refunds go (a word, a
/// container id, or a list tried in turn; left out, inventory, source, storage, ground). A build paying from the player
/// carries the warning materials_from_player.
/// </summary>
internal sealed class MaterialsSource
{
    internal MaterialsSource(IReadOnlyList<Payer> payers, RefundRoute refundTo)
    {
        Payers = payers;
        RefundTo = refundTo;
    }

    public IReadOnlyList<Payer> Payers { get; }

    public RefundRoute RefundTo { get; }

    internal const string FromPlayerWarning = "materials_from_player";

    internal static MaterialsSource Player { get; } =
        new MaterialsSource(new Payer[] { new Payer.Player() }, RefundRoute.Default);

    internal static MaterialsSource Bind(OptionReader options) =>
        new MaterialsSource(PayersOf(options), RefundsOf(options));

    /// <summary>True when the player pays: materials left out, or naming "player".</summary>
    internal bool FromPlayer
    {
        get
        {
            foreach (Payer payer in Payers)
            {
                if (payer is Payer.Player)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The materials_from_player warning, {code, message}, for a build the player pays for.</summary>
    internal static JObject FromPlayerNote() => new JObject
    {
        ["code"] = FromPlayerWarning,
        ["message"] = "This build pays from the player's inventory (materials left out, or \"player\")."
    };

    private static IReadOnlyList<Payer> PayersOf(OptionReader options)
    {
        IReadOnlyList<string>? named = options.Words("materials");
        if (named == null)
        {
            return Player.Payers;
        }

        List<Payer> payers = new List<Payer>(named.Count);
        foreach (string name in named)
        {
            string word = name.Trim();
            if (word.Length == 0)
            {
                options.Problem("materials", "a holder is a thing's id or \"player\"");
            }
            else
            {
                payers.Add(string.Equals(word, "player", StringComparison.OrdinalIgnoreCase)
                    ? new Payer.Player()
                    : new Payer.Holder(word));
            }
        }

        return payers;
    }

    private static RefundRoute RefundsOf(OptionReader options)
    {
        JToken? given = options.Raw("refund_to");
        switch (given)
        {
            case null:
                return RefundRoute.Default;
            case JValue { Type: JTokenType.String or JTokenType.Integer } one:
                return RefundRoute.OfWord(one.ToString()) ?? Refused(options, one.ToString());
            case JArray list:
                List<string> words = new List<string>(list.Count);
                foreach (JToken entry in list)
                {
                    string word = entry.ToString().Trim();
                    if (RefundTarget.Of(word) == null)
                    {
                        return Refused(options, word);
                    }

                    if (words.Exists(known => string.Equals(known, word, StringComparison.OrdinalIgnoreCase)))
                    {
                        options.Problem("refund_to", $"names {word} twice");
                        return RefundRoute.Default;
                    }

                    words.Add(word);
                }

                if (RefundRoute.OfWords(words, out _) is { } chain)
                {
                    return chain;
                }

                options.Problem("refund_to", $"a list takes 1 to {RefundRoute.MaximumTargets} places");
                return RefundRoute.Default;
            default:
                return Refused(options, given.ToString());
        }
    }

    private static RefundRoute Refused(OptionReader options, string word)
    {
        options.Problem("refund_to",
            $"'{word}' is not inventory, source, storage, ground, none or a container's id");
        return RefundRoute.Default;
    }
}

/// <summary>One holder that pays: the player, or a thing by id.</summary>
internal abstract class Payer
{
    private Payer()
    {
    }

    internal sealed class Player : Payer
    {
    }

    internal sealed class Holder : Payer
    {
        internal Holder(string id)
        {
            Id = id;
        }

        public string Id { get; }
    }
}
