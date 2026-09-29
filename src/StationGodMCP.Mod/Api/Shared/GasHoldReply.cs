#nullable enable

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// What the gas hold meant for one request, collected while it runs (requests run one at a time on the main thread)
/// and added to its reply as gas_hold. A request can judge several runs (undo_job checks and starts one per tool): the
/// reply keeps a lift a started run made over any refusal, a refusal over anything else, and otherwise the first.
/// Nothing is added when the hold did not apply and no acknowledgement was given.
/// </summary>
internal static class GasHoldReply
{
    private static GasHoldVerdict? _verdict;
    private static GasHoldStage _stage;
    private static GasHoldVerdict.Refusing? _refusal;

    internal static void Begin()
    {
        _verdict = null;
        _refusal = null;
    }

    internal static void Record(GasHoldVerdict verdict, GasHoldStage stage)
    {
        if (!verdict.Reportable)
        {
            return;
        }

        if (verdict is GasHoldVerdict.Refusing refusing)
        {
            _refusal ??= refusing;
        }

        if (_verdict == null || Rank(verdict, stage) > Rank(_verdict, _stage))
        {
            _verdict = verdict;
            _stage = stage;
        }
    }

    /// <summary>The first run of this request the hold refuses (or, in a dry run, would refuse), or null.</summary>
    internal static GasHoldVerdict.Refusing? Refusal => _refusal;

    /// <summary>What was recorded since Begin, as the reply's gas_hold, or null; then forgotten.</summary>
    internal static GasHoldView? Take()
    {
        GasHoldView? view = _verdict != null ? GasHoldView.Of(_verdict, _stage) : null;
        Begin();
        return view;
    }

    /// <summary>The result with gas_hold added when anything was recorded (object results only).</summary>
    internal static object Attach(object result, GasHoldView? hold)
    {
        if (hold == null)
        {
            return result;
        }

        JsonSerializer serializer = JsonSerializer.Create(ApiJson.Settings);
        if (!(JToken.FromObject(result, serializer) is JObject item))
        {
            return result;
        }

        item["gas_hold"] = JToken.FromObject(hold, serializer);
        return item;
    }

    private static int Rank(GasHoldVerdict verdict, GasHoldStage stage) =>
        verdict is GasHoldVerdict.Lifting && stage == GasHoldStage.Started ? 2
        : verdict is GasHoldVerdict.Refusing ? 1
        : 0;
}
