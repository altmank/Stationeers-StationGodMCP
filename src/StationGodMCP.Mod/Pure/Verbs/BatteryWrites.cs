#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// One battery's result, in the call's order: its charge before and after (checked for a report, read back once
/// set), or the refusal that leaves it alone. Each fails alone.
/// </summary>
internal abstract class BatteryResult
{
    private BatteryResult()
    {
    }

    internal sealed class Charged : BatteryResult
    {
        internal Charged(int index, ThingView battery, PowerStoreKind kind, ThingView? heldBy, ChargeChange change)
        {
            Index = index;
            Battery = battery;
            Kind = kind;
            HeldBy = heldBy;
            Change = change;
        }

        public int Index { get; }

        public ThingView Battery { get; }

        public PowerStoreKind Kind { get; }

        public ThingView? HeldBy { get; }

        public ChargeChange Change { get; }
    }

    internal sealed class Refused : BatteryResult
    {
        internal Refused(int index, ThingId? id, ApiException error)
        {
            Index = index;
            Id = id;
            Error = error;
        }

        public int Index { get; }

        public ThingId? Id { get; }

        public ApiException Error { get; }
    }
}

/// <summary>
/// write battery's replies (catalogue write.json, battery): {goal, found_in?, results, count, success_count,
/// error_count, added_j}, one result per battery. The report has the same keys but results: each battery is a target,
/// [id, name, joules now -> joules set], with the what's undo mark, and each refused one a problem with its index.
/// </summary>
internal static class BatteryReplies
{
    private const string ResultsKey = "results";

    internal static JObject Acted(ChargeGoal goal, ThingView? foundIn, IReadOnlyList<BatteryResult> results)
    {
        BatchBuilder batch = new BatchBuilder(results.Count);
        double added = 0.0;
        foreach (BatteryResult result in results)
        {
            switch (result)
            {
                case BatteryResult.Charged charged:
                    added += charged.Change.AddedJoules;
                    batch.Succeeded(View(charged));
                    break;
                case BatteryResult.Refused refused:
                    batch.Failed(new BatteryRefusedView(refused.Index, refused.Id, refused.Error));
                    break;
            }
        }

        return JObject.FromObject(new SetBatteryChargeView(goal, foundIn, batch.Build(), added), ApiJson.Serializer);
    }

    internal static ReportReply Reported(ChargeGoal goal, ThingView? foundIn, IReadOnlyList<BatteryResult> results,
        string undoMark)
    {
        JsonSerializer serializer = ApiJson.Serializer;
        List<ReportTarget> targets = new List<ReportTarget>(results.Count);
        List<ReportProblem> problems = new List<ReportProblem>();
        foreach (BatteryResult result in results)
        {
            switch (result)
            {
                case BatteryResult.Charged charged:
                    BatteryChargedView view = View(charged);
                    targets.Add(new ReportTarget(view.ReferenceId.ToString(), null, view.DisplayName,
                        ReplyFields.Value(view.BeforeJ, serializer), ReplyFields.Value(view.AfterJ, serializer),
                        undoMark));
                    break;
                case BatteryResult.Refused refused:
                    problems.Add(new ReportProblem(refused.Index, refused.Id?.ToString(), VerbError.Of(refused.Error)));
                    break;
            }
        }

        JObject body = Acted(goal, foundIn, results);
        body.Remove(ResultsKey);
        return Report.Of(targets, body, problems);
    }

    private static BatteryChargedView View(BatteryResult.Charged charged) =>
        new BatteryChargedView(charged.Index, charged.Battery, charged.Kind, charged.HeldBy, charged.Change);
}
