#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using UnityEngine;

namespace StationGodMCP.Net;

/// <summary>
/// The server's side of drawing on a remote player's screen: sends the command to their client and keeps how long
/// each drawing it sent lasts, per connection and kind, so a later call can say how many it cleared.
/// </summary>
internal static class RemoteDrawings
{
    private static readonly Dictionary<(long Connection, bool Previews), List<float>> Showing =
        new Dictionary<(long, bool), List<float>>();

    /// <summary>Sends the command; the number of drawings of its kind it cleared, or null when it could not be sent.</summary>
    internal static int? Send(long connectionId, DrawCommand command)
    {
        if (!StationGodNet.SendDraw(connectionId, DrawWire.Encode(command)))
        {
            return null;
        }

        bool previews = command is DrawCommand.Previews;
        float now = RemoteViews.Now;
        if (!Showing.TryGetValue((connectionId, previews), out List<float>? untils))
        {
            untils = new List<float>();
            Showing[(connectionId, previews)] = untils;
        }

        untils.RemoveAll(until => until <= now);
        int cleared = command.Replace ? untils.Count : 0;
        if (command.Replace)
        {
            untils.Clear();
        }

        switch (command)
        {
            case DrawCommand.Previews boxes:
                foreach (PreviewBox box in boxes.Boxes)
                {
                    untils.Add(now + box.Seconds);
                }

                break;
            case DrawCommand.Highlights marks:
                foreach (MarkSpec mark in marks.Marks)
                {
                    untils.Add(now + mark.Seconds);
                }

                break;
        }

        return cleared;
    }

    internal static void Prune()
    {
        foreach ((long Connection, bool Previews) key in new List<(long, bool)>(Showing.Keys))
        {
            if (Client.Find(key.Connection) == null)
            {
                Showing.Remove(key);
            }
        }
    }

    internal static void Clear() => Showing.Clear();
}

/// <summary>
/// A client's side: draws what the server sent for this player with this game's own Previews and Highlights, as the
/// tools draw for a local player. Things are found by reference id, the same on every machine; one this game does not
/// have is left out.
/// </summary>
internal static class RemoteDrawing
{
    internal static void Receive(byte[] payload)
    {
        switch (DrawWire.Decode(payload))
        {
            case WireRead<DrawCommand>.Read read:
                Draw(read.Value);
                return;
            case WireRead<DrawCommand>.OtherProtocol other:
                OnceLog.Warning("draw_protocol", $"The server's StationGod draws with view protocol {other.Protocol}; " +
                                                 $"this game's StationGod {StationGodMod.Version} speaks " +
                                                 $"{ViewProtocol.Current}, so its drawings are not shown.");
                return;
            case WireRead<DrawCommand>.Malformed malformed:
                OnceLog.Warning("draw_malformed", $"A drawing from the server did not read ({malformed.Reason}).");
                return;
        }
    }

    private static void Draw(DrawCommand command)
    {
        switch (command)
        {
            case DrawCommand.Previews previews:
                if (previews.Replace)
                {
                    Previews.Clear();
                }

                foreach (PreviewBox box in previews.Boxes)
                {
                    if (!Previews.Box(box.Box, ColorOf(box.Color), box.Seconds, "StationGodPreview " + box.Name, box.XRay))
                    {
                        OnceLog.Warning("draw_lines", "This game has no shader to draw the server's preview lines with.");
                        return;
                    }
                }

                return;
            case DrawCommand.Highlights highlights:
                if (highlights.Replace)
                {
                    Highlights.Clear();
                }

                if (highlights.Marks.Count > 0 && XRay.MeshMaterial == null)
                {
                    OnceLog.Warning("draw_marks", "This game has no material to draw the server's highlights with.");
                    return;
                }

                foreach (MarkSpec mark in highlights.Marks)
                {
                    Highlights.Add(MarkOf(mark));
                }

                return;
        }
    }

    private static HighlightMark MarkOf(MarkSpec mark)
    {
        float until = Time.realtimeSinceStartup + mark.Seconds;
        Tint tint = new Tint(mark.Tint.Red, mark.Tint.Green, mark.Tint.Blue, mark.Tint.Alpha);
        switch (mark)
        {
            case MarkSpec.Point point:
                return new PointMark(new HighlightTarget.Point(point.At, tint, string.Empty, mark.Label, mark.Pulse),
                    until);
            case MarkSpec.Things things:
                List<Thing> found = new List<Thing>(things.Ids.Count);
                List<ThingId> ids = new List<ThingId>(things.Ids.Count);
                foreach (long id in things.Ids)
                {
                    ids.Add(new ThingId(id));
                    Thing thing = Thing.Find(id);
                    if (thing != null && !thing.IsBeingDestroyed)
                    {
                        found.Add(thing);
                    }
                }

                return new ThingsMark(new HighlightTarget.Things(ids, tint, string.Empty, mark.Label, mark.Pulse), until,
                    found);
            default:
                throw new System.InvalidOperationException($"No mark for {mark.GetType().Name}.");
        }
    }

    internal static Color ColorOf(Rgba color) => new Color(color.Red, color.Green, color.Blue, color.Alpha);
}
