#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// move gas's order (catalogue move.json, gas): the source, to or delete (one of them), the gases (every one when left
/// out; a room or the planet needs them named), amount_mol per gas (all when left out), allow burst and joined. The
/// planet only gives gas up: source "planet" goes with delete and named gases.
/// </summary>
internal sealed class GasMoveOrder
{
    internal GasMoveOrder(GasPlaceOption source, GasPlaceOption? to, IReadOnlyList<string>? gases, double? amountMol,
        bool allowBurst, bool joined)
    {
        Source = source;
        To = to;
        Gases = gases;
        AmountMol = amountMol;
        AllowBurst = allowBurst;
        Joined = joined;
    }

    public GasPlaceOption Source { get; }

    public GasPlaceOption? To { get; }

    public IReadOnlyList<string>? Gases { get; }

    public double? AmountMol { get; }

    public bool AllowBurst { get; }

    public bool Joined { get; }

    private const string SourceOption = "source";
    private const string ToOption = "to";
    private const string DeleteOption = "delete";
    private const string GasesOption = "gases";

    /// <summary>True when the gas is destroyed rather than moved.</summary>
    internal bool Deletes => To == null;

    /// <summary>The order the options give; null with every problem added.</summary>
    internal static GasMoveOrder? Of(JToken? source, JToken? to, bool delete, IReadOnlyList<string>? gases,
        double? amountMol, bool allowBurst, bool joined, List<OptionProblem> problems)
    {
        int before = problems.Count;
        GasPlaceOption? from = null;
        if (source == null)
        {
            problems.Add(new OptionProblem(SourceOption,
                "is needed: a thing's id, \"looking_at\", \"planet\" (with delete), {\"room\"} or {\"room_of\"}"));
        }
        else
        {
            from = GasPlaceOption.Of(source, SourceOption, problems);
        }

        GasPlaceOption? into = to == null ? null : GasPlaceOption.Of(to, ToOption, problems);
        if (from is GasPlaceOption.Planet)
        {
            if (!delete || to != null)
            {
                problems.Add(new OptionProblem(DeleteOption,
                    "must be true, with no to, when source is \"planet\": the planet only gives gas up"));
            }

            if (gases == null)
            {
                problems.Add(new OptionProblem(GasesOption,
                    "names the gases to take out of the planet's air; taking all of it is refused"));
            }
        }
        else if ((to != null) == delete)
        {
            problems.Add(new OptionProblem(ToOption, delete
                ? "goes without delete: give one of them"
                : "or delete: true is needed: where the gas goes, or that it is destroyed"));
        }

        if (from is { IsRoom: true } && gases == null)
        {
            problems.Add(new OptionProblem(GasesOption,
                "names the gases to take out of a room: a room's air is never emptied by omission"));
        }

        if (amountMol is <= 0.0 || amountMol is { } amount && (double.IsNaN(amount) || double.IsInfinity(amount)))
        {
            problems.Add(new OptionProblem("amount_mol", "must be more than 0"));
        }

        return problems.Count == before
            ? new GasMoveOrder(from!, delete ? null : into, gases, amountMol, allowBurst, joined)
            : null;
    }
}

/// <summary>
/// move gas's replies (catalogue move.json, gas). A confirmed move is queued for the next atmospherics tick and
/// answers its prediction: {transfer_id, status: queued, predicted, joined, from, to, deleted, moved, error}. The report
/// has the prediction's keys without transfer_id and status, and one target per side, [the atmosphere's owner or room,
/// name, total_mol before -> after], with the what's undo mark.
/// </summary>
internal static class GasReplies
{
    private const string TransferIdKey = "transfer_id";
    private const string StatusKey = "status";

    internal static JObject Acted(object view) => JObject.FromObject(view, ApiJson.Fresh());

    internal static JObject Reported(MoveGasView predicted, string undoMark)
    {
        JsonSerializer serializer = ApiJson.Fresh();
        List<ReportTarget> targets = new List<ReportTarget>(2);
        foreach (GasSideView? side in new[] { predicted.From, predicted.To })
        {
            if (side != null)
            {
                targets.Add(Target(side, undoMark, serializer));
            }
        }

        JObject body = Acted(predicted);
        body.Remove(TransferIdKey);
        body.Remove(StatusKey);
        return Report.Of(targets, body, new List<ReportProblem>());
    }

    /// <summary>A planet removal's report: its keys without status, and the planet's air as the one target.</summary>
    internal static JObject ReportedPlanet(JObject removal, double beforeMol, double afterMol, string undoMark)
    {
        JsonSerializer serializer = ApiJson.Fresh();
        JObject body = (JObject)removal.DeepClone();
        body.Remove(StatusKey);
        ReportTarget target = new ReportTarget("planet", null, "the planet's air",
            ReplyFields.Value(beforeMol, serializer), ReplyFields.Value(afterMol, serializer), undoMark);
        return Report.Of(new[] { target }, body, new List<ReportProblem>());
    }

    private static ReportTarget Target(GasSideView side, string undoMark, JsonSerializer serializer)
    {
        GasOwnerView? owner = side.Members.Count > 0 ? side.Members[0].Owner : null;
        string? id = side.Room != null ? side.Room.RoomId : owner?.ReferenceId.ToString();
        string? name = side.Room != null ? $"room {side.Room.RoomId} ({side.Room.RoomType})" : owner?.DisplayName;
        return new ReportTarget(id, null, name, ReplyFields.Value(side.Total.Before.TotalMol, serializer),
            ReplyFields.Value(side.Total.After.TotalMol, serializer), undoMark);
    }
}
