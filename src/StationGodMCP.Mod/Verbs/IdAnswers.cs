#nullable enable

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// A what's answer for the ids a call gives (PLAN 2.0, the per-id list form; SPEC-NOTES 14, D13): one id answers the
/// what's single shape, its refusal the call's; a list answers one row per id, {index, ok, reference_id, payload |
/// error} (Rows), reference_id the id the row read (a "looking_at" row the thing under the crosshair), each failing
/// alone.
/// </summary>
internal static class IdAnswers
{
    /// <summary>Ids that name things (TargetIds: a reference id or "looking_at"), each read by read.</summary>
    internal static VerbOutcome Things(PerIdList ids, string label, Func<ThingId, object> read, JObject? top = null) =>
        Of(ids, id =>
        {
            ThingId thing = TargetIds.Of(id, label);
            return (thing.ToString(), read(thing));
        }, top);

    /// <summary>Ids read as given: read answers the row's reference id and its view.</summary>
    internal static VerbOutcome Of(PerIdList ids, Func<string, (string ReferenceId, object View)> read,
        JObject? top = null)
    {
        JsonSerializer serializer = ApiJson.Fresh();
        if (!ids.AnswersRows)
        {
            return new VerbOutcome.Answered(Payload(read(ids.Ids[0]).View, serializer));
        }

        Rows rows = new Rows();
        foreach (string id in ids.Ids)
        {
            (string ReferenceId, object View) found;
            try
            {
                found = read(id);
            }
            catch (ApiException refused)
            {
                rows.Failed(id, new VerbError(refused.Code, refused.Message,
                    refused.Details == null ? null : JToken.FromObject(refused.Details, serializer)));
                continue;
            }

            rows.Succeeded(found.ReferenceId, Payload(found.View, serializer));
        }

        return new VerbOutcome.Answered(rows.Reply(top));
    }

    /// <summary>A view as a reply object, in the reply views' names.</summary>
    internal static JObject Payload(object view, JsonSerializer serializer) =>
        view as JObject ?? JObject.FromObject(view, serializer);
}
