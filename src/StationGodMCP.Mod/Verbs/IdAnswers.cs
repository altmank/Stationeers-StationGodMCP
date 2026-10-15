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
    /// <summary>
    /// Ids that name things (TargetIds: a reference id or "looking_at"), each read by read; read answers the id's view,
    /// or a refusal (an ApiException built, not thrown) that becomes the id's row error.
    /// </summary>
    internal static VerbOutcome Things(PerIdList ids, string label, Func<ThingId, object> read, object? top = null) =>
        Of(ids, id => TargetIds.TryOf(id, label, out ThingId thing) is { } refused
            ? (id, refused)
            : (thing.ToString(), read(thing)), top);

    /// <summary>
    /// Ids read as given: read answers the row's reference id and its view, or a refusal as the view (an ApiException
    /// built, not thrown). A refusal read throws still answers its row; one id's refusal is the call's.
    /// </summary>
    internal static VerbOutcome Of(PerIdList ids, Func<string, (string ReferenceId, object View)> read,
        object? top = null)
    {
        if (!ids.AnswersRows)
        {
            object view = read(ids.Ids[0]).View;
            return view is ApiException one ? new VerbOutcome.Refused(VerbError.Of(one)) : new VerbOutcome.Answered(view);
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
                rows.Failed(id, VerbError.Of(refused));
                continue;
            }

            if (found.View is ApiException answered)
            {
                rows.Failed(id, VerbError.Of(answered));
            }
            else
            {
                rows.Succeeded(found.ReferenceId, found.View);
            }
        }

        return new VerbOutcome.Answered(rows.Reply(top));
    }

    /// <summary>The id's thing, or thing_not_found (built, not thrown) when none exists.</summary>
    internal static object Thing(ThingId id, Func<Assets.Scripts.Objects.Thing, object> read) =>
        GameLookup.TryFindThing(id, out Assets.Scripts.Objects.Thing thing) ? read(thing) : ApiErrors.ThingNotFound(id);

    /// <summary>The id's device in scope, or the refusal Devices.Require would throw (built, not thrown).</summary>
    internal static object Device(DeviceScope scope, ThingId id, Func<ScopedTarget, object> read) =>
        Devices.Find(scope, id.Value) is { } device ? read(device) : Devices.NotFound(scope, id);
}
