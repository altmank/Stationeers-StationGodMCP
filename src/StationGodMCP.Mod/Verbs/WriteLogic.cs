#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write logic (catalogue write.json): logic values written as a chip's s instruction writes them
/// (ILogicable.SetLogicValue), in the gateway's scope, with the value read before and after when the type also reads
/// (LogicOps). id (one or a list) each gets logic = value; writes are {id, logic, value} each, in order. One id answers
/// its write; a list or writes answer one row per write, each failing alone. A write that opens a vent, valve or pump
/// runs the network lint first, and a problem refuses it (lint_refused) unless allow: ["lint"]. Without confirm the
/// call reports every write from the same checks and writes nothing (Report.Of). A write is held for the calling
/// stream's restores (HeldRestores) through LogicOps.Apply.
/// </summary>
[ReplyFact(FactRule.Live, "what it changes, read and checked when it acts")]
internal static class WriteLogic
{
    internal static VerbOutcome Run(WriteLogicOptions options)
    {
        string label = WriteLogicOptions.Spec.Label;
        List<OptionProblem> problems = new List<OptionProblem>();
        LogicOrders? orders = LogicOrders.Of(options.Selector.Ids, options.Logic, options.Value, options.Writes,
            problems);
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label, problems));
        }

        if (orders == null)
        {
            return new VerbOutcome.Refused(LogicOrders.Missing(label));
        }

        DeviceScope scope = Devices.Scope(options.Gateway);
        bool allowLint = Allow.Lifts(options.Allow, WriteLint.Guard);
        bool report = !options.Report.Acts;
        List<LogicWriteResult> results = LogicOps.WriteMany(scope, orders.Orders, allowLint, report,
            orders.AnswersRows, label);
        return report
            ? new VerbOutcome.Answered(LogicWriteReplies.Reported(scope.Id, results))
            : LogicWriteReplies.Acted(scope.Id, results, orders.AnswersRows);
    }
}
