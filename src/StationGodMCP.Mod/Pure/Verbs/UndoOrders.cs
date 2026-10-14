#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// undo's call, checked without the game (catalogue undo.json; T-A5-2): the ids (one or a list, required), the one
/// holder every call pays from and refunds to (materials: left out means the job's own recorded source, else the
/// player; an explicit "player" means the player), the removals' refund_to (left out, each tool's own chain), the raw
/// allow_bridge every piece run is passed, acknowledge_gas_lost (checked: blank is a problem) and confirm. The
/// bulk-write words allow and from_step are read so the handler refuses them naming the hub's bulk undo (T-A5-2(b)):
/// the mod's own ids outlive no bulk write.
/// </summary>
internal sealed class UndoOrders
{
    /// <summary>The call names no id at all (the RemoveRunOrders pattern).</summary>
    internal const string MissingTarget = "missing_target";

    private UndoOrders(UndoOptions options, OptionReader given)
    {
        Options = options;
        Given = given;
        Label = UndoOptions.Spec.Label;
        Holder = HolderOf(given, out bool givenHolder);
        HolderGiven = givenHolder;
        RefundTo = RefundOf(options);
    }

    /// <summary>The call's label, as its refusals prefix it: "undo".</summary>
    public string Label { get; }

    /// <summary>The what's options as they were bound.</summary>
    internal UndoOptions Options { get; }

    /// <summary>
    /// The reader the options were bound from: materials presence is not in the bound options (a defaulted
    /// MaterialsSource reads the same whether the call named the player or left materials out), so only the reader
    /// says what the call gave.
    /// </summary>
    internal OptionReader Given { get; }

    /// <summary>The ids the call names, in the order given; null when it names none (Missing).</summary>
    internal PerIdList? Ids => Options.Selector.Ids;

    /// <summary>The call's problems, as its options were read; refused with every one at once.</summary>
    internal List<OptionProblem> Problems => Given.Problems;

    /// <summary>The one holder the call named (materials): a thing by id, or null for the player; HolderGiven says it named one.</summary>
    internal Payer? Holder { get; }

    /// <summary>Whether the call named materials at all: left out, the job's own recorded source decides.</summary>
    internal bool HolderGiven { get; }

    /// <summary>refund_to as the call gives it; null when left out (each tool's own chain).</summary>
    internal RefundRoute? RefundTo { get; }

    /// <summary>allow_bridge, as given: passed to every piece run.</summary>
    internal JToken? AllowBridge => Options.AllowBridge;

    internal string? AcknowledgeGasLost => Options.AcknowledgeGasLost;

    /// <summary>confirm: whether the call acts, and how many rows a report shows.</summary>
    internal ReportConfirm Report => Options.Report;

    /// <summary>Whether the call acts (confirm: true).</summary>
    internal bool Acts => Options.Report.Acts;

    /// <summary>allow: a bulk write's word, whose undo is the hub's (T-A5-2(b)); null when the call gives none.</summary>
    internal IReadOnlyList<string>? Allow => Options.Allow;

    /// <summary>from_step: a bulk write's word, whose undo is the hub's (T-A5-2(b)); null when the call gives none.</summary>
    internal long? FromStep => Options.FromStep;

    /// <summary>The what's undo mark, as the report's targets carry it ("[cannot undo]").</summary>
    internal string Mark => UndoOptions.Spec.Write!.UndoMark;

    internal static UndoOrders Bind(OptionReader options)
    {
        // The acknowledge is checked where the other verbs check it (blank is a problem, not left out).
        GasHoldArgs.Check(options.Text("acknowledge_gas_lost"), options.Problems);
        return new UndoOrders(UndoOptions.Bind(options), options);
    }

    /// <summary>The call names no job: missing_target, naming what the id may be.</summary>
    internal static VerbError Missing() => new VerbError(MissingTarget,
        $"{UndoOptions.Spec.Label}: name the job by id (place-N, run-N, remove-N), or \"paste\".",
        new JObject { ["options"] = new JArray("id") });

    /// <summary>
    /// The ids in the order they are undone: a job id's numeric suffix (HeldTickJobs numbers every job from one global
    /// counter, so the suffix orders by creation) newest first; an id with no number keeps its place last. Each comes
    /// with its place in the ids given, which the report indexes its problems by.
    /// </summary>
    internal static List<(string Id, int Given)> NewestFirst(IReadOnlyList<string> ids)
    {
        List<(string Id, long Number, int Given)> numbered = new List<(string, long, int)>();
        List<(string Id, int Given)> rest = new List<(string, int)>();
        for (int at = 0; at < ids.Count; at++)
        {
            if (Suffix(ids[at]) is { } number)
            {
                numbered.Add((ids[at], number, at));
            }
            else
            {
                rest.Add((ids[at], at));
            }
        }

        for (int at = 1; at < numbered.Count; at++)
        {
            (string Id, long Number, int Given) id = numbered[at];
            int place = at;
            while (place > 0 && numbered[place - 1].Number < id.Number)
            {
                numbered[place] = numbered[place - 1];
                place--;
            }

            numbered[place] = id;
        }

        List<(string Id, int Given)> ordered = new List<(string, int)>(ids.Count);
        foreach ((string id, long number, int given) in numbered)
        {
            ordered.Add((id, given));
        }

        ordered.AddRange(rest);
        return ordered;
    }

    // The numeric suffix of a mod job id ("run-4": the place of the job in HeldTickJobs' one counter); null for an id
    // with none, which is no job the mod started.
    private static long? Suffix(string id)
    {
        int at = id.LastIndexOf('-');
        return at >= 0 && at + 1 < id.Length &&
               long.TryParse(id.Substring(at + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)
            ? number
            : null;
    }

    // materials: the one holder the call names. The catalogue declares one string, so a list is a problem; left out
    // reads as absent (the job's own source decides), and "player" reads as the player.
    private static Payer? HolderOf(OptionReader options, out bool given)
    {
        given = false;
        JToken? raw = options.Raw("materials");
        if (raw == null)
        {
            return null;
        }

        if (raw.Type is not (JTokenType.String or JTokenType.Integer))
        {
            options.Problem("materials", "is one holder: a thing's id, or \"player\"");
            return null;
        }

        string word = raw.ToString().Trim();
        if (word.Length == 0)
        {
            options.Problem("materials", "is one holder: a thing's id, or \"player\"");
            return null;
        }

        given = true;
        return string.Equals(word, "player", StringComparison.OrdinalIgnoreCase) ? null : new Payer.Holder(word);
    }

    // refund_to: "default" is no word of the route, so the default route means the call left the option out.
    private static RefundRoute? RefundOf(UndoOptions options) =>
        options.Materials.RefundTo == RefundRoute.Default ? null : options.Materials.RefundTo;
}
