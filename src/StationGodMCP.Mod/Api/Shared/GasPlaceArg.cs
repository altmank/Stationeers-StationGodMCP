#nullable enable

using System;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// move_gas's from or to as given, before anything is looked up. A closed set: an atmosphere by reference id, the
/// planet ("planet"), a room by the room_id the rooms tool reports ({"room_id": "..."}), or the room a thing is in
/// ({"room_of": "<reference id>"}).
/// </summary>
internal abstract class GasPlaceArg
{
    private const string PlanetWord = "planet";

    private GasPlaceArg()
    {
    }

    internal sealed class Atmosphere : GasPlaceArg
    {
        internal Atmosphere(ThingId id)
        {
            Id = id;
        }

        internal ThingId Id { get; }
    }

    internal sealed class Planet : GasPlaceArg
    {
        internal static readonly Planet Instance = new Planet();

        private Planet()
        {
        }
    }

    internal sealed class Room : GasPlaceArg
    {
        internal Room(ThingId roomId)
        {
            RoomId = roomId;
        }

        internal ThingId RoomId { get; }
    }

    internal sealed class RoomOf : GasPlaceArg
    {
        internal RoomOf(ThingId thingId)
        {
            ThingId = thingId;
        }

        internal ThingId ThingId { get; }
    }

    /// <summary>The argument, or null when it is absent.</summary>
    internal static GasPlaceArg? Optional(Args args, string name)
    {
        JToken? token = args.Optional(name);
        return token switch
        {
            null => null,
            JObject room => ParseRoom(room, name),
            JValue value when value.Type == JTokenType.String &&
                              string.Equals(value.Value<string>()?.Trim(), PlanetWord,
                                  StringComparison.OrdinalIgnoreCase) => Planet.Instance,
            _ when ThingId.TryRead(token, out ThingId id) => new Atmosphere(id),
            _ => throw Invalid(name),
        };
    }

    internal static GasPlaceArg Required(Args args, string name) =>
        Optional(args, name) ?? throw ApiErrors.InvalidArgument($"Argument '{name}' is required.");

    private static GasPlaceArg ParseRoom(JObject room, string name)
    {
        Args inner = new Args(room);
        foreach (JProperty property in room.Properties())
        {
            if (property.Name != "room_id" && property.Name != "room_of")
            {
                throw Invalid(name);
            }
        }

        bool byId = inner.Has("room_id");
        if (byId == inner.Has("room_of"))
        {
            throw Invalid(name);
        }

        return byId
            ? new Room(ReadId(inner, "room_id", name))
            : new RoomOf(ReadId(inner, "room_of", name));
    }

    private static ThingId ReadId(Args inner, string key, string name) =>
        ThingId.TryRead(inner.Optional(key), out ThingId id) ? id : throw Invalid(name);

    private static ApiException Invalid(string name) =>
        ApiErrors.InvalidArgument(
            $"Argument '{name}' must be a reference id as a decimal string, \"planet\", {{\"room_id\": \"<id>\"}} " +
            "(a room as the rooms tool reports it) or {\"room_of\": \"<reference id>\"} (the room that thing is in).");
}
