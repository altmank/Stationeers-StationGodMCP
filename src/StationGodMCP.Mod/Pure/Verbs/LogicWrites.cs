#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>One logic write a call asks for: its place in the call, the device's id as given, the logic type and value.</summary>
internal sealed class LogicOrder
{
    internal LogicOrder(int index, string id, JToken logic, double value)
    {
        Index = index;
        Id = id;
        Logic = logic;
        Value = value;
    }

    public int Index { get; }

    public string Id { get; }

    public JToken Logic { get; }

    public double Value { get; }
}

/// <summary>
/// write logic's writes (catalogue write.json): id (one or a list), each given logic = value, or writes, each
/// {id, logic, value}, in order. Every problem with the options is listed at once; neither form is missing_target.
/// </summary>
internal sealed class LogicOrders
{
    internal LogicOrders(IReadOnlyList<LogicOrder> orders, bool answersRows)
    {
        Orders = orders;
        AnswersRows = answersRows;
    }

    public IReadOnlyList<LogicOrder> Orders { get; }

    public bool AnswersRows { get; }

    internal const string MissingTarget = "missing_target";

    internal static VerbError Missing(string label) =>
        new VerbError(MissingTarget, $"{label}: give id with logic and value, or writes: [{{id, logic, value}}].",
            new JObject { ["options"] = new JArray("id", "writes") });

    /// <summary>The writes the options ask for; null with the problems added, or with no target at all.</summary>
    internal static LogicOrders? Of(PerIdList? ids, JToken? logic, double? value, JToken? writes,
        List<OptionProblem> problems)
    {
        if (ids != null && writes != null)
        {
            problems.Add(new OptionProblem("writes", "goes without id: each entry names its own device"));
            return null;
        }

        if (writes != null)
        {
            if (logic != null)
            {
                problems.Add(new OptionProblem("logic", "goes with id; each writes entry names its own"));
            }

            if (value != null)
            {
                problems.Add(new OptionProblem("value", "goes with id; each writes entry names its own"));
            }

            List<LogicOrder> listed = Entries(writes, problems);
            return problems.Count == 0 ? new LogicOrders(listed, true) : null;
        }

        if (ids == null)
        {
            return null;
        }

        if (logic == null)
        {
            problems.Add(new OptionProblem("logic", "is needed with id: a logic type's name or number"));
        }

        if (value == null)
        {
            problems.Add(new OptionProblem("value", "is needed with id: a finite number"));
        }
        else if (double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            problems.Add(new OptionProblem("value", "must be a finite number"));
        }

        if (problems.Count > 0)
        {
            return null;
        }

        List<LogicOrder> orders = new List<LogicOrder>(ids.Ids.Count);
        foreach (string id in ids.Ids)
        {
            orders.Add(new LogicOrder(orders.Count, id, logic!, value!.Value));
        }

        return new LogicOrders(orders, ids.AnswersRows);
    }

    private static List<LogicOrder> Entries(JToken writes, List<OptionProblem> problems)
    {
        List<LogicOrder> orders = new List<LogicOrder>();
        if (writes is not JArray entries)
        {
            problems.Add(new OptionProblem("writes", "must be a list of {id, logic, value}"));
            return orders;
        }

        for (int index = 0; index < entries.Count; index++)
        {
            string path = $"writes[{index}]";
            if (entries[index] is not JObject entry)
            {
                problems.Add(new OptionProblem(path, "must be {id, logic, value}"));
                continue;
            }

            string? id = entry["id"] is { Type: JTokenType.String } given ? ((string)given!).Trim() : null;
            JToken? logic = entry["logic"] is { Type: not JTokenType.Null } named ? named : null;
            double? value = OptionReader.NumberOf(entry["value"]);
            if (string.IsNullOrEmpty(id))
            {
                problems.Add(new OptionProblem(path + ".id", "is needed: a device's id"));
            }

            if (logic == null)
            {
                problems.Add(new OptionProblem(path + ".logic", "is needed: a logic type's name or number"));
            }

            if (value is not { } number || double.IsNaN(number) || double.IsInfinity(number))
            {
                problems.Add(new OptionProblem(path + ".value", "is needed: a finite number"));
            }

            foreach (JProperty key in entry.Properties())
            {
                if (key.Name is not ("id" or "logic" or "value"))
                {
                    problems.Add(new OptionProblem(path + "." + key.Name, "is not taken: an entry is {id, logic, value}"));
                }
            }

            if (!string.IsNullOrEmpty(id) && logic != null && value is { } written && !double.IsNaN(written) &&
                !double.IsInfinity(written))
            {
                orders.Add(new LogicOrder(index, id!, logic, written));
            }
        }

        return orders;
    }
}

/// <summary>
/// One logic write's result, in the call's order: checked for a report (Planned), written (Written), or refused by a
/// check of the game (Failed); each fails alone. Name is the device's display name, Id the id as given when the write
/// failed before its device was found.
/// </summary>
internal abstract class LogicWriteResult
{
    private LogicWriteResult()
    {
    }

    internal sealed class Planned : LogicWriteResult
    {
        internal Planned(int index, string? name, LogicWriteChecked write)
        {
            Index = index;
            Name = name;
            Write = write;
        }

        public int Index { get; }

        public string? Name { get; }

        public LogicWriteChecked Write { get; }
    }

    internal sealed class Written : LogicWriteResult
    {
        internal Written(int index, string? name, LogicWrite write)
        {
            Index = index;
            Name = name;
            Write = write;
        }

        public int Index { get; }

        public string? Name { get; }

        public LogicWrite Write { get; }
    }

    internal sealed class Failed : LogicWriteResult
    {
        internal Failed(int index, string? id, VerbError error)
        {
            Index = index;
            Id = id;
            Error = error;
        }

        public int Index { get; }

        public string? Id { get; }

        public VerbError Error { get; }
    }
}

/// <summary>
/// write logic's replies (catalogue write.json, logic): one write {gateway_id, reference_id, logic_type,
/// requested_value, previous_value, current_value}; several, one row each {index, ok, reference_id, logic_type,
/// requested_value, previous_value, current_value} or its error, under {gateway_id}. A row or reply carries driven_by
/// when a controller writes the same logic type on the device; the writes' warnings (each with its index in a list)
/// and passed: ["lint"] go to the reply. A report (Report.Of) lists every checked write as a target, [id, name (logic),
/// value now -> value asked], each refused one as a problem.
/// </summary>
internal static class LogicWriteReplies
{
    private const string Undo = "[undo]";
    private const string CannotUndoAction = "[cannot undo: action]";

    // The logic types that fire an action rather than hold a setting: writing back the value before does not undo them.
    private static readonly string[] ActionTypes = { "Activate", "ClearMemory", "Reset", "Pulse", "Clear" };

    /// <summary>A logic write's undo mark: [cannot undo: action] for an action type, else [undo].</summary>
    internal static string Mark(LogicTypeView type)
    {
        foreach (string action in ActionTypes)
        {
            if (string.Equals(action, type.Name, StringComparison.OrdinalIgnoreCase))
            {
                return CannotUndoAction;
            }
        }

        return Undo;
    }

    /// <summary>The acting answer: one write's reply or refusal, or every write's row.</summary>
    internal static VerbOutcome Acted(string gatewayId, IReadOnlyList<LogicWriteResult> results, bool answersRows)
    {
        ReplyObject top = new ReplyObject().Add("gateway_id", gatewayId);
        if (!answersRows && results.Count == 1)
        {
            switch (results[0])
            {
                case LogicWriteResult.Failed failed:
                    return new VerbOutcome.Refused(failed.Error);
                case LogicWriteResult.Written written:
                    ReplyObject one = new ReplyObject().Spread(top)
                        .Spread(Payload(written.Write.Checked, written.Write.CurrentValue));
                    AddChecks(one, results);
                    return new VerbOutcome.Answered(one);
            }
        }

        Rows rows = new Rows();
        foreach (LogicWriteResult result in results)
        {
            switch (result)
            {
                case LogicWriteResult.Written written:
                    rows.Succeeded(written.Write.Checked.ReferenceId.ToString(),
                        Payload(written.Write.Checked, written.Write.CurrentValue));
                    break;
                case LogicWriteResult.Failed failed:
                    rows.Failed(failed.Id, failed.Error);
                    break;
            }
        }

        ReplyObject reply = rows.Reply(top);
        AddChecks(reply, results);
        return new VerbOutcome.Answered(reply);
    }

    /// <summary>The report: every checked write a target, every refused one a problem, and the writes' warnings.</summary>
    internal static ReportReply Reported(string gatewayId, IReadOnlyList<LogicWriteResult> results)
    {
        JsonSerializer serializer = ApiJson.Serializer;
        List<ReportTarget> targets = new List<ReportTarget>();
        List<ReportProblem> problems = new List<ReportProblem>();
        foreach (LogicWriteResult result in results)
        {
            switch (result)
            {
                case LogicWriteResult.Planned planned:
                    LogicWriteChecked write = planned.Write;
                    targets.Add(new ReportTarget(write.ReferenceId.ToString(), null, NameOf(planned.Name, write.LogicType),
                        ReplyFields.Value(write.PreviousValue, serializer), write.RequestedValue, Mark(write.LogicType),
                        write.LintPassed ? new[] { WriteLint.Guard } : null,
                        write.DrivenBy != null ? JToken.FromObject(write.DrivenBy, serializer) : null));
                    break;
                case LogicWriteResult.Failed failed:
                    problems.Add(new ReportProblem(failed.Index, failed.Id, failed.Error));
                    break;
            }
        }

        ReplyObject body = new ReplyObject().Add("gateway_id", gatewayId);
        AddChecks(body, results);
        return Report.Of(targets, body, problems);
    }

    private static string NameOf(string? name, LogicTypeView type) =>
        $"{name ?? "?"} ({type.Name ?? type.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)})";

    // {reference_id, logic_type, requested_value, previous_value, current_value, driven_by?}
    private static ReplyObject Payload(LogicWriteChecked write, double? current)
    {
        ReplyObject payload = new ReplyObject()
            .Add("reference_id", write.ReferenceId.ToString())
            .Add("logic_type", write.LogicType)
            .Add("requested_value", write.RequestedValue)
            .Add("previous_value", write.PreviousValue)
            .Add("current_value", current);
        if (write.DrivenBy != null)
        {
            payload.Add("driven_by", write.DrivenBy);
        }

        return payload;
    }

    // The writes' warnings, each with its index in a list, and passed: ["lint"] when allow let a lint problem through.
    private static void AddChecks(ReplyObject reply, IReadOnlyList<LogicWriteResult> results)
    {
        List<BuildIssueView> warnings = new List<BuildIssueView>();
        bool passed = false;
        foreach (LogicWriteResult result in results)
        {
            LogicWriteChecked? write = result switch
            {
                LogicWriteResult.Planned planned => planned.Write,
                LogicWriteResult.Written written => written.Write.Checked,
                _ => null
            };
            if (write != null)
            {
                warnings.AddRange(write.Warnings);
                passed |= write.LintPassed;
            }
        }

        if (warnings.Count > 0)
        {
            reply.Add("warnings", warnings);
        }

        if (passed)
        {
            reply.Add("passed", new[] { WriteLint.Guard });
        }
    }
}
