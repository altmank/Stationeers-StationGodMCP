#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>One thing to paint: its place in the call, its id as given, and the colour as given.</summary>
internal sealed class PaintOrder
{
    internal PaintOrder(int index, string id, string color)
    {
        Index = index;
        Id = id;
        Color = color;
    }

    public int Index { get; }

    public string Id { get; }

    public string Color { get; }
}

/// <summary>
/// What write paint is asked (catalogue write.json, paint): with no target, the game's colours (Colors); things by id,
/// one color for all, or items, a color each (Things); or a pipe, cable or chute network's pieces, and its devices
/// with include devices, one color for all (Network: the game finds the members). Every problem at once.
/// </summary>
internal abstract class PaintRequest
{
    private PaintRequest()
    {
    }

    internal sealed class Colors : PaintRequest
    {
    }

    internal sealed class Things : PaintRequest
    {
        internal Things(IReadOnlyList<PaintOrder> orders)
        {
            Orders = orders;
        }

        public IReadOnlyList<PaintOrder> Orders { get; }
    }

    internal sealed class Network : PaintRequest
    {
        internal Network(NetworkTarget target, string? kind, bool devices, string color)
        {
            Target = target;
            Kind = kind;
            Devices = devices;
            Color = color;
        }

        public NetworkTarget Target { get; }

        public string? Kind { get; }

        public bool Devices { get; }

        public string Color { get; }
    }

    /// <summary>The request the options make; null with every problem added.</summary>
    internal static PaintRequest? Of(PerIdList? ids, NetworkTarget? network, string? kind, bool devices,
        string? color, JToken? items, List<OptionProblem> problems)
    {
        int targets = (ids != null ? 1 : 0) + (network != null ? 1 : 0) + (items != null ? 1 : 0);
        if (targets > 1)
        {
            problems.Add(new OptionProblem(ids != null ? "id" : "network",
                "id, network and items go one at a time: paint things, a network, or {id, color} items"));
        }

        if (network == null && kind != null)
        {
            problems.Add(new OptionProblem("kind", "goes with network: which of a device's networks"));
        }

        if (network == null && devices)
        {
            problems.Add(new OptionProblem("include", "devices goes with network"));
        }

        if (items != null && color != null)
        {
            problems.Add(new OptionProblem("color", "goes with id or network; each items entry carries its own"));
        }

        if (targets == 0 && color != null)
        {
            problems.Add(new OptionProblem("color", "needs id or network: the things to paint"));
        }

        if ((ids != null || network != null) && color == null)
        {
            problems.Add(new OptionProblem("color", "is needed: a colour name, an index, or 'default'"));
        }

        List<PaintOrder> orders = items != null ? Entries(items, problems) : new List<PaintOrder>();
        if (problems.Count > 0)
        {
            return null;
        }

        if (network != null)
        {
            return new Network(network, kind, devices, color!);
        }

        if (ids != null)
        {
            foreach (string id in ids.Ids)
            {
                orders.Add(new PaintOrder(orders.Count, id, color!));
            }
        }

        return ids != null || items != null ? new Things(orders) : new Colors();
    }

    private static List<PaintOrder> Entries(JToken items, List<OptionProblem> problems)
    {
        List<PaintOrder> orders = new List<PaintOrder>();
        if (items is not JArray entries)
        {
            problems.Add(new OptionProblem("items", "must be a list of {id, color}"));
            return orders;
        }

        for (int index = 0; index < entries.Count; index++)
        {
            string path = $"items[{index}]";
            if (entries[index] is not JObject entry)
            {
                problems.Add(new OptionProblem(path, "must be {id, color}"));
                continue;
            }

            string? id = entry["id"] is { Type: JTokenType.String } given ? ((string)given!).Trim() : null;
            string? color = entry["color"] is { Type: JTokenType.String } named ? (string)named! : null;
            if (string.IsNullOrEmpty(id))
            {
                problems.Add(new OptionProblem(path + ".id", "is needed: a thing's id"));
            }

            if (color == null)
            {
                problems.Add(new OptionProblem(path + ".color", "is needed: a colour name, an index, or 'default'"));
            }

            foreach (JProperty key in entry.Properties())
            {
                if (key.Name is not ("id" or "color"))
                {
                    problems.Add(new OptionProblem(path + "." + key.Name, "is not taken: an entry is {id, color}"));
                }
            }

            if (!string.IsNullOrEmpty(id) && color != null)
            {
                orders.Add(new PaintOrder(index, id!, color));
            }
        }

        return orders;
    }
}

/// <summary>
/// One paint's result, in the call's order: checked for a report (Planned: the thing's name, its colour now and the
/// colour it would get), painted (Painted: the colour before and after), or refused by a check of the game (Failed);
/// each fails alone.
/// </summary>
internal abstract class PaintResult
{
    private PaintResult()
    {
    }

    internal sealed class Planned : PaintResult
    {
        internal Planned(ThingId referenceId, string? name, ThingColorView now, string? color)
        {
            ReferenceId = referenceId;
            Name = name;
            Now = now;
            Color = color;
        }

        public ThingId ReferenceId { get; }

        public string? Name { get; }

        public ThingColorView Now { get; }

        public string? Color { get; }
    }

    internal sealed class Painted : PaintResult
    {
        internal Painted(ThingId referenceId, ThingColorView previous, ThingColorView color)
        {
            ReferenceId = referenceId;
            Previous = previous;
            Color = color;
        }

        public ThingId ReferenceId { get; }

        public ThingColorView Previous { get; }

        public ThingColorView Color { get; }
    }

    internal sealed class Failed : PaintResult
    {
        internal Failed(int index, string id, VerbError error)
        {
            Index = index;
            Id = id;
            Error = error;
        }

        public int Index { get; }

        public string Id { get; }

        public VerbError Error { get; }
    }
}

/// <summary>
/// write paint's replies (catalogue write.json, paint): the colours {colors: [{index, name, paint_only}], count}; for
/// targets one row each, {index, ok, reference_id, previous_color, color} or its error, colours as {index, name,
/// is_default}. A report lists each thing as a target, [id, name, colour now -> colour asked], [undo].
/// </summary>
internal static class PaintReplies
{
    private const string Undo = "[undo]";

    internal static JObject Colors(PaintColorsView colors) => JObject.FromObject(colors, ApiJson.Serializer);

    internal static ReplyObject Acted(IReadOnlyList<PaintResult> results)
    {
        JsonSerializer serializer = ApiJson.Serializer;
        Rows rows = new Rows();
        foreach (PaintResult result in results)
        {
            switch (result)
            {
                case PaintResult.Painted painted:
                    rows.Succeeded(painted.ReferenceId.ToString(), new JObject
                    {
                        ["previous_color"] = JToken.FromObject(painted.Previous, serializer),
                        ["color"] = JToken.FromObject(painted.Color, serializer)
                    });
                    break;
                case PaintResult.Failed failed:
                    rows.Failed(failed.Id, failed.Error);
                    break;
            }
        }

        return rows.Reply();
    }

    internal static ReportReply Reported(IReadOnlyList<PaintResult> results)
    {
        List<ReportTarget> targets = new List<ReportTarget>();
        List<ReportProblem> problems = new List<ReportProblem>();
        foreach (PaintResult result in results)
        {
            switch (result)
            {
                case PaintResult.Planned planned:
                    targets.Add(new ReportTarget(planned.ReferenceId.ToString(), null, planned.Name,
                        planned.Now.Name ?? (planned.Now.Index?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        planned.Color, Undo));
                    break;
                case PaintResult.Failed failed:
                    problems.Add(new ReportProblem(failed.Index, failed.Id, failed.Error));
                    break;
            }
        }

        return Report.Of(targets, null, problems);
    }
}
