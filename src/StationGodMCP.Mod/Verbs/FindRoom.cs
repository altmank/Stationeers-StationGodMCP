#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Layout;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Verbs;

/// <summary>
/// find room (catalogue find.json): every closed room measured cell by cell, largest first (ClosedRooms), with include
/// cells every cell centre and include devices the devices in each; one id is the room that thing is in;
/// {rooms, count, total, offset, has_more, local_player_room_id} (ListReply: the rooms whole, so offset 0, no limit
/// and has_more false). A list of ids answers one row per id (Rows), each that thing's room or why it is in none. Read
/// only.
/// </summary>
[ReplyFact(FactRule.Followed, "the rooms", Keeper = typeof(Assets.Scripts.GridSystem.Room))]
[ReplyFact(FactRule.Live, "each room's air and devices")]
internal static class FindRoom
{
    private const string PlayerRoom = "local_player_room_id";

    internal static VerbOutcome Run(FindRoomOptions options)
    {
        bool cells = options.Fields.Includes("cells");
        bool devices = options.Fields.Includes("devices");
        PerIdList? ids = options.Selector.Ids;
        if (options.Selector.Radius != null && options.Selector.Near == null)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(FindRoomOptions.Spec.Label,
                new[] { new OptionProblem("radius", "goes with near") }));
        }

        Vec3? near = options.Selector.Near is { } point
            ? NearCentres.Of(point, FindRoomOptions.Spec.Label)
            : (Vec3?)null;
        double radius = options.Selector.Radius ?? 0.0;
        return WorkResult<Found>.Of(answer => Finding(cells, devices, ids, answer),
            found => Answered(near is { } at ? Near(found, at, radius) : found, ids));
    }

    // near: only the rooms whose bounds come within radius of the point, nearest first.
    private static Found Near(Found found, Vec3 point, double radius)
    {
        if (found.Rooms == null)
        {
            return found;
        }

        List<(double Distance, RoomView Room)> kept = new List<(double, RoomView)>();
        foreach (RoomView room in found.Rooms)
        {
            Vec3 min = new Vec3(room.Bounds.Min.X, room.Bounds.Min.Y, room.Bounds.Min.Z);
            Vec3 max = new Vec3(room.Bounds.Max.X, room.Bounds.Max.Y, room.Bounds.Max.Z);
            if (RoomNear.Keeps(min, max, point, radius))
            {
                kept.Add((RoomNear.Distance(min, max, point), room));
            }
        }

        kept.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        return new Found(found.Measure, kept.ConvertAll(static entry => entry.Room));
    }

    // The measuring (its devices over steps), then, without ids, every room (a room a step).
    private static IEnumerable<object?> Finding(bool cells, bool devices, PerIdList? ids, Answer<Found> answer)
    {
        Answer<RoomMeasure> measure = new Answer<RoomMeasure>();
        foreach (object? step in ClosedRooms.Measuring(cells, devices, measure))
        {
            yield return step;
        }

        List<RoomView>? rooms = null;
        if (ids == null)
        {
            Answer<List<RoomView>> all = new Answer<List<RoomView>>();
            foreach (object? step in ClosedRooms.All(measure.Value, all))
            {
                yield return step;
            }

            rooms = all.Value;
        }

        answer.Value = new Found(measure.Value, rooms);
    }

    private static VerbOutcome Answered(Found found, PerIdList? ids)
    {
        RoomMeasure measure = found.Measure;
        if (ids is { AnswersRows: true })
        {
            JsonSerializer serializer = ApiJson.Serializer;
            Rows rows = new Rows();
            foreach (string id in ids.Ids)
            {
                rows.Add(id, () => RoomOf(id, measure));
            }

            return new VerbOutcome.Answered(rows.Reply(ReplyFields.Of((PlayerRoom, measure.PlayerRoomId))));
        }

        List<RoomView> rooms = found.Rooms ?? new List<RoomView> { RoomOf(ids!.Ids[0], measure) };
        return new VerbOutcome.Answered(RoomsReply(rooms, measure));
    }

    // The measuring and, without ids, every room.
    private sealed class Found
    {
        internal Found(RoomMeasure measure, List<RoomView>? rooms)
        {
            Measure = measure;
            Rooms = rooms;
        }

        internal RoomMeasure Measure { get; }

        internal List<RoomView>? Rooms { get; }
    }

    // The rooms, whole, as one page.
    private static ReplyObject RoomsReply(List<RoomView> rooms, RoomMeasure measure)
    {
        JsonSerializer serializer = ApiJson.Serializer;
        return ListReply.Of("rooms", rooms, Paging.All, room => room,
            ReplyFields.Of((PlayerRoom, measure.PlayerRoomId)));
    }

    // The room the thing is in: refused thing_not_found or not_in_room.
    private static RoomView RoomOf(string id, RoomMeasure measure) =>
        ThingId.TryRead(new JValue(id), out ThingId thing)
            ? measure.Of(ClosedRooms.RequireRoom(thing, measure.Rooms))
            : throw ApiErrors.Refused(ApiErrors.ThingNotFoundCode, $"find room: {id} is no thing's id.");
}
