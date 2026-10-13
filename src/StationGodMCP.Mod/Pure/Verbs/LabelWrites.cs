#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>One rename a call asks for: its place in the call, the thing's id as given, and the name.</summary>
internal sealed record LabelOrder(int Index, string Id, string Label);

/// <summary>
/// write label's renames (catalogue write.json, label): one id gets label; a list names each '&lt;label&gt; 1',
/// '&lt;label&gt; 2' in the order given (an empty label gives each its game name back, unnumbered); labels are
/// {id, label} each, in order. Every problem with the options is listed at once.
/// </summary>
internal sealed record LabelOrders(IReadOnlyList<LabelOrder> Orders, bool AnswersRows)
{
    /// <summary>The renames the options ask for; null with every problem added.</summary>
    internal static LabelOrders? Of(PerIdList? ids, string? label, JToken? labels, List<OptionProblem> problems)
    {
        if (labels != null)
        {
            if (ids != null)
            {
                problems.Add(new OptionProblem("labels", "goes without id: each entry names its own thing"));
            }

            if (label != null)
            {
                problems.Add(new OptionProblem("label", "goes with id; each labels entry names its own"));
            }

            List<LabelOrder> listed = Entries(labels, problems);
            return problems.Count == 0 ? new LabelOrders(listed, true) : null;
        }

        if (ids == null)
        {
            problems.Add(new OptionProblem("id", "or labels is needed: the thing to rename, or {id, label} entries"));
            return null;
        }

        if (label == null)
        {
            problems.Add(new OptionProblem("label", "is needed with id: the new name; empty gives back the game's name"));
            return null;
        }

        if (problems.Count > 0)
        {
            return null;
        }

        bool numbered = ids.GivenAsList && label.Length > 0;
        List<LabelOrder> orders = new List<LabelOrder>(ids.Ids.Count);
        foreach (string id in ids.Ids)
        {
            string name = numbered
                ? label + " " + (orders.Count + 1).ToString(CultureInfo.InvariantCulture)
                : label;
            orders.Add(new LabelOrder(orders.Count, id, name));
        }

        return new LabelOrders(orders, ids.AnswersRows);
    }

    private static List<LabelOrder> Entries(JToken labels, List<OptionProblem> problems)
    {
        List<LabelOrder> orders = new List<LabelOrder>();
        if (labels is not JArray entries)
        {
            problems.Add(new OptionProblem("labels", "must be a list of {id, label}"));
            return orders;
        }

        for (int index = 0; index < entries.Count; index++)
        {
            string path = $"labels[{index}]";
            if (entries[index] is not JObject entry)
            {
                problems.Add(new OptionProblem(path, "must be {id, label}"));
                continue;
            }

            string? id = entry["id"] is { Type: JTokenType.String } given ? ((string)given!).Trim() : null;
            string? label = entry["label"] is { Type: JTokenType.String } named ? (string)named! : null;
            if (string.IsNullOrEmpty(id))
            {
                problems.Add(new OptionProblem(path + ".id", "is needed: a thing's id"));
            }

            if (label == null)
            {
                problems.Add(new OptionProblem(path + ".label", "is needed: the new name, or empty for the game's name"));
            }

            foreach (JProperty key in entry.Properties())
            {
                if (key.Name is not ("id" or "label"))
                {
                    problems.Add(new OptionProblem(path + "." + key.Name, "is not taken: an entry is {id, label}"));
                }
            }

            if (!string.IsNullOrEmpty(id) && label != null)
            {
                orders.Add(new LabelOrder(index, id!, label));
            }
        }

        return orders;
    }
}

/// <summary>A rename as the game made it: the thing, the name as written, whether a client sent it, the names around it.</summary>
internal sealed record Relabel(ThingId ReferenceId, string? PrefabName, string Written, bool SentToHost,
    LabelStateView Previous, LabelStateView Current);

/// <summary>
/// One rename's result, in the call's order: checked for a report (Planned: the thing's name now and the name the
/// Labeller would write), renamed (Renamed), or refused by a check of the game (Failed); each fails alone.
/// </summary>
internal abstract record LabelResult
{
    private LabelResult()
    {
    }

    internal sealed record Planned(ThingId ReferenceId, string? Name, string? Now, string Written) : LabelResult;

    internal sealed record Renamed(Relabel Rename) : LabelResult;

    internal sealed record Failed(int Index, string Id, VerbError Error) : LabelResult;
}

/// <summary>
/// write label's replies (catalogue write.json, label): one rename {reference_id, prefab_name, written, sent_to_host,
/// previous, current}, previous and current {display_name, custom_name}; several, one row each, or its error. A
/// report lists each rename as a target, [id, name, the name now -> the name written], [undo].
/// </summary>
internal static class LabelReplies
{
    private const string Undo = "[undo]";

    internal static VerbOutcome Acted(IReadOnlyList<LabelResult> results, bool answersRows)
    {
        JsonSerializer serializer = ApiJson.Fresh();
        if (!answersRows && results.Count == 1)
        {
            switch (results[0])
            {
                case LabelResult.Failed failed:
                    return new VerbOutcome.Refused(failed.Error);
                case LabelResult.Renamed renamed:
                    JObject one = new JObject { ["reference_id"] = renamed.Rename.ReferenceId.ToString() };
                    foreach (JProperty key in Payload(renamed.Rename, serializer).Properties())
                    {
                        one[key.Name] = key.Value;
                    }

                    return new VerbOutcome.Answered(one);
            }
        }

        Rows rows = new Rows();
        foreach (LabelResult result in results)
        {
            switch (result)
            {
                case LabelResult.Renamed renamed:
                    rows.Succeeded(renamed.Rename.ReferenceId.ToString(), Payload(renamed.Rename, serializer));
                    break;
                case LabelResult.Failed failed:
                    rows.Failed(failed.Id, failed.Error);
                    break;
            }
        }

        return new VerbOutcome.Answered(rows.Reply());
    }

    internal static JObject Reported(IReadOnlyList<LabelResult> results)
    {
        List<ReportTarget> targets = new List<ReportTarget>();
        List<ReportProblem> problems = new List<ReportProblem>();
        foreach (LabelResult result in results)
        {
            switch (result)
            {
                case LabelResult.Planned planned:
                    targets.Add(new ReportTarget(planned.ReferenceId.ToString(), null, planned.Name, planned.Now,
                        planned.Written, Undo));
                    break;
                case LabelResult.Failed failed:
                    problems.Add(new ReportProblem(failed.Index, failed.Id, failed.Error));
                    break;
            }
        }

        return Report.Of(targets, null, problems);
    }

    private static JObject Payload(Relabel rename, JsonSerializer serializer) =>
        new JObject
        {
            ["prefab_name"] = rename.PrefabName,
            ["written"] = rename.Written,
            ["sent_to_host"] = rename.SentToHost,
            ["previous"] = JToken.FromObject(rename.Previous, serializer),
            ["current"] = JToken.FromObject(rename.Current, serializer)
        };
}
