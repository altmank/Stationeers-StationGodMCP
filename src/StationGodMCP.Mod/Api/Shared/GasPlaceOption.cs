#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// move gas's source or to as given, before anything is looked up. A closed set: an atmosphere by its owner's id (a
/// thing's id or "looking_at", a pipe network's or an atmosphere's id), the planet ("planet"), gas made out of nothing
/// ("create", a source only), a room by the id find room reports ({"room": "..."}), or the room a thing is in
/// ({"room_of": "<id>"}).
/// </summary>
internal abstract class GasPlaceOption
{
    private const string PlanetWord = "planet";
    private const string CreateWord = "create";
    private const string RoomKey = "room";
    private const string RoomOfKey = "room_of";

    private GasPlaceOption()
    {
    }

    internal sealed class Atmosphere : GasPlaceOption
    {
        internal Atmosphere(string id)
        {
            Id = id;
        }

        /// <summary>A reference id or "looking_at", as given.</summary>
        internal string Id { get; }
    }

    internal sealed class Planet : GasPlaceOption
    {
        internal static readonly Planet Instance = new Planet();

        private Planet()
        {
        }
    }

    /// <summary>Gas made out of nothing (a cheat for creative worlds): a source only.</summary>
    internal sealed class Created : GasPlaceOption
    {
        internal static readonly Created Instance = new Created();

        private Created()
        {
        }
    }

    internal sealed class Room : GasPlaceOption
    {
        internal Room(ThingId roomId)
        {
            RoomId = roomId;
        }

        internal ThingId RoomId { get; }
    }

    internal sealed class RoomOf : GasPlaceOption
    {
        internal RoomOf(ThingId thingId)
        {
            ThingId = thingId;
        }

        internal ThingId ThingId { get; }
    }

    /// <summary>A room's air is never emptied by omission, so a room as the source needs the gases named.</summary>
    internal bool IsRoom => this is Room or RoomOf;

    /// <summary>The place the option names; null with the problem added when it names none.</summary>
    internal static GasPlaceOption? Of(JToken token, string option, List<OptionProblem> problems)
    {
        GasPlaceOption? place = token switch
        {
            JObject room => OfRoom(room),
            JValue { Type: JTokenType.String } value when ((string)value!).Trim().Length > 0 =>
                string.Equals(((string)value!).Trim(), PlanetWord, StringComparison.OrdinalIgnoreCase)
                    ? Planet.Instance
                    : string.Equals(((string)value!).Trim(), CreateWord, StringComparison.OrdinalIgnoreCase)
                        ? Created.Instance
                        : new Atmosphere(((string)value!).Trim()),
            _ => null
        };
        if (place == null)
        {
            problems.Add(new OptionProblem(option,
                "is a thing's id, \"looking_at\", \"planet\", \"create\", {\"room\": \"<id>\"} (a room as find room " +
                "gives it) or " +
                "{\"room_of\": \"<id>\"} (the room that thing is in)"));
        }

        return place;
    }

    private static GasPlaceOption? OfRoom(JObject room)
    {
        if (room.Count != 1)
        {
            return null;
        }

        JProperty only = (JProperty)room.First!;
        if (!ThingId.TryRead(only.Value, out ThingId id))
        {
            return null;
        }

        return only.Name switch
        {
            RoomKey => new Room(id),
            RoomOfKey => new RoomOf(id),
            _ => null
        };
    }
}
