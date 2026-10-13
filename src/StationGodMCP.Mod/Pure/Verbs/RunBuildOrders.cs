#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// build cable, pipe and chute's call, checked without the game: the kind and grade; the run (exactly one of
/// waypoints, cells, piece or pieces, with extra_ends, branches and join); what it lifts (allow: door_keepout, split
/// and lint) and split_long; the pieces it takes down itself (remove_ids) and the things it checks as gone
/// (assume_removed); what it may bridge and must end up on (allow_bridge, join_to), the device a split is measured
/// against (root) and whether it adds the tap (join_trunk); who pays and where the refunds go (materials, refund_to);
/// and how its report is answered (include, rows, verbose, queue, acknowledge_gas_lost). Positions, ends and branch
/// cells stay as given: only the game resolves them. The what's label prefixes every refusal ("build cable: ...").
/// </summary>
internal sealed class RunBuildOrder
{
    /// <summary>The four ways a run is named; exactly one of them is the call's form.</summary>
    internal const string ByWaypoints = "waypoints";

    internal const string ByCells = "cells";
    internal const string ByPiece = "piece";
    internal const string ByPieces = "pieces";

    private readonly bool _gradeRequired;
    private readonly IReadOnlyList<string> _grades;

    private RunBuildOrder(string kind, WhatSpec spec, bool gradeRequired)
    {
        Kind = kind;
        Label = spec.Label;
        Mark = spec.Write!.UndoMark;
        _gradeRequired = gradeRequired;
        _grades = Words(spec.Option("grade"));
    }

    /// <summary>cable, pipe or chute.</summary>
    public string Kind { get; }

    /// <summary>The call's label, as its refusals prefix it: "build cable".</summary>
    public string Label { get; }

    /// <summary>The what's undo mark, as its report rows carry it ("[undo]").</summary>
    public string Mark { get; }

    /// <summary>The grade new pieces are built of.</summary>
    public string? Grade { get; private set; }

    /// <summary>The one way the run is given: waypoints, cells, piece or pieces.</summary>
    public string Form { get; private set; } = ByWaypoints;

    public JToken? Waypoints { get; private set; }

    public JToken? Cells { get; private set; }

    public JToken? Piece { get; private set; }

    public JToken? Pieces { get; private set; }

    /// <summary>branches as given: {waypoints} or {cells} each, with attach.</summary>
    public JToken? Branches { get; private set; }

    /// <summary>extra_ends as given: {at, toward} each.</summary>
    public JToken? ExtraEnds { get; private set; }

    /// <summary>How the run joins what is there: ends, none or all (a piece or pieces run never joins).</summary>
    public string Join { get; private set; } = "ends";

    /// <summary>allow door_keepout: new pieces in a door's keep-out are a warning.</summary>
    public bool AllowDoorKeepOut { get; private set; }

    /// <summary>allow split: a split is a warning.</summary>
    public bool AllowSplit { get; private set; }

    /// <summary>allow lint: a lint problem is a warning and the rows about it say PASSED.</summary>
    public bool AllowLint { get; private set; }

    /// <summary>split_long: a long straight is split into singles in the same job instead of refused.</summary>
    public bool SplitLong { get; private set; } = true;

    /// <summary>remove_ids as given: the kind's pieces the same job takes down.</summary>
    public IReadOnlyList<string> RemoveIds { get; private set; } = new string[0];

    /// <summary>assume_removed as given: things checked as gone, never removed by this run.</summary>
    public IReadOnlyList<string> AssumeRemoved { get; private set; } = new string[0];

    /// <summary>allow_bridge: each the network handle given (a network's id, a piece or device, or {id, port}).</summary>
    public IReadOnlyList<NetworkTarget> AllowBridge { get; private set; } = new NetworkTarget[0];

    /// <summary>root: the device a would_split measures cut-off devices against.</summary>
    public ThingId? Root { get; private set; }

    /// <summary>join_to: the network the run must end up on.</summary>
    public NetworkTarget? JoinTo { get; private set; }

    /// <summary>join_trunk: add the tap that joins join_to instead of only warning.</summary>
    public bool JoinTrunk { get; private set; }

    /// <summary>Who pays and where the refunds go.</summary>
    public MaterialsSource Materials { get; private set; } = MaterialsSource.Player;

    /// <summary>include and verbose, as the call gives them.</summary>
    public Fields Fields { get; private set; } = Fields.None;

    /// <summary>confirm and rows: whether this call acts, and how many rows its report lists.</summary>
    public ReportConfirm Report { get; private set; } = ReportConfirm.Acting;

    /// <summary>queue: start behind a job already running, instead of waiting refused while one is.</summary>
    public bool Queue { get; private set; } = true;

    /// <summary>acknowledge_gas_lost, for a what that takes it (a pipe build); null otherwise.</summary>
    public string? AcknowledgeGasLost { get; private set; }

    /// <summary>
    /// Where the job took its materials and put its refund, for its undo record (JobSnapshots). The thing that pays
    /// is the world's to resolve: from_id is filled in when the request runs.
    /// </summary>
    public JobSource Source { get; private set; } = JobSource.Unknown;

    /// <summary>The order build cable's options give; null with every problem added, or with missing set when the
    /// call names no run at all.</summary>
    internal static RunBuildOrder? Of(BuildCableOptions options, List<OptionProblem> problems,
        out VerbError? missing)
    {
        RunBuildOrder order = new RunBuildOrder("cable", BuildCableOptions.Spec, gradeRequired: false);
        order.Grade = options.Grade;
        order.Waypoints = options.Waypoints;
        order.Cells = options.Cells;
        order.Piece = options.Piece;
        order.Pieces = options.Pieces;
        order.Branches = options.Branches;
        order.ExtraEnds = options.ExtraEnds;
        order.Join = options.Join ?? "ends";
        order.SplitLong = options.SplitLong ?? true;
        order.RemoveIds = options.RemoveIds ?? new string[0];
        order.AssumeRemoved = options.AssumeRemoved ?? new string[0];
        order.JoinTrunk = options.JoinTrunk ?? false;
        order.Materials = options.Materials;
        order.Fields = options.Fields;
        order.Report = options.Report;
        order.Queue = options.Queue ?? true;
        return order.Read(options.Root, options.JoinTo, options.AllowBridge, options.Allow, problems, out missing);
    }

    /// <summary>The order build pipe's options give (a pipe must name its grade).</summary>
    internal static RunBuildOrder? Of(BuildPipeOptions options, List<OptionProblem> problems, out VerbError? missing)
    {
        RunBuildOrder order = new RunBuildOrder("pipe", BuildPipeOptions.Spec, gradeRequired: true);
        order.Grade = options.Grade;
        order.Waypoints = options.Waypoints;
        order.Cells = options.Cells;
        order.Piece = options.Piece;
        order.Pieces = options.Pieces;
        order.Branches = options.Branches;
        order.ExtraEnds = options.ExtraEnds;
        order.Join = options.Join ?? "ends";
        order.SplitLong = options.SplitLong ?? true;
        order.RemoveIds = options.RemoveIds ?? new string[0];
        order.AssumeRemoved = options.AssumeRemoved ?? new string[0];
        order.JoinTrunk = options.JoinTrunk ?? false;
        order.Materials = options.Materials;
        order.Fields = options.Fields;
        order.Report = options.Report;
        order.Queue = options.Queue ?? true;
        order.AcknowledgeGasLost = options.AcknowledgeGasLost;
        return order.Read(options.Root, options.JoinTo, options.AllowBridge, options.Allow, problems, out missing);
    }

    /// <summary>The order build chute's options give.</summary>
    internal static RunBuildOrder? Of(BuildChuteOptions options, List<OptionProblem> problems,
        out VerbError? missing)
    {
        RunBuildOrder order = new RunBuildOrder("chute", BuildChuteOptions.Spec, gradeRequired: false);
        order.Grade = options.Grade;
        order.Waypoints = options.Waypoints;
        order.Cells = options.Cells;
        order.Piece = options.Piece;
        order.Pieces = options.Pieces;
        order.Branches = options.Branches;
        order.ExtraEnds = options.ExtraEnds;
        order.Join = options.Join ?? "ends";
        order.SplitLong = options.SplitLong ?? true;
        order.RemoveIds = options.RemoveIds ?? new string[0];
        order.AssumeRemoved = options.AssumeRemoved ?? new string[0];
        order.JoinTrunk = options.JoinTrunk ?? false;
        order.Materials = options.Materials;
        order.Fields = options.Fields;
        order.Report = options.Report;
        order.Queue = options.Queue ?? true;
        return order.Read(options.Root, options.JoinTo, options.AllowBridge, options.Allow, problems, out missing);
    }

    /// <summary>
    /// The order a piece run of undo_job starts: the pieces the job takes down as it builds them again (the pieces
    /// form), the grade they take, the pieces the same run takes down itself (remove_ids), the small-grid things of
    /// the rest of the removal it checks as gone (assume_removed), the caller's allow_bridge, who pays and where the
    /// refunds go, and whether the call acts (confirm) or only reports. Null with every problem added.
    /// </summary>
    internal static RunBuildOrder? Replay(string kind, string grade, JToken pieces, IReadOnlyList<string> removeIds,
        IReadOnlyList<string> assumeRemoved, JToken? allowBridge, MaterialsSource materials, JobSource source,
        bool acts, bool queue, string? acknowledgeGasLost, List<OptionProblem> problems)
    {
        RunBuildOrder order = new RunBuildOrder(kind, SpecOf(kind), gradeRequired: false);
        order.Form = ByPieces;
        order.Pieces = pieces;
        order.Grade = grade;
        order.RemoveIds = removeIds;
        order.AssumeRemoved = assumeRemoved;
        order.Materials = materials;
        order.Report = new ReportConfirm(true, acts, null);
        order.Queue = queue;
        order.AcknowledgeGasLost = acknowledgeGasLost;
        RunBuildOrder? read = order.Read(null, null, allowBridge, null, problems, out VerbError? _);
        if (read != null)
        {
            read.Source = source;
        }

        return read;
    }

    /// <summary>The build what a kind word names: cable, pipe or chute.</summary>
    internal static WhatSpec SpecOf(string kind) => kind switch
    {
        "pipe" => BuildPipeOptions.Spec,
        "chute" => BuildChuteOptions.Spec,
        _ => BuildCableOptions.Spec
    };

    // The whole call read at once: its one form, its words, its handles and its acknowledge. Null with a problem (a
    // second form, a word that reads as none, an unreadable handle, a blank acknowledge), or with missing set when
    // the call names no run at all.
    private RunBuildOrder? Read(string? root, JToken? joinTo, JToken? allowBridge, IReadOnlyList<string>? allow,
        List<OptionProblem> problems, out VerbError? missing)
    {
        int before = problems.Count;
        missing = null;
        if (RunForm(problems, out missing) is { } form)
        {
            Form = form;
        }

        if (_gradeRequired && Grade == null)
        {
            problems.Add(new OptionProblem("grade", $"is required: {string.Join(", ", _grades)}"));
        }

        ReadJoin(problems);
        AllowDoorKeepOut = Allow.Lifts(allow, "door_keepout");
        AllowSplit = Allow.Lifts(allow, "split");
        AllowLint = Allow.Lifts(allow, WriteLint.Guard);
        ReadRoot(root, problems);
        ReadJoinTo(joinTo, problems);
        ReadAllowBridge(allowBridge, problems);
        GasHoldArgs.Check(AcknowledgeGasLost, problems);
        Source = new JobSource(null, false, Materials.RefundTo, null);
        return problems.Count == before && missing == null ? this : null;
    }

    // The one way the call names its run: waypoints, cells, piece or pieces. A second is a problem; none is a
    // missing_target naming all four.
    private string? RunForm(List<OptionProblem> problems, out VerbError? missing)
    {
        missing = null;
        List<string> given = new List<string>(4);
        if (Waypoints != null)
        {
            given.Add(ByWaypoints);
        }

        if (Cells != null)
        {
            given.Add(ByCells);
        }

        if (Piece != null)
        {
            given.Add(ByPiece);
        }

        if (Pieces != null)
        {
            given.Add(ByPieces);
        }

        if (given.Count > 1)
        {
            problems.Add(new OptionProblem(given[1],
                $"goes without {given[0]}: name the run by waypoints, cells, piece or pieces"));
            return null;
        }

        if (given.Count == 0)
        {
            missing = new VerbError(RemoveRunOrders.MissingTarget,
                $"{Label}: name the run by waypoints, cells, piece or pieces.",
                new JObject { ["options"] = new JArray(ByWaypoints, ByCells, ByPiece, ByPieces) });
            return null;
        }

        return given[0];
    }

    private void ReadJoin(List<OptionProblem> problems)
    {
        string join = Join.Trim().ToLowerInvariant();
        if (join != "ends" && join != "none" && join != "all")
        {
            problems.Add(new OptionProblem("join", "is ends, none or all"));
            return;
        }

        Join = join;
    }

    private void ReadRoot(string? root, List<OptionProblem> problems)
    {
        if (root == null)
        {
            return;
        }

        if (ThingId.TryRead(new JValue(root), out ThingId id))
        {
            Root = id;
        }
        else
        {
            problems.Add(new OptionProblem("root", "must be a device's id"));
        }
    }

    private void ReadJoinTo(JToken? joinTo, List<OptionProblem> problems)
    {
        if (joinTo == null)
        {
            return;
        }

        JoinTo = NetworkTarget.Of(joinTo);
        if (JoinTo == null)
        {
            problems.Add(new OptionProblem("join_to",
                "must be a network's id, a piece or device on it, or {id, port}"));
        }
    }

    // allow_bridge: each entry a network handle (a network's id, a piece or device on it, or {id, port}).
    private void ReadAllowBridge(JToken? given, List<OptionProblem> problems)
    {
        if (given == null)
        {
            return;
        }

        if (!(given is JArray list))
        {
            problems.Add(new OptionProblem("allow_bridge", "must be a list of networks"));
            return;
        }

        List<NetworkTarget> targets = new List<NetworkTarget>(list.Count);
        for (int index = 0; index < list.Count; index++)
        {
            if (NetworkTarget.Of(list[index]) is { } target)
            {
                targets.Add(target);
            }
            else
            {
                problems.Add(new OptionProblem($"allow_bridge[{index}]",
                    "must be a network's id, a piece or device on it, or {id, port}"));
            }
        }

        AllowBridge = targets;
    }

    // The words a what's option takes, in catalogue order; empty when its schema names none.
    private static IReadOnlyList<string> Words(OptionSpec? option)
    {
        if (option == null || !(option.Schema["enum"] is JArray listed))
        {
            return new string[0];
        }

        List<string> words = new List<string>(listed.Count);
        foreach (JToken word in listed)
        {
            string text = word.ToString();
            if (text.Length > 0)
            {
                words.Add(text);
            }
        }

        return words;
    }
}
