#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.DeviceReads;
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
        JsonSerializer serializer = ApiJson.Fresh();
        string label = ReadLogicOptions.Spec.Label;
        return request switch
        {
            LogicReadRequest.One one => new VerbOutcome.Answered(IdAnswers.Payload(
                new LogicReadView(scope.Id, Read(scope, one.Pair, label)), serializer)),
            LogicReadRequest.Pairs pairs => new VerbOutcome.Answered(Pairs(scope, pairs.Entries, label, serializer)),
            LogicReadRequest.Items items => new VerbOutcome.Answered(Items(
                DeviceReads.Read(scope, items.Request, label), serializer)),
            _ => throw new System.InvalidOperationException("read logic has three forms.")
        };
    }

    private static LogicRead Read(DeviceScope scope, LogicPair pair, string label) =>
        LogicOps.Read(scope, TargetIds.Of(pair.Id, label), pair.Logic);

    // One row per pair: {index, ok, reference_id, logic_type, value}, or the pair's error.
    private static JObject Pairs(DeviceScope scope, IReadOnlyList<LogicPair> pairs, string label,
        JsonSerializer serializer)
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

            rows.Succeeded(read.ReferenceId.ToString(), new JObject
            {
                ["logic_type"] = JToken.FromObject(read.LogicType, serializer),
                ["value"] = ReplyFields.Value(read.Value, serializer)
            });
        }

        return rows.Reply(new JObject { ["gateway_id"] = scope.Id });
    }

    // One row per entry: the parts it read, keyed by the names sent, or its error.
    private static JObject Items(ReadDevicesView read, JsonSerializer serializer)
    {
        Rows rows = new Rows();
        foreach (BatchItemView item in read.Results)
        {
            JObject row = JObject.FromObject(item, serializer);
            string? referenceId = (string?)row["reference_id"];
            if (row["error"] is JObject error)
            {
                rows.Failed(referenceId, new VerbError((string)error["code"]!, (string)error["message"]!, error["data"]));
            }
            else
            {
                rows.Succeeded(referenceId, row);
            }
        }

        JObject top = new JObject { ["gateway_id"] = read.GatewayId };
        if (read.Clock != null)
        {
            top["clock"] = JToken.FromObject(read.Clock, serializer);
        }

        return rows.Reply(top);
    }
}
