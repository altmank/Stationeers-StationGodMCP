#nullable enable

namespace StationGodMCP.Pure.RemoteView;

/// <summary>
/// The server's order to a player's game to move that player: their human's reference id and the point their feet go
/// to. The player's own game moves them, because a client's character is its own to move: the server's copy follows
/// what the client sends.
/// </summary>
internal sealed class MoveCommand
{
    internal MoveCommand(long humanId, Vec3 to)
    {
        HumanId = humanId;
        To = to;
    }

    internal long HumanId { get; }

    internal Vec3 To { get; }
}

/// <summary>A MoveCommand's bytes: protocol (1 byte), human reference id (8), the point (12). 21 bytes.</summary>
internal static class MoveWire
{
    internal const int Length = 21;

    internal static byte[] Encode(MoveCommand command)
    {
        WireWriter writer = new WireWriter();
        writer.Byte(ViewProtocol.Current);
        writer.Int64(command.HumanId);
        writer.Vector(command.To);
        return writer.ToArray();
    }

    internal static WireRead<MoveCommand> Decode(byte[] bytes)
    {
        WireReader reader = new WireReader(bytes);
        byte protocol = reader.Byte();
        if (reader.Failed)
        {
            return new WireRead<MoveCommand>.Malformed("empty");
        }

        if (protocol != ViewProtocol.Current)
        {
            return new WireRead<MoveCommand>.OtherProtocol(protocol);
        }

        long human = reader.Int64();
        Vec3 to = reader.Vector();
        if (!reader.Complete)
        {
            return new WireRead<MoveCommand>.Malformed($"{bytes.Length} bytes, not {Length}");
        }

        return IsFinite(to)
            ? new WireRead<MoveCommand>.Read(new MoveCommand(human, to))
            : new WireRead<MoveCommand>.Malformed("the point is not finite");
    }

    private static bool IsFinite(Vec3 point) =>
        !double.IsNaN(point.X) && !double.IsInfinity(point.X) && !double.IsNaN(point.Y) &&
        !double.IsInfinity(point.Y) && !double.IsNaN(point.Z) && !double.IsInfinity(point.Z);
}
