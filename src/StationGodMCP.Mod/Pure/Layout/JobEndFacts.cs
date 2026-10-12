#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Pure;

/// <summary>
/// What a held-tick job did, in a few lists: the facts undo_job plans from, and what a jobs-topic job_ended event
/// carries. {job_id, tool, status, error {code, message}, created_ids, removed, changed, placed}, each key present only
/// when the job's final reply has it, a list only when it holds something:
/// <list type="bullet">
/// <item>created_ids: every id the job built, new and changed pieces (a run's log.created_ids);</item>
/// <item>removed: every id it removed outright (a run's log.removed, a structure job's result.removed);</item>
/// <item>changed: a run's changed pieces {old_reference_id, new_reference_id, target_prefab_name};</item>
/// <item>placed: the new things {reference_id, prefab_name} (a run's log.placed, a structure job's result.placed).</item>
/// </list>
/// The same lists are what undo_job takes in its request when the job is past the finished jobs the mod keeps.
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

    /// <summary>
    /// undo_job's created_ids, removed, changed and placed, read into facts: null with no problem when none is given;
    /// null with the problem (an invalid_argument) when one is not the shape a job's final reply gives it. removed takes
    /// ids, or things with a reference_id (a structure job's result.removed).
    /// </summary>
    internal static JObject? FromRequest(JToken? createdIds, JToken? removed, JToken? changed, JToken? placed,
        out string? problem)
    {
        problem = null;
        if (Absent(createdIds) && Absent(removed) && Absent(changed) && Absent(placed))
        {
            return null;
        }

        JObject facts = new JObject();
        JArray created = new JArray();
        foreach (JToken id in Listed(createdIds, CreatedIdsKey, ref problem))
        {
            Require(AddId(created, id), CreatedIdsKey, "a reference id", ref problem);
        }

        JArray gone = new JArray();
        foreach (JToken entry in Listed(removed, RemovedKey, ref problem))
        {
            Require(AddId(gone, entry is JObject thing ? thing["reference_id"] : entry), RemovedKey,
                "a reference id or {reference_id}", ref problem);
        }

        JArray swaps = new JArray();
        foreach (JToken entry in Listed(changed, ChangedKey, ref problem))
        {
            bool valid = entry is JObject change && IsId(change["old_reference_id"]) &&
                         IsId(change["new_reference_id"]) && IsTextOrAbsent(change["target_prefab_name"]);
            Require(valid, ChangedKey, "{old_reference_id, new_reference_id, target_prefab_name}", ref problem);
            if (valid)
            {
                swaps.Add(Change(entry));
            }
        }

        JArray things = new JArray();
        foreach (JToken entry in Listed(placed, PlacedKey, ref problem))
        {
            bool valid = entry is JObject thing && IsId(thing["reference_id"]) && IsTextOrAbsent(thing["prefab_name"]);
            Require(valid, PlacedKey, "{reference_id, prefab_name}", ref problem);
            if (valid)
            {
                things.Add(Placed(entry));
            }
        }

        if (problem != null)
        {
            return null;
        }

        AddList(facts, CreatedIdsKey, created);
        AddList(facts, RemovedKey, gone);
        AddList(facts, ChangedKey, swaps);
        AddList(facts, PlacedKey, things);
        return facts;
    }

    /// <summary>
    /// Everything the job built, with the prefab it built when the facts name it: created_ids (the prefab from placed or
    /// changed), then each placed thing created_ids does not hold (a structure job's).
    /// </summary>
    internal static List<(long Id, string? Prefab)> Created(JObject facts)
    {
        Dictionary<long, string?> prefabs = new Dictionary<long, string?>();
        foreach (JToken placed in Array(facts[PlacedKey]))
        {
            prefabs[Id(placed["reference_id"])] = placed.Value<string>("prefab_name");
        }

        foreach (JToken changed in Array(facts[ChangedKey]))
        {
            prefabs[Id(changed["new_reference_id"])] = changed.Value<string>("target_prefab_name");
        }

        List<(long, string?)> created = new List<(long, string?)>();
        HashSet<long> listed = new HashSet<long>();
        foreach (JToken id in Array(facts[CreatedIdsKey]))
        {
            long value = Id(id);
            listed.Add(value);
            created.Add((value, prefabs.TryGetValue(value, out string? prefab) ? prefab : null));
        }

        foreach (JToken placed in Array(facts[PlacedKey]))
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
        foreach (JToken id in Array(facts[RemovedKey]))
        {
            removed.Add(Id(id));
        }

        foreach (JToken changed in Array(facts[ChangedKey]))
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

    private static IEnumerable<JToken> Listed(JToken? token, string name, ref string? problem)
    {
        if (Absent(token))
        {
            return System.Array.Empty<JToken>();
        }

        if (token is JArray array)
        {
            return array;
        }

        problem ??= $"Argument '{Vocabulary.Arg(name)}' must be a list, as the job's final reply gives it.";
        return System.Array.Empty<JToken>();
    }

    private static void Require(bool ok, string name, string shape, ref string? problem)
    {
        if (!ok)
        {
            problem ??= $"Every entry of '{Vocabulary.Arg(name)}' must be {shape}, as the job's final reply gives it.";
        }
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

    private static bool IsId(JToken? token) => ThingId.TryRead(token, out _);

    private static bool IsTextOrAbsent(JToken? token) => Absent(token) || token!.Type == JTokenType.String;

    private static bool Absent(JToken? token) => token == null || token.Type == JTokenType.Null;

    private static string Text(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<JToken> Array(JToken? token) =>
        token is JArray array ? array : (IEnumerable<JToken>)System.Array.Empty<JToken>();

    private static long Id(JToken? token) => ThingId.TryRead(token, out ThingId id) ? id.Value : 0L;
}
