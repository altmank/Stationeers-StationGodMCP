#nullable enable

using System;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A verb's id as the catalogue's id option takes it (catalogue/verbs/_shared.json, id): a thing's reference id as a
/// decimal string, or "looking_at", the thing under the player's crosshair (PlayerSight.Target: as_player's, or the
/// rule's). Refused thing_not_found, worded with the call's label, when the text names no id or nothing is under the
/// crosshair.
/// </summary>
internal static class TargetIds
{
    internal const string LookingAt = "looking_at";

    /// <summary>The reference id the text names.</summary>
    internal static ThingId Of(string id, string label) =>
        TryOf(id, label, out ThingId read) is { } refused ? throw refused : read;

    /// <summary>
    /// The reference id the text names, or the refusal (built, not thrown: a list of ids answers it as one row's
    /// error); null when read holds the id.
    /// </summary>
    internal static ApiException? TryOf(string id, string label, out ThingId read)
    {
        if (string.Equals(id.Trim(), LookingAt, StringComparison.OrdinalIgnoreCase))
        {
            Thing? thing = PlayerSight.Current().Require(label).Target;
            read = thing != null ? new ThingId(thing.ReferenceId) : default;
            return thing != null
                ? null
                : ApiErrors.Refused("thing_not_found", $"{label}: nothing is under the crosshair for \"looking_at\".");
        }

        return ThingId.TryRead(new JValue(id), out read)
            ? null
            : ApiErrors.Refused("thing_not_found",
                $"{label}: '{id}' is not a thing's id (a reference id, or \"looking_at\").");
    }

    /// <summary>The thing the text names, refused thing_not_found when none exists.</summary>
    internal static Thing Thing(string id, string label) => GameLookup.RequireThing(Of(id, label));
}
