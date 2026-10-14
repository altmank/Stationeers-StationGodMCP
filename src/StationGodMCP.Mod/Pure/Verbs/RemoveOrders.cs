#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// remove structure's call, checked without the game (catalogue remove.json): the structures by id (read against the
/// world before the order is made: a reference id, or what "looking_at" found), the guards allow lifts (contents,
/// breach, broken, burst, unsupported), refund_to, the holders materials names, gas_to, include notes and verbose, and
/// confirm, queue and acknowledge_gas_lost. Replay is the typed removal call the undo makes: the ids of what the job
/// built, the undo's one holder and refund route, and the caller's acknowledge_gas_lost, with no guards lifted. The
/// what's label prefixes every refusal ("remove structure: ...").
/// </summary>
internal sealed class RemoveStructureOrder
{
    private RemoveStructureOrder(WhatSpec spec)
    {
        Label = spec.Label;
        Mark = spec.Write!.UndoMark;
    }

    /// <summary>The call's label, as its refusals prefix it: "remove structure".</summary>
    public string Label { get; }

    /// <summary>The what's undo mark, as its report rows carry it ("[undo]").</summary>
    public string Mark { get; }

    /// <summary>The ids that could be read, in the order given.</summary>
    public List<ThingId> Ids { get; } = new List<ThingId>();

    /// <summary>The ids as the call gave them, null where one could not be read: the report indexes its problems by place.</summary>
    public List<ThingId?> Named { get; } = new List<ThingId?>();

    /// <summary>Each id that could not be read, as a problem at its place among the ids given.</summary>
    public List<ReportProblem> Unread { get; } = new List<ReportProblem>();

    /// <summary>The guards the call lifts.</summary>
    public RemovalAllowance Allow { get; private set; } = new RemovalAllowance(false, false);

    /// <summary>Who pays and where the refunds go; a removal gives materials back.</summary>
    public MaterialsSource Materials { get; private set; } = MaterialsSource.Player;

    /// <summary>include notes: the report carries the tool's fixed explanations.</summary>
    public bool IncludeNotes { get; private set; }

    /// <summary>verbose: every port a split cuts as its own warning; else one per device.</summary>
    public bool SplitDetail { get; private set; }

    /// <summary>gas_to: where a removed device's own gas goes; null to leave it to the game.</summary>
    public GasTarget? GasTo { get; private set; }

    /// <summary>confirm: whether this call acts, and how many rows a report lists.</summary>
    public ReportConfirm Report { get; private set; } = ReportConfirm.Acting;

    /// <summary>queue: start behind a job already running, instead of answering busy while one is.</summary>
    public bool Queue { get; private set; } = true;

    /// <summary>acknowledge_gas_lost, as the call gives it.</summary>
    public string? AcknowledgeGasLost { get; private set; }

    /// <summary>The request the game plans: the ids, the guards, refund_to, who pays and what the call includes.</summary>
    internal RemoveArguments Arguments(ThingId? from, List<ThingId> moreFrom) =>
        new RemoveArguments(Ids, Allow, Materials.RefundTo, from, IncludeNotes, GasTo, SplitDetail);

    /// <summary>
    /// The order remove structure's options give, over the ids already read against the world (named, with unread
    /// holding each id that could not be read). Null with every problem added.
    /// </summary>
    internal static RemoveStructureOrder? Of(RemoveStructureOptions options, List<ThingId?> named,
        List<ReportProblem> unread, List<OptionProblem> problems)
    {
        RemoveStructureOrder order = new RemoveStructureOrder(RemoveStructureOptions.Spec);
        order.GasTo = GasTarget.Of(options.GasTo, problems);
        GasHoldArgs.Check(options.AcknowledgeGasLost, problems);
        if (problems.Count > 0)
        {
            return null;
        }

        order.Named.AddRange(named);
        order.Unread.AddRange(unread);
        foreach (ThingId? id in named)
        {
            if (id is { } read)
            {
                order.Ids.Add(read);
            }
        }

        order.Allow = Allowance(options.Allow);
        order.Materials = options.Materials;
        order.IncludeNotes = options.Fields.Includes("notes");
        order.SplitDetail = options.Fields.Verbose;
        order.Report = options.Report;
        order.Queue = options.Queue ?? true;
        order.AcknowledgeGasLost = options.AcknowledgeGasLost;
        return order;
    }

    /// <summary>
    /// The order the undo's removal call makes: the ids of what the job built, the one holder every call pays from
    /// (null: the player), the refund route (null: each tool's own chain), the caller's acknowledge_gas_lost, and
    /// whether the call acts (a real run queues behind nothing and starts the job) or only reports. Nothing is
    /// lifted: an undo gives no allow.
    /// </summary>
    internal static RemoveStructureOrder Replay(IReadOnlyList<long> ids, long? materials, RefundRoute? refundTo,
        string? acknowledgeGasLost, bool acts, bool queue)
    {
        RemoveStructureOrder order = new RemoveStructureOrder(RemoveStructureOptions.Spec);
        List<Payer> payers = new List<Payer>(1);
        payers.Add(materials.HasValue
            ? new Payer.Holder(UndoReplies.Text(materials.Value))
            : (Payer)new Payer.Player());
        order.Materials = new MaterialsSource(payers, refundTo ?? RefundRoute.Default);
        foreach (long id in ids)
        {
            order.Ids.Add(new ThingId(id));
            order.Named.Add(new ThingId(id));
        }

        order.Report = new ReportConfirm(true, acts, null);
        order.Queue = queue;
        order.AcknowledgeGasLost = acknowledgeGasLost;
        return order;
    }

    // The guards the call names: contents, breach, broken, burst, unsupported.
    private static RemovalAllowance Allowance(IReadOnlyList<string>? allow)
    {
        List<string> words = new List<string>(allow ?? System.Array.Empty<string>());
        return new RemovalAllowance(words.Contains("contents"), words.Contains("breach"), words.Contains("broken"),
            words.Contains("burst"), words.Contains("unsupported"));
    }
}
