#nullable enable

using Assets.Scripts.Objects;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>Finds things by id through the game's reference table (Thing.Find), and names them in replies.</summary>
internal static class GameLookup
{
    internal static bool TryFindThing(ThingId id, out Thing thing)
    {
        thing = Thing.Find(id.Value);
        return thing != null;
    }

    internal static Thing RequireThing(ThingId id)
    {
        if (!TryFindThing(id, out Thing thing))
        {
            throw ApiErrors.ThingNotFound(id);
        }

        return thing;
    }

    internal static ThingView ViewOf(Thing thing) =>
        new ThingView(new ThingId(thing.ReferenceId), thing.PrefabName, thing.DisplayName);

    internal static PositionView ViewOf(Vector3 position) => new PositionView(position.x, position.y, position.z);
}
