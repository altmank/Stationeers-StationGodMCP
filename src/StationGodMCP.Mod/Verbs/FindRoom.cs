#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find room (catalogue find.json): every closed room measured cell by cell, largest first (ClosedRooms), with include
/// cells every cell centre and include devices the devices in each; one id is the room that thing is in. A list of ids
/// answers one row per id (Rows), each that thing's room or why it is in none. Read only.
/// </summary>
internal static class FindRoom
{
    private const string PlayerRoom = "local_player_room_id";

    internal static VerbOutcome Run(FindRoomOptions options)
    {
        RoomMeasure measure = ClosedRooms.Measuring(options.Fields.Includes("cells"), options.Fields.Includes("devices"));
        PerIdList? ids = options.Selector.Ids;
        if (ids is { AnswersRows: true })
        {
            JsonSerializer serializer = ApiJson.Fresh();
            Rows rows = new Rows();
            foreach (string id in ids.Ids)
            {
                rows.Add(id, () => JObject.FromObject(RoomOf(id, measure), serializer));
            }

            return new VerbOutcome.Answered(rows.Reply(ReplyFields.Of((PlayerRoom, measure.PlayerRoomId))));
        }

        List<RoomView> rooms = ids != null
            ? new List<RoomView> { RoomOf(ids.Ids[0], measure) }
            : ClosedRooms.All(measure);
        return new VerbOutcome.Answered(ReplyFields.Of(("rooms", rooms), (PlayerRoom, measure.PlayerRoomId)));
    }

    // The room the thing is in: refused thing_not_found or not_in_room.
    private static RoomView RoomOf(string id, RoomMeasure measure) =>
        ThingId.TryRead(new JValue(id), out ThingId thing)
            ? measure.Of(ClosedRooms.RequireRoom(thing, measure.Rooms))
            : throw ApiErrors.Refused(ApiErrors.ThingNotFoundCode, $"find room: {id} is no thing's id.");
}
