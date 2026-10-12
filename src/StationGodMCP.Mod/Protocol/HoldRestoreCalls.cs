#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Protocol;

/// <summary>
/// hold_restore on the wire (protocol.md, Held restores): a call run on the main thread that changes the calling
/// stream's held restores, {set?: [{reference_id, logic_type, value}], clear?: [{reference_id, logic_type}],
/// clear_all?}, applied in that order as clear_all, clear, set, and answered with the stream's restores after the
/// change, {restores: [{reference_id, logic_type, value}]}. The store (HeldRestores) refuses the whole change when an
/// entry names no holdable device or logic type, or a bound would be passed.
/// </summary>
internal sealed class HoldRestoreCalls
{
    internal const string HoldRestoreMethod = "hold_restore";

    /// <summary>The most entries set or clear may each give.</summary>
    internal const int MaximumEntries = HeldRestores.MaximumPerStream;

    private static readonly string[] Arguments = { "set", "clear", "clear_all" };

    private readonly HeldRestores _restores;

    internal HoldRestoreCalls(HeldRestores restores)
    {
        _restores = restores ?? throw new ArgumentNullException(nameof(restores));
    }

    internal static bool Handles(string method) => method == HoldRestoreMethod;

    /// <summary>hold_restore run on the main thread at this frame: the reply message.</summary>
    internal string Run(ProtocolCall call, double queueWaitMs, long frame)
    {
        Stopwatch watch = Stopwatch.StartNew();
        CallRequest request = call.Request;
        double queueMs = Math.Round(queueWaitMs, 2);
        ErrorView? error = null;
        HeldRestoresView? result = null;
        try
        {
            JObject given = request.Params ?? new JObject();
            foreach (JProperty property in given.Properties())
            {
                if (Array.IndexOf(Arguments, property.Name) < 0)
                {
                    throw ApiErrors.InvalidArgument(
                        $"hold_restore has no argument '{property.Name}'; it takes set, clear and clear_all.");
                }
            }

            Args args = new Args(given);
            List<(ThingId Id, JToken? LogicType, double Value)> set =
                new List<(ThingId, JToken?, double)>();
            foreach (Args entry in Entries(args, "set"))
            {
                set.Add((entry.ThingId("reference_id"), entry.Optional("logic_type"), entry.Double("value")));
            }

            List<(ThingId Id, JToken? LogicType)> clear = new List<(ThingId, JToken?)>();
            foreach (Args entry in Entries(args, "clear"))
            {
                clear.Add((entry.ThingId("reference_id"), entry.Optional("logic_type")));
            }

            string? token = (call.Source as Connection)?.ResumeToken;
            result = new HeldRestoresView(_restores.Change(call.Context, token, args.OptionalBool("clear_all") ?? false,
                clear, set));
        }
        catch (ApiException refused)
        {
            error = new ErrorView(refused.Code, refused.Message, refused.Details);
        }
        catch (Exception exception)
        {
            // The request boundary: a bug in the store answers this call, never the frame.
            error = new ErrorView("internal_error", exception.Message);
        }

        double elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 2);
        bool budgetHit = CurrentFrame.BudgetReached(StopwatchClock.Instance.NowMs);
        return ApiJson.WriteShared(error != null
            ? CallReplyView.Failed(request.Id, error, elapsedMs, queueMs, frame, budgetHit)
            : CallReplyView.Of(request.Id, result!, false, elapsedMs, queueMs, frame, budgetHit));
    }

    // A list argument's entries, each an object read with its path ("set[2]") in every message; absent is empty.
    private static List<Args> Entries(Args args, string name)
    {
        List<Args> entries = new List<Args>();
        if (!args.Has(name))
        {
            return entries;
        }

        JArray listed = args.Optional(name) as JArray ??
                        throw ApiErrors.InvalidArgument($"Argument '{name}' must be a list of objects.");
        if (listed.Count > MaximumEntries)
        {
            throw ApiErrors.InvalidArgument(
                $"Argument '{name}' takes at most {MaximumEntries} entries.");
        }

        for (int index = 0; index < listed.Count; index++)
        {
            entries.Add(listed[index] is JObject entry
                ? new Args(entry, $"{name}[{index}]")
                : throw ApiErrors.InvalidArgument($"Argument '{name}[{index}]' must be an object."));
        }

        return entries;
    }
}
