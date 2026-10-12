#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Controllers;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Protocol;

/// <summary>
/// inspect_controllers on the wire (protocol.md, Protocol methods): a read run on the main thread answering what can
/// write each device asked about (ControllersView): chips by pin, id and batch, logic writers, batch writers and
/// mirrors. It takes exactly one of reference_id, reference_ids (1 to 256) or network_id; the index itself is the
/// game's (Controllers.Inspect).
/// </summary>
internal sealed class ControllerCalls
{
    internal const string InspectControllersMethod = "inspect_controllers";

    /// <summary>The most devices one call may list.</summary>
    internal const int MaximumIds = 256;

    private static readonly string[] Arguments = { "reference_id", "reference_ids", "network_id" };

    private readonly Func<ControllerQuery, object> _inspect;

    /// <summary>inspect answers the reply's result, or throws ApiException with the refusal.</summary>
    internal ControllerCalls(Func<ControllerQuery, object> inspect)
    {
        _inspect = inspect ?? throw new ArgumentNullException(nameof(inspect));
    }

    internal static bool Handles(string method) => method == InspectControllersMethod;

    /// <summary>inspect_controllers run on the main thread at this frame: the reply message.</summary>
    internal string Run(ProtocolCall call, double queueWaitMs, long frame)
    {
        Stopwatch watch = Stopwatch.StartNew();
        CallRequest request = call.Request;
        double queueMs = Math.Round(queueWaitMs, 2);
        ErrorView? error = null;
        object? result = null;
        try
        {
            result = _inspect(Query(request.Params));
        }
        catch (ApiException refused)
        {
            error = new ErrorView(refused.Code, refused.Message);
        }
        catch (Exception exception)
        {
            // The request boundary: a bug in the index answers this call, never the frame.
            error = new ErrorView("internal_error", exception.Message);
        }

        double elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 2);
        bool budgetHit = CurrentFrame.BudgetReached(StopwatchClock.Instance.NowMs);
        return ApiJson.WriteShared(error != null
            ? CallReplyView.Failed(request.Id, error, elapsedMs, queueMs, frame, budgetHit)
            : CallReplyView.Of(request.Id, result!, false, elapsedMs, queueMs, frame, budgetHit));
    }

    /// <summary>The query the params ask for; invalid_argument naming the problem otherwise.</summary>
    internal static ControllerQuery Query(JObject? parameters)
    {
        JObject given = parameters ?? new JObject();
        string? named = null;
        foreach (JProperty property in given.Properties())
        {
            if (Array.IndexOf(Arguments, property.Name) < 0)
            {
                throw ApiErrors.InvalidArgument(
                    $"{InspectControllersMethod} has no argument '{Vocabulary.Arg(property.Name)}'; it takes one of " +
                    Vocabulary.Text("{arg:reference_id}, {arg:reference_ids} or {arg:network_id}."));
            }

            if (named != null)
            {
                throw OneOf();
            }

            named = property.Name;
        }

        switch (named)
        {
            case "reference_id":
                return ControllerQuery.Device(Id(given["reference_id"], "reference_id"));
            case "network_id":
                return ControllerQuery.Network(Id(given["network_id"], "network_id"));
            case "reference_ids":
                return ControllerQuery.Devices(Ids(given["reference_ids"]));
            default:
                throw OneOf();
        }
    }

    private static ApiException OneOf() =>
        ApiErrors.InvalidArgument(Vocabulary.Text(
            "Pass exactly one of {arg:reference_id} (a device), {arg:reference_ids} (a list) or {arg:network_id} " +
            "(a cable network)."));

    private static long Id(JToken? token, string name) =>
        ThingId.TryRead(token, out ThingId id)
            ? id.Value
            : throw ApiErrors.InvalidArgument(
                $"Argument '{Vocabulary.Arg(name)}' must be a reference id, such as \"4612280\".");

    private static List<long> Ids(JToken? token)
    {
        if (!(token is JArray listed) || listed.Count == 0 || listed.Count > MaximumIds)
        {
            throw ApiErrors.InvalidArgument(
                $"Argument '{Vocabulary.Arg("reference_ids")}' must be a list of 1 to {MaximumIds} reference ids.");
        }

        List<long> ids = new List<long>(listed.Count);
        for (int index = 0; index < listed.Count; index++)
        {
            if (!ThingId.TryRead(listed[index], out ThingId id))
            {
                throw ApiErrors.InvalidArgument(
                    $"Argument '{Vocabulary.Arg("reference_ids")}[{index}]' must be a reference id, such as \"4612280\".");
            }

            ids.Add(id.Value);
        }

        return ids;
    }
}
