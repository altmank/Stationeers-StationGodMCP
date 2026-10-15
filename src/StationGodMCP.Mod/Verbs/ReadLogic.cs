#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.DeviceReads;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// read logic (catalogue read.json): logic values as a chip's l instruction reads them (ILogicable.GetLogicValue), in
/// the gateway's scope, in one frame (LogicReadRequest). One id and one logic type answer {gateway_id, reference_id,
/// logic_type, value}; id and logic lists, or reads that are all {id, logic} pairs, one row per pair, {index, ok,
/// reference_id, logic_type, value} or the pair's error (SPEC-NOTES 14: flat rows); reads with a logic list, slots,
/// atmosphere or reagents, or include clock, one row per entry (DeviceReads), each part asked for or its error. Each
/// row fails alone. Read only.
/// </summary>
[ReplyFact(FactRule.Live, "logic values")]
internal static class ReadLogic
{
    internal static VerbOutcome Run(ReadLogicOptions options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        if (LogicReadRequest.Of(options, problems) is not { } request)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(ReadLogicOptions.Spec.Label, problems));
        }

        DeviceScope scope = Devices.Scope(options.Gateway);
        string label = ReadLogicOptions.Spec.Label;
        return request switch
        {
            LogicReadRequest.One one => new VerbOutcome.Answered(new LogicReadView(scope.Id, Read(scope, one.Pair, label))),
            LogicReadRequest.Pairs pairs => new VerbOutcome.Answered(Pairs(scope, pairs.Entries, label)),
            LogicReadRequest.Items items => new VerbOutcome.Answered(Items(DeviceReads.Read(scope, items.Request, label))),
            _ => throw new System.InvalidOperationException("read logic has three forms.")
        };
    }

    private static LogicRead Read(DeviceScope scope, LogicPair pair, string label) =>
        LogicOps.Read(scope, TargetIds.Of(pair.Id, label), pair.Logic);

    // One row per pair: {index, ok, reference_id, logic_type, value}, or the pair's error.
    private static ReplyObject Pairs(DeviceScope scope, IReadOnlyList<LogicPair> pairs, string label)
    {
        Rows rows = new Rows();
        foreach (LogicPair pair in pairs)
        {
            LogicRead read;
            try
            {
                read = Read(scope, pair, label);
            }
            catch (ApiException refused)
            {
                rows.Failed(pair.Id, new VerbError(refused.Code, refused.Message));
                continue;
            }

            rows.Succeeded(read.ReferenceId.ToString(),
                new ReplyObject(2).Add("logic_type", read.LogicType).Add("value", read.Value));
        }

        return rows.Reply(new ReplyObject(1).Add("gateway_id", scope.Id));
    }

    // One row per entry: the parts it read, keyed by the names sent, or its error.
    private static ReplyObject Items(ReadDevicesView read)
    {
        Rows rows = new Rows();
        foreach (BatchItemView item in read.Results)
        {
            switch (item)
            {
                case DeviceReadItemView found:
                    rows.Succeeded(found.ReferenceId.ToString(), found);
                    break;
                case DeviceReadFailedView failed:
                    rows.Failed(failed.ReferenceId, new VerbError(failed.Error.Code, failed.Error.Message));
                    break;
                default:
                    throw new System.InvalidOperationException("A device read is an item read or a failed one.");
            }
        }

        ReplyObject top = new ReplyObject(2).Add("gateway_id", read.GatewayId);
        if (read.Clock != null)
        {
            top.Add("clock", read.Clock);
        }

        return rows.Reply(top);
    }
}
