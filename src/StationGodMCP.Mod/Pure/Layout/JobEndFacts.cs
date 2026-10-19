#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Pure;

/// <summary>
/// What a held-tick job did, in a few lists: what a jobs-topic job_ended event carries, and the facts undo plans
/// from. {job_id, tool, status, error {code, message}, created_ids, removed, changed, placed}, each key present only
/// when the job's final reply has it, a list only when it holds something:
/// <list type="bullet">
/// <item>created_ids: every id the job built, new and changed pieces (a run's log.created_ids, a swap or upgrade job's
/// swapped new ids);</item>
/// <item>removed: every id it removed outright (a run's log.removed, a structure job's result.removed, a swap or
/// upgrade job's removed);</item>
/// <item>changed: a run's changed pieces and a swap or upgrade job's swapped ones {old_reference_id, new_reference_id,
/// target_prefab_name};</item>
/// <item>placed: the new things {reference_id, prefab_name} (a run's log.placed, a structure job's result.placed).</item>
/// </list>
/// </summary>
internal static class JobEndFacts
{
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
        foreach (JToken id in Array(log?["created_ids"]))
        {
            AddId(created, id);
        }

        foreach (JToken swap in Array(reply["swapped"]))
        {
            AddId(created, swap["new_reference_id"]);
        }

        JArray removed = new JArray();
        foreach (JToken id in Array(log?["removed"]))
        {
            AddId(removed, id);
        }

        foreach (JToken piece in Array(result?["removed"]))
        {
            AddId(removed, piece["reference_id"]);
        }

        foreach (JToken id in Array(reply["removed"]))
        {
            AddId(removed, id);
        }

        JArray changed = new JArray();
        foreach (JToken change in Array(log?["changed"]))
        {
            changed.Add(Change(change));
        }

        foreach (JToken swap in Array(reply["swapped"]))
        {
            changed.Add(Change(swap));
        }

        JArray placed = new JArray();
        foreach (JToken thing in Array(log?["placed"]))
        {
            placed.Add(Placed(thing));
        }

        foreach (JToken thing in Array(result?["placed"]))
        {
            placed.Add(Placed(thing));
        }

        AddList(facts, "created_ids", created);
        AddList(facts, "removed", removed);
        AddList(facts, "changed", changed);
        AddList(facts, "placed", placed);
        return facts;
    }

    /// <summary>
    /// Everything the job built, with the prefab it built when the facts name it: created_ids (the prefab from placed or
    /// changed), then each placed thing created_ids does not hold (a structure job's).
    /// </summary>
    internal static List<(long Id, string? Prefab)> Created(JObject facts)
    {
        Dictionary<long, string?> prefabs = new Dictionary<long, string?>();
        foreach (JToken placed in Array(facts["placed"]))
        {
            prefabs[Id(placed["reference_id"])] = placed.Value<string>("prefab_name");
        }

        foreach (JToken changed in Array(facts["changed"]))
        {
            prefabs[Id(changed["new_reference_id"])] = changed.Value<string>("target_prefab_name");
        }

        List<(long, string?)> created = new List<(long, string?)>();
        HashSet<long> listed = new HashSet<long>();
        foreach (JToken id in Array(facts["created_ids"]))
        {
            long value = Id(id);
            listed.Add(value);
            created.Add((value, prefabs.TryGetValue(value, out string? prefab) ? prefab : null));
        }

        foreach (JToken placed in Array(facts["placed"]))
        {
            long value = Id(placed["reference_id"]);
            if (!listed.Contains(value))
            {
                created.Add((value, placed.Value<string>("prefab_name")));
            }
        }

        return created;
    }

    /// <summary>Everything the job removed: removed, and each changed piece's old one.</summary>
    internal static List<long> Removed(JObject facts)
    {
        List<long> removed = new List<long>();
        foreach (JToken id in Array(facts["removed"]))
        {
            removed.Add(Id(id));
        }

        foreach (JToken changed in Array(facts["changed"]))
        {
            removed.Add(Id(changed["old_reference_id"]));
        }

        return removed;
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

    private static long Id(JToken? token) => ThingId.TryRead(token, out ThingId id) ? id.Value : 0L;
}
