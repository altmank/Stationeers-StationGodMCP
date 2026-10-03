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
/// What the server asks a client's StationGod to draw for its player: show_preview's wire boxes or highlight's marks.
/// Replace removes that kind's earlier drawings first (a call without keep); a replace with nothing to draw is clear.
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
}

/// <summary>One show_preview wire box: its corners, colour, seconds, a short name and whether it shows through walls.</summary>
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
/// A DrawCommand's bytes, protocol 1: protocol (1), kind (1: 1 previews, 2 highlights), replace (1), count (4), then
/// each box as min, max (12 each), colour (16), seconds (4), name (text), xray (1), or each mark as its kind (1: 1
/// point, 2 things), tint (16), pulse (1), seconds (4), label (text), then the point (12) or a count (4) and that many
/// reference ids (8 each). Counts above the caps are refused on both ends; the sender keeps within them.
/// </summary>
internal static class DrawWire
{
    internal const int MaximumBoxes = 2048;
    internal const int MaximumMarks = 64;

    /// <summary>The most thing ids in one command, all marks together: 128 KB of ids.</summary>
    internal const int MaximumIds = 16384;

    private const byte PreviewsKind = 1;
    private const byte HighlightsKind = 2;
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
