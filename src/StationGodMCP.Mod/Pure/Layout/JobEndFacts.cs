#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure;

/// <summary>
/// What a held-tick job did, in a few lists: what a jobs-topic job_ended event carries. {job_id, tool, status, error
/// {code, message}, created_ids, removed, changed, placed}, each key present only when the job's final reply has it,
/// a list only when it holds something:
/// <list type="bullet">
/// <item>created_ids: every id the job built, new and changed pieces (a run's log.created_ids);</item>
/// <item>removed: every id it removed outright (a run's log.removed, a structure job's result.removed);</item>
/// <item>changed: a run's changed pieces {old_reference_id, new_reference_id, target_prefab_name};</item>
/// <item>placed: the new things {reference_id, prefab_name} (a run's log.placed, a structure job's result.placed).</item>
/// </list>
/// </summary>
internal static class JobEndFacts
{
    internal const string CreatedIdsKey = "created_ids";
    internal const string RemovedKey = "removed";
    internal const string ChangedKey = "changed";
    internal const string PlacedKey = "placed";

    /// <summary>The facts of a job's final reply, as the client sees it (its wire JSON).</summary>
    internal static JObject Of(JObject reply)
    {
        JObject facts = new JObject();
        CopyText(reply, facts, "job_id");
        CopyText(reply, facts, "tool");
        CopyText(reply, facts, "status");
        JToken? log = reply["log"];
        JToken? result = reply["result"];
        if ((reply["error"] ?? result?["error"]) is JObject error)
        {
            JObject brief = new JObject();
            CopyText(error, brief, "code");
            CopyText(error, brief, "message");
            facts["error"] = brief;
        }

        JArray created = new JArray();
        foreach (JToken id in Array(log?[CreatedIdsKey]))
        {
            AddId(created, id);
        }

        JArray removed = new JArray();
        foreach (JToken id in Array(log?[RemovedKey]))
        {
            AddId(removed, id);
        }

        foreach (JToken piece in Array(result?[RemovedKey]))
        {
            AddId(removed, piece["reference_id"]);
        }

        JArray changed = new JArray();
        foreach (JToken change in Array(log?[ChangedKey]))
        {
            changed.Add(Change(change));
        }

        JArray placed = new JArray();
        foreach (JToken thing in Array(log?[PlacedKey]))
        {
            placed.Add(Placed(thing));
        }

        foreach (JToken thing in Array(result?[PlacedKey]))
        {
            placed.Add(Placed(thing));
        }

        AddList(facts, CreatedIdsKey, created);
        AddList(facts, RemovedKey, removed);
        AddList(facts, ChangedKey, changed);
        AddList(facts, PlacedKey, placed);
        return facts;
    }

    private static JObject Change(JToken change)
    {
        JObject brief = new JObject();
        CopyId(change, brief, "old_reference_id");
        CopyId(change, brief, "new_reference_id");
        CopyText(change, brief, "target_prefab_name");
        return brief;
    }

    private static JObject Placed(JToken thing)
    {
        JObject brief = new JObject();
        CopyId(thing, brief, "reference_id");
        CopyText(thing, brief, "prefab_name");
        return brief;
    }

    private static void AddList(JObject facts, string key, JArray list)
    {
        if (list.Count > 0)
        {
            facts[key] = list;
        }
    }

    private static bool AddId(JArray into, JToken? token)
    {
        if (!ThingId.TryRead(token, out ThingId id))
        {
            return false;
        }

        into.Add(Text(id.Value));
        return true;
    }

    private static void CopyId(JToken from, JObject into, string key)
    {
        if (ThingId.TryRead(from[key], out ThingId id))
        {
            into[key] = Text(id.Value);
        }
    }

    private static void CopyText(JToken from, JObject into, string key)
    {
        if (from[key] is JValue { Type: JTokenType.String } text)
        {
            into[key] = text.Value<string>();
        }
    }

    private static string Text(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<JToken> Array(JToken? token) =>
        token is JArray array ? array : (IEnumerable<JToken>)System.Array.Empty<JToken>();
}
