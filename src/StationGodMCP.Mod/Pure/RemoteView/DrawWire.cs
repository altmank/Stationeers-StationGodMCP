#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.RemoteView;

/// <summary>A colour, red, green, blue and alpha from 0 to 1.</summary>
internal readonly struct Rgba
{
    internal Rgba(float red, float green, float blue, float alpha)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
    }

    internal float Red { get; }

    internal float Green { get; }

    internal float Blue { get; }

    internal float Alpha { get; }
}

/// <summary>
/// What the server asks a client's StationGod to draw for its player: show preview's wire boxes or ghosts, or show
/// highlight's marks. Replace removes that kind's earlier drawings first (a call without keep); a replace with nothing
/// to draw is clear.
/// </summary>
internal abstract class DrawCommand
{
    private DrawCommand(bool replace)
    {
        Replace = replace;
    }

    internal bool Replace { get; }

    internal sealed class Previews : DrawCommand
    {
        internal Previews(bool replace, IReadOnlyList<PreviewBox> boxes) : base(replace)
        {
            Boxes = boxes;
        }

        internal IReadOnlyList<PreviewBox> Boxes { get; }
    }

    internal sealed class Highlights : DrawCommand
    {
        internal Highlights(bool replace, IReadOnlyList<MarkSpec> marks) : base(replace)
        {
            Marks = marks;
        }

        internal IReadOnlyList<MarkSpec> Marks { get; }
    }

    internal sealed class Ghosts : DrawCommand
    {
        internal Ghosts(bool replace, IReadOnlyList<GhostSpec> ghosts) : base(replace)
        {
            List = ghosts;
        }

        internal IReadOnlyList<GhostSpec> List { get; }
    }

    /// <summary>
    /// The host's ghost board, kept in step on a client: a reset (the whole board, as one command or the first of
    /// several) empties the client's copy first; then the ghosts added and the ids taken off. Replace is the reset.
    /// </summary>
    internal sealed class Board : DrawCommand
    {
        internal Board(bool reset, IReadOnlyList<BoardGhost> added, IReadOnlyList<long> removed) : base(reset)
        {
            Added = added;
            Removed = removed;
        }

        internal IReadOnlyList<BoardGhost> Added { get; }

        internal IReadOnlyList<long> Removed { get; }
    }
}

/// <summary>Who put a ghost there: an agent's call (show preview), or a player in their game.</summary>
internal enum GhostSource : byte
{
    Agent = 1,
    Player = 2
}

/// <summary>
/// One ghost: a structure's meshes at a build state, drawn tinted where a placement or a run's piece would stand, for
/// its seconds; xray draws it through walls, frames and terrain too. The prefab goes by hash, the same on every
/// machine; the turn as Euler degrees (Quaternion.Euler). Source says who put it there, so a ghost a player places
/// travels and draws the same way as an agent's.
/// </summary>
internal sealed class GhostSpec
{
    internal GhostSpec(int prefabHash, int buildState, Vec3 position, Vec3 euler, Rgba tint, float seconds, bool xray,
        GhostSource source = GhostSource.Agent, string? label = null)
    {
        Label = label;
        PrefabHash = prefabHash;
        BuildState = buildState;
        Position = position;
        Euler = euler;
        Tint = tint;
        Seconds = seconds;
        XRay = xray;
        Source = source;
    }

    internal int PrefabHash { get; }

    /// <summary>The build state whose meshes are drawn; past the prefab's last, its last.</summary>
    internal int BuildState { get; }

    internal Vec3 Position { get; }

    internal Vec3 Euler { get; }

    internal Rgba Tint { get; }

    internal float Seconds { get; }

    internal bool XRay { get; }

    internal GhostSource Source { get; }

    /// <summary>What the ghost is, shown when the crosshair is on it; null for none.</summary>
    internal string? Label { get; }
}

/// <summary>One show preview wire box: its corners, colour, seconds, a short name and whether it shows through walls.</summary>
internal sealed class PreviewBox
{
    internal PreviewBox(Box3 box, Rgba color, float seconds, string name, bool xray)
    {
        Box = box;
        Color = color;
        Seconds = seconds;
        Name = name;
        XRay = xray;
    }

    internal Box3 Box { get; }

    internal Rgba Color { get; }

    internal float Seconds { get; }

    internal string Name { get; }

    internal bool XRay { get; }
}

/// <summary>One highlight mark: its tint, label and pulse and seconds, and a point or the things it draws.</summary>
internal abstract class MarkSpec
{
    private MarkSpec(Rgba tint, string? label, bool pulse, float seconds)
    {
        Tint = tint;
        Label = label;
        Pulse = pulse;
        Seconds = seconds;
    }

    internal Rgba Tint { get; }

    internal string? Label { get; }

    internal bool Pulse { get; }

    internal float Seconds { get; }

    internal sealed class Point : MarkSpec
    {
        internal Point(Vec3 at, Rgba tint, string? label, bool pulse, float seconds) : base(tint, label, pulse, seconds)
        {
            At = at;
        }

        internal Vec3 At { get; }
    }

    /// <summary>Things by reference id; the client finds each (Thing.Find), the same ids on every machine.</summary>
    internal sealed class Things : MarkSpec
    {
        internal Things(IReadOnlyList<long> ids, Rgba tint, string? label, bool pulse, float seconds)
            : base(tint, label, pulse, seconds)
        {
            Ids = ids;
        }

        internal IReadOnlyList<long> Ids { get; }
    }
}

/// <summary>
/// A DrawCommand's bytes, protocol 1 to 3: protocol (1), kind (1: 1 previews, 2 highlights, 3 ghosts from protocol 3),
/// replace (1), count (4), then each box as min, max (12 each), colour (16), seconds (4), name (text), xray (1), or
/// each mark as its kind (1: 1 point, 2 things), tint (16), pulse (1), seconds (4), label (text), then the point (12)
/// or a count (4) and that many reference ids (8 each), or each ghost as prefab hash (4), build state (4), position
/// (12), Euler degrees (12), tint (16), seconds (4), xray (1), source (1), label (text, protocol 4). Protocol 4 adds
/// kind 4, the board: reset
/// (1, in place of replace), the added count (4) and each board ghost as id (8), prefab hash (4), prefab name (text),
/// build state (4), position (12), Euler degrees (12), tint (16), source (1), owner (text), label (text), placed at (8,
/// milliseconds), then the removed count (4) and that many ids (8 each). Counts above the caps are refused on both
/// ends; the sender keeps within them.
/// </summary>
internal static class DrawWire
{
    internal const int MaximumBoxes = 2048;
    internal const int MaximumMarks = 64;

    /// <summary>The most ghosts in one command (56 bytes each without a label).</summary>
    internal const int MaximumGhosts = 2048;

    private const int GhostBytes = 56;

    /// <summary>The most board ghosts in one command; a larger board goes as a reset and further commands.</summary>
    internal const int MaximumBoardGhosts = 1024;

    /// <summary>The most ids one board command takes off.</summary>
    internal const int MaximumRemovedIds = GhostBoard.Maximum;

    /// <summary>The most thing ids in one command, all marks together: 128 KB of ids.</summary>
    internal const int MaximumIds = 16384;

    private const byte PreviewsKind = 1;
    private const byte HighlightsKind = 2;
    private const byte GhostsKind = 3;
    private const byte BoardKind = 4;
    private const byte PointMark = 1;
    private const byte ThingsMark = 2;

    internal static byte[] Encode(DrawCommand command)
    {
        WireWriter writer = new WireWriter();
        writer.Byte(ViewProtocol.Current);
        switch (command)
        {
            case DrawCommand.Previews previews:
                writer.Byte(PreviewsKind);
                writer.Bool(previews.Replace);
                writer.Int32(previews.Boxes.Count);
                foreach (PreviewBox box in previews.Boxes)
                {
                    writer.Vector(box.Box.Min);
                    writer.Vector(box.Box.Max);
                    Write(writer, box.Color);
                    writer.Single(box.Seconds);
                    writer.Text(box.Name);
                    writer.Bool(box.XRay);
                }

                break;
            case DrawCommand.Highlights highlights:
                writer.Byte(HighlightsKind);
                writer.Bool(highlights.Replace);
                writer.Int32(highlights.Marks.Count);
                foreach (MarkSpec mark in highlights.Marks)
                {
                    Write(writer, mark);
                }

                break;
            case DrawCommand.Board board:
                writer.Byte(BoardKind);
                writer.Bool(board.Replace);
                writer.Int32(board.Added.Count);
                foreach (BoardGhost ghost in board.Added)
                {
                    Write(writer, ghost);
                }

                writer.Int32(board.Removed.Count);
                foreach (long id in board.Removed)
                {
                    writer.Int64(id);
                }

                break;
            case DrawCommand.Ghosts ghosts:
                writer.Byte(GhostsKind);
                writer.Bool(ghosts.Replace);
                writer.Int32(ghosts.List.Count);
                foreach (GhostSpec ghost in ghosts.List)
                {
                    Write(writer, ghost);
                }

                break;
            default:
                throw new System.InvalidOperationException($"No wire form for {command.GetType().Name}.");
        }

        return writer.ToArray();
    }

    internal static WireRead<DrawCommand> Decode(byte[] bytes)
    {
        WireReader reader = new WireReader(bytes);
        byte protocol = reader.Byte();
        if (reader.Failed)
        {
            return new WireRead<DrawCommand>.Malformed("empty");
        }

        if (protocol != ViewProtocol.Current)
        {
            return new WireRead<DrawCommand>.OtherProtocol(protocol);
        }

        byte kind = reader.Byte();
        bool replace = reader.Bool();
        int count = reader.Int32();
        DrawCommand? command = kind switch
        {
            PreviewsKind when count >= 0 && count <= MaximumBoxes => new DrawCommand.Previews(replace, Boxes(reader, count)),
            HighlightsKind when count >= 0 && count <= MaximumMarks => Marks(reader, count, replace),
            GhostsKind when count >= 0 && count <= MaximumGhosts => new DrawCommand.Ghosts(replace, Ghosts(reader, count)),
            BoardKind when count >= 0 && count <= MaximumBoardGhosts => Board(reader, count, replace),
            _ => null
        };
        if (command == null)
        {
            return new WireRead<DrawCommand>.Malformed($"kind {kind} with {count} entries");
        }

        return reader.Complete
            ? new WireRead<DrawCommand>.Read(command)
            : new WireRead<DrawCommand>.Malformed(reader.Failed ? "the payload ends early or a count is past its cap"
                : $"{reader.Remaining} bytes past its end");
    }

    private static List<PreviewBox> Boxes(WireReader reader, int count)
    {
        List<PreviewBox> boxes = new List<PreviewBox>(count);
        for (int index = 0; index < count && !reader.Failed; index++)
        {
            Box3 box = new Box3(reader.Vector(), reader.Vector());
            boxes.Add(new PreviewBox(box, Color(reader), reader.Single(), reader.Text() ?? string.Empty, reader.Bool()));
        }

        return boxes;
    }

    private static DrawCommand.Highlights Marks(WireReader reader, int count, bool replace)
    {
        List<MarkSpec> marks = new List<MarkSpec>(count);
        int ids = 0;
        for (int index = 0; index < count && !reader.Failed; index++)
        {
            byte kind = reader.Byte();
            Rgba tint = Color(reader);
            bool pulse = reader.Bool();
            float seconds = reader.Single();
            string? label = reader.Text();
            switch (kind)
            {
                case PointMark:
                    marks.Add(new MarkSpec.Point(reader.Vector(), tint, label, pulse, seconds));
                    break;
                case ThingsMark:
                    int things = reader.Int32();
                    if (things < 0 || ids + things > MaximumIds)
                    {
                        reader.Fail();
                        break;
                    }

                    ids += things;
                    List<long> list = new List<long>(System.Math.Min(things, reader.Remaining / 8));
                    for (int thing = 0; thing < things && !reader.Failed; thing++)
                    {
                        list.Add(reader.Int64());
                    }

                    marks.Add(new MarkSpec.Things(list, tint, label, pulse, seconds));
                    break;
                default:
                    reader.Fail();
                    break;
            }
        }

        return new DrawCommand.Highlights(replace, marks);
    }

    private static List<GhostSpec> Ghosts(WireReader reader, int count)
    {
        List<GhostSpec> ghosts = new List<GhostSpec>(System.Math.Min(count, reader.Remaining / GhostBytes));
        for (int index = 0; index < count && !reader.Failed; index++)
        {
            int hash = reader.Int32();
            int state = reader.Int32();
            Vec3 position = reader.Vector();
            Vec3 euler = reader.Vector();
            Rgba tint = Color(reader);
            float seconds = reader.Single();
            bool xray = reader.Bool();
            byte source = reader.Byte();
            string? label = reader.Text();
            if (source != (byte)GhostSource.Agent && source != (byte)GhostSource.Player)
            {
                reader.Fail();
                break;
            }

            ghosts.Add(new GhostSpec(hash, state, position, euler, tint, seconds, xray, (GhostSource)source, label));
        }

        return ghosts;
    }

    private static DrawCommand.Board Board(WireReader reader, int count, bool reset)
    {
        List<BoardGhost> added = new List<BoardGhost>(System.Math.Min(count, reader.Remaining / 64));
        for (int index = 0; index < count && !reader.Failed; index++)
        {
            long id = reader.Int64();
            int hash = reader.Int32();
            string name = reader.Text() ?? string.Empty;
            int state = reader.Int32();
            Vec3 position = reader.Vector();
            Vec3 euler = reader.Vector();
            Rgba tint = Color(reader);
            byte source = reader.Byte();
            string owner = reader.Text() ?? string.Empty;
            string? label = reader.Text();
            long placedMs = reader.Int64();
            if (source != (byte)GhostSource.Agent && source != (byte)GhostSource.Player)
            {
                reader.Fail();
                break;
            }

            added.Add(new BoardGhost(id, hash, name, state, position, euler, tint, (GhostSource)source, owner, label,
                placedMs / 1000.0));
        }

        int removedCount = reader.Int32();
        List<long> removed = new List<long>();
        if (removedCount < 0 || removedCount > MaximumRemovedIds)
        {
            reader.Fail();
        }
        else
        {
            for (int index = 0; index < removedCount && !reader.Failed; index++)
            {
                removed.Add(reader.Int64());
            }
        }

        return new DrawCommand.Board(reset, added, removed);
    }

    private static void Write(WireWriter writer, BoardGhost ghost)
    {
        writer.Int64(ghost.Id);
        writer.Int32(ghost.PrefabHash);
        writer.Text(ghost.PrefabName);
        writer.Int32(ghost.BuildState);
        writer.Vector(ghost.Position);
        writer.Vector(ghost.Euler);
        Write(writer, ghost.Tint);
        writer.Byte((byte)ghost.Source);
        writer.Text(ghost.Owner);
        writer.Text(ghost.Label);
        writer.Int64((long)System.Math.Round(ghost.PlacedAt * 1000.0));
    }

    private static void Write(WireWriter writer, GhostSpec ghost)
    {
        writer.Int32(ghost.PrefabHash);
        writer.Int32(ghost.BuildState);
        writer.Vector(ghost.Position);
        writer.Vector(ghost.Euler);
        Write(writer, ghost.Tint);
        writer.Single(ghost.Seconds);
        writer.Bool(ghost.XRay);
        writer.Byte((byte)ghost.Source);
        writer.Text(ghost.Label);
    }

    private static void Write(WireWriter writer, MarkSpec mark)
    {
        writer.Byte(mark is MarkSpec.Point ? PointMark : ThingsMark);
        Write(writer, mark.Tint);
        writer.Bool(mark.Pulse);
        writer.Single(mark.Seconds);
        writer.Text(mark.Label);
        switch (mark)
        {
            case MarkSpec.Point point:
                writer.Vector(point.At);
                break;
            case MarkSpec.Things things:
                writer.Int32(things.Ids.Count);
                foreach (long id in things.Ids)
                {
                    writer.Int64(id);
                }

                break;
            default:
                throw new System.InvalidOperationException($"No wire form for {mark.GetType().Name}.");
        }
    }

    private static void Write(WireWriter writer, Rgba color)
    {
        writer.Single(color.Red);
        writer.Single(color.Green);
        writer.Single(color.Blue);
        writer.Single(color.Alpha);
    }

    private static Rgba Color(WireReader reader) => new Rgba(reader.Single(), reader.Single(), reader.Single(), reader.Single());
}
