#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// The per-id list form (PLAN 2.0; SPEC-NOTES 14, D13): the ids a call names in id, one or a list. A list answers one
/// row per id ({index, ok, reference_id, payload | error}), unless the what's id_list names the list option the ids
/// fill instead (move item's moves, read sample's reads: one entry per id). One id answers the what's single shape.
/// </summary>
internal sealed record PerIdList(IReadOnlyList<string> Ids, bool GivenAsList, string? FillsList)
{
    /// <summary>The id option's kind: absent (null) when the what takes no id or the call gives none.</summary>
    internal static PerIdList? Bind(OptionReader options)
    {
        JToken? given = options.Raw("id");
        if (given == null)
        {
            return null;
        }

        List<string> ids = new List<string>();
        bool list = given is JArray;
        foreach (JToken entry in list ? (IEnumerable<JToken>)given : new[] { given })
        {
            string? id = entry.Type == JTokenType.String ? ((string)entry!).Trim() : null;
            if (string.IsNullOrEmpty(id))
            {
                options.Problem(list ? $"id[{ids.Count}]" : "id", "an id is a thing's id, a non-empty string");
                continue;
            }

            ids.Add(id!);
        }

        return new PerIdList(ids, list, list ? options.What.IdList : null);
    }

    /// <summary>The call answers one row per id: it gave a list, and no id_list folds it into another option.</summary>
    internal bool AnswersRows => GivenAsList && FillsList == null;

    /// <summary>The entries an id_list option takes from the ids: {id} each, in order; empty without id_list.</summary>
    internal JArray Entries()
    {
        JArray entries = new JArray();
        if (FillsList != null)
        {
            foreach (string id in Ids)
            {
                entries.Add(new JObject { ["id"] = id });
            }
        }

        return entries;
    }
}
