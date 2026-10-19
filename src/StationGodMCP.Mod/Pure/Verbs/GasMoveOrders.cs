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
/// planet only gives gas up: source "planet" goes with delete and named gases. Source "create" makes gas out of
/// nothing: it goes with to, named gases, amount_mol (each gas's moles) and temperature_k.
/// </summary>
internal sealed class GasMoveOrder
{
    internal GasMoveOrder(GasPlaceOption source, GasPlaceOption? to, IReadOnlyList<string>? gases, double? amountMol,
        bool allowBurst, bool joined, double? temperatureK = null)
    {
        TemperatureK = temperatureK;
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

    /// <summary>The temperature gas made out of nothing arrives at (source "create"); null for any other source.</summary>
    public double? TemperatureK { get; }

    /// <summary>True when the gas is made out of nothing rather than taken from a source.</summary>
    internal bool Creates => Source is GasPlaceOption.Created;

    private const string SourceOption = "source";
    private const string ToOption = "to";
    private const string DeleteOption = "delete";
    private const string GasesOption = "gases";
    private const string TemperatureOption = "temperature_k";

    /// <summary>True when the gas is destroyed rather than moved.</summary>
    internal bool Deletes => To == null;

    /// <summary>The order the options give; null with every problem added.</summary>
    internal static GasMoveOrder? Of(JToken? source, JToken? to, bool delete, IReadOnlyList<string>? gases,
        double? amountMol, bool allowBurst, bool joined, List<OptionProblem> problems, double? temperatureK = null)
    {
        int before = problems.Count;
        GasPlaceOption? from = null;
        if (source == null)
        {
            problems.Add(new OptionProblem(SourceOption,
                "is needed: a thing's id, \"looking_at\", \"planet\" (with delete), \"create\" (with to), {\"room\"} or " +
                "{\"room_of\"}"));
        }
        else
        {
            from = GasPlaceOption.Of(source, SourceOption, problems);
        }

        GasPlaceOption? into = to == null ? null : GasPlaceOption.Of(to, ToOption, problems);
        if (into is GasPlaceOption.Created or GasPlaceOption.Planet)
        {
            problems.Add(new OptionProblem(ToOption, "is a thing's id, \"looking_at\" or a room: gas is made or taken " +
                "only at the source"));
        }

        if (from is GasPlaceOption.Created)
        {
            if (delete || to == null)
            {
                problems.Add(new OptionProblem(ToOption,
                    "is needed, with no delete, when source is \"create\": where the made gas goes"));
            }

            if (gases == null)
            {
                problems.Add(new OptionProblem(GasesOption, "names the gases to make"));
            }

            if (amountMol == null)
            {
                problems.Add(new OptionProblem("amount_mol", "is needed with source \"create\": the moles of each gas"));
            }

            if (temperatureK is not { } kelvin)
            {
                problems.Add(new OptionProblem(TemperatureOption,
                    "is needed with source \"create\": the temperature the gas arrives at"));
            }
            else if (!(kelvin > 0.0) || double.IsInfinity(kelvin))
            {
                problems.Add(new OptionProblem(TemperatureOption, "must be more than 0"));
            }
        }
        else if (from is GasPlaceOption.Planet)
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

        if (temperatureK != null && from is not GasPlaceOption.Created)
        {
            problems.Add(new OptionProblem(TemperatureOption,
                "goes with source \"create\": moved gas keeps its own heat"));
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
            ? new GasMoveOrder(from!, delete ? null : into, gases, amountMol, allowBurst, joined, temperatureK)
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

    internal static object Acted(object view) => view;

    internal static ReportReply Reported(MoveGasView predicted, string undoMark)
    {
        JsonSerializer serializer = ApiJson.Serializer;
        List<ReportTarget> targets = new List<ReportTarget>(2);
        foreach (GasSideView? side in new[] { predicted.From, predicted.To })
        {
            if (side != null)
            {
                targets.Add(Target(side, undoMark));
            }
        }

        return Report.Of(targets, new ReplyObject().Spread(predicted, TransferIdKey, StatusKey), new List<ReportProblem>());
    }

    /// <summary>A planet removal's report: its keys without status, and the planet's air as the one target.</summary>
    internal static ReportReply ReportedPlanet(object removal, double beforeMol, double afterMol, string undoMark)
    {
        ReportTarget target = new ReportTarget("planet", null, "the planet's air",
            beforeMol, afterMol, undoMark);
        return Report.Of(new[] { target }, new ReplyObject().Spread(removal, StatusKey), new List<ReportProblem>());
    }

    private static ReportTarget Target(GasSideView side, string undoMark)
    {
        GasOwnerView? owner = side.Members.Count > 0 ? side.Members[0].Owner : null;
        string? id = side.Room != null ? side.Room.RoomId : owner?.ReferenceId.ToString();
        string? name = side.Room != null ? $"room {side.Room.RoomId} ({side.Room.RoomType})" : owner?.DisplayName;
        return new ReportTarget(id, null, name, side.Total.Before.TotalMol,
            side.Total.After.TotalMol, undoMark);
    }
}

