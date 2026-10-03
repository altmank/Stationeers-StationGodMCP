#nullable enable

namespace StationGodMCP.Pure.RemoteView;

/// <summary>
/// A ViewReport's bytes, protocol 1: protocol (1 byte), session, sequence (4 each), eye, forward, up (12 each), flags
/// (1: third person 1, seated 2, hit 4, target 8), player position (12), then with a target its reference id (8) and
/// interactable index (4, -1 for none), and with a hit its point, normal (12 each), distance (4) and thing id (8, 0 for
/// terrain). 58 bytes with neither, 70 with a target, 94 with a hit, 106 with both.
/// </summary>
internal static class ViewWire
{
    /// <summary>How far the client casts the ray; the tools' reaches are all within it.</summary>
    internal const double ReachM = 50.0;

    internal const int BaseLength = 58;
    internal const int TargetLength = 12;
    internal const int HitLength = 36;

    private const byte ThirdPersonFlag = 1;
    private const byte SeatedFlag = 2;
    private const byte HitFlag = 4;
    private const byte TargetFlag = 8;
    private const byte KnownFlags = ThirdPersonFlag | SeatedFlag | HitFlag | TargetFlag;
    private const int NoInteractable = -1;

    internal static byte[] Encode(ViewReport report)
    {
        WireWriter writer = new WireWriter();
        writer.Byte(ViewProtocol.Current);
        writer.Int32(report.Session);
        writer.Int32(report.Sequence);
        writer.Vector(report.Eye);
        writer.Vector(report.Forward);
        writer.Vector(report.Up);
        writer.Byte(FlagsOf(report));
        writer.Vector(report.PlayerPosition);
        if (report.Target is ViewTarget target)
        {
            writer.Int64(target.ThingId);
            writer.Int32(target.InteractableId ?? NoInteractable);
        }

        if (report.Hit is ViewHit hit)
        {
            writer.Vector(hit.Point);
            writer.Vector(hit.Normal);
            writer.Single((float)hit.DistanceM);
            writer.Int64(hit.ThingId);
        }

        return writer.ToArray();
    }

    internal static WireRead<ViewReport> Decode(byte[] bytes)
    {
        WireReader reader = new WireReader(bytes);
        byte protocol = reader.Byte();
        if (reader.Failed)
        {
            return new WireRead<ViewReport>.Malformed("empty");
        }

        if (protocol != ViewProtocol.Current)
        {
            return new WireRead<ViewReport>.OtherProtocol(protocol);
        }

        int session = reader.Int32();
        int sequence = reader.Int32();
        Vec3 eye = reader.Vector();
        Vec3 forward = reader.Vector();
        Vec3 up = reader.Vector();
        byte flags = reader.Byte();
        Vec3 player = reader.Vector();
        ViewTarget? target = (flags & TargetFlag) != 0 ? TargetOf(reader) : null;
        ViewHit? hit = (flags & HitFlag) != 0
            ? new ViewHit(reader.Vector(), reader.Vector(), reader.Single(), reader.Int64())
            : null;
        if ((flags & ~KnownFlags) != 0)
        {
            return new WireRead<ViewReport>.Malformed($"unknown flags {flags}");
        }

        if (!reader.Complete)
        {
            return new WireRead<ViewReport>.Malformed(
                reader.Failed ? "the payload ends early" : $"{reader.Remaining} bytes past its end");
        }

        return new WireRead<ViewReport>.Read(new ViewReport(session, sequence, eye, forward, up,
            (flags & ThirdPersonFlag) != 0, (flags & SeatedFlag) != 0, player, target, hit));
    }

    private static ViewTarget TargetOf(WireReader reader)
    {
        long thing = reader.Int64();
        int interactable = reader.Int32();
        return new ViewTarget(thing, interactable == NoInteractable ? (int?)null : interactable);
    }

    private static byte FlagsOf(ViewReport report) =>
        (byte)((report.ThirdPerson ? ThirdPersonFlag : 0) | (report.Seated ? SeatedFlag : 0) |
               (report.Hit != null ? HitFlag : 0) | (report.Target != null ? TargetFlag : 0));
}
