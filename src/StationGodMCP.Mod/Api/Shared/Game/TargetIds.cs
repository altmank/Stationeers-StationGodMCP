#nullable enable

using System;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A verb's id as the catalogue's id option takes it (catalogue/verbs/_shared.json, id): a thing's reference id as a
/// decimal string, or "looking_at", the thing under the local player's crosshair (PlayerView.Target). Refused
/// thing_not_found, worded with the call's label, when the text names no id or nothing is under the crosshair.
/// </summary>
internal static class TargetIds
{
    internal const string LookingAt = "looking_at";

    /// <summary>The reference id the text names.</summary>
    internal static ThingId Of(string id, string label)
    {
        if (string.Equals(id.Trim(), LookingAt, StringComparison.OrdinalIgnoreCase))
        {
            Thing? thing = PlayerView.Current().Require(label).Target;
            return thing != null
                ? new ThingId(thing.ReferenceId)
                : throw ApiErrors.Refused("thing_not_found", $"{label}: nothing is under the crosshair for \"looking_at\".");
        }

        return ThingId.TryRead(new JValue(id), out ThingId read)
            ? read
            : throw ApiErrors.Refused("thing_not_found",
                $"{label}: '{id}' is not a thing's id (a reference id, or \"looking_at\").");
    }

    /// <summary>The thing the text names, refused thing_not_found when none exists.</summary>
    internal static Thing Thing(string id, string label) => GameLookup.RequireThing(Of(id, label));
}
