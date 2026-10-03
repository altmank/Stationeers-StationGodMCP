#nullable enable

using System;

namespace StationGodMCP.Pure.RemoteView;

/// <summary>
/// When a client sends its view: at once when it changed in a way a tool would read differently (another thing or
/// interactable under the cursor, the ray on another thing, small cell or face, a turn of more than a degree, the eye
/// moved more than 5 cm, the camera mode changed), at most every 0.1 s, and every second regardless, so the server can
/// tell an idle player's view is still current.
/// </summary>
internal static class ViewChangeRule
{
    internal const double MinimumIntervalS = 0.1;
    internal const double HeartbeatS = 1.0;
    internal const double TurnDegrees = 1.0;
    internal const double EyeMoveM = 0.05;

    /// <summary>Whether a view is worth taking at all yet (a ray cast costs a frame something; none before the cap).</summary>
    internal static bool MayTake(double sinceSentS) => sinceSentS >= MinimumIntervalS;

    /// <summary>Whether to send current, given the last view sent (null: none yet) and the seconds since.</summary>
    internal static bool ShouldSend(ViewReport? last, double sinceSentS, ViewReport current)
    {
        if (last == null)
        {
            return true;
        }

        if (sinceSentS < MinimumIntervalS)
        {
            return false;
        }

        return sinceSentS >= HeartbeatS || Changed(last, current);
    }

    internal static bool Changed(ViewReport last, ViewReport current) =>
        last.ThirdPerson != current.ThirdPerson || last.Seated != current.Seated ||
        !Nullable.Equals(TargetOf(last), TargetOf(current)) ||
        !Nullable.Equals(HitOf(last), HitOf(current)) ||
        DegreesBetween(last.Forward, current.Forward) > TurnDegrees ||
        (current.Eye - last.Eye).Length > EyeMoveM;

    private static (long, int?)? TargetOf(ViewReport report) =>
        report.Target is ViewTarget target ? (target.ThingId, target.InteractableId) : null;

    // What a tool reads off the hit: the thing, the small cell it lands in and the face it is on.
    private static (long, GridCell, GridStep)? HitOf(ViewReport report) =>
        report.Hit is ViewHit hit ? (hit.ThingId, SmallCellOf(hit.Point), ViewBasis.Nearest(hit.Normal)) : null;

    /// <summary>The small cell (decimetres, multiples of 5) nearest a point in metres.</summary>
    internal static GridCell SmallCellOf(Vec3 point) =>
        new GridCell(Half(point.X), Half(point.Y), Half(point.Z));

    private static int Half(double metres) => (int)Math.Round(metres * 2.0) * 5;

    private static double DegreesBetween(Vec3 a, Vec3 b)
    {
        double cos = a.Normalized.Dot(b.Normalized);
        return Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))) * 180.0 / Math.PI;
    }
}
