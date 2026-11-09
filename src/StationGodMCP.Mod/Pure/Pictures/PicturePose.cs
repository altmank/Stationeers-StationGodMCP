#nullable enable

using System;

namespace StationGodMCP.Pure.Pictures;

/// <summary>
/// Where show picture's camera stands (catalogue show.json, picture): on a sphere about the thing's centre, yaw degrees
/// around from the thing's level front toward its right (0 front, 90 right, 180 behind), pitch degrees above the level
/// (looking down), at distance metres, looking at the centre. Left out, the distance is the one at which the thing's
/// bounding sphere fills the narrower of the two fields of view with a 10% margin. Pure: Unity's left-handed axes (x
/// east, y up, z north; right = up x forward), no game types.
/// </summary>
internal sealed class PicturePose
{
    internal const double FitMargin = 1.1;

    /// <summary>The nearest a fitted camera stands, so a tiny thing is not pictured from inside the near plane.</summary>
    internal const double MinimumFitDistance = 0.3;

    private PicturePose(Vec3 position, Vec3 lookAt, double yaw, double pitch, double fov, double distance)
    {
        Position = position;
        LookAt = lookAt;
        Yaw = yaw;
        Pitch = pitch;
        Fov = fov;
        Distance = distance;
    }

    internal Vec3 Position { get; }

    internal Vec3 LookAt { get; }

    /// <summary>Yaw as used, in [0, 360).</summary>
    internal double Yaw { get; }

    internal double Pitch { get; }

    /// <summary>The vertical field of view in degrees.</summary>
    internal double Fov { get; }

    internal double Distance { get; }

    /// <summary>The unit direction from the camera to the centre.</summary>
    internal Vec3 Direction => (LookAt - Position).Normalized;

    /// <summary>
    /// The pose about centre, the thing's front being forward (levelled; straight up or down, or zero, is north). radius
    /// is the thing's bounding sphere in metres; aspect is width / height.
    /// </summary>
    internal static PicturePose Of(Vec3 centre, Vec3 forward, double radius, double yaw, double pitch, double? distance,
        double fov, double aspect)
    {
        Vec3 level = new Vec3(forward.X, 0, forward.Z).Normalized;
        if (level.Length < 0.5)
        {
            level = new Vec3(0, 0, 1);
        }

        Vec3 up = new Vec3(0, 1, 0);
        Vec3 right = up.Cross(level).Normalized;
        double yawUsed = Wrap(yaw);
        double yawRad = yawUsed * Math.PI / 180;
        double pitchRad = pitch * Math.PI / 180;
        Vec3 around = level * Math.Cos(yawRad) + right * Math.Sin(yawRad);
        Vec3 outward = around * Math.Cos(pitchRad) + up * Math.Sin(pitchRad);
        double used = distance ?? FitDistance(radius, fov, aspect);
        return new PicturePose(centre + outward * used, centre, yawUsed, pitch, fov, used);
    }

    /// <summary>
    /// The distance at which a sphere of radius fills the narrower field of view (vertical fov, or the horizontal one
    /// the aspect gives) with the margin, at least MinimumFitDistance.
    /// </summary>
    internal static double FitDistance(double radius, double fov, double aspect)
    {
        double half = fov * Math.PI / 360;
        double horizontalHalf = Math.Atan(Math.Tan(half) * aspect);
        double narrow = Math.Min(half, horizontalHalf);
        double fit = Math.Max(radius, 0) * FitMargin / Math.Sin(narrow);
        return Math.Max(fit, MinimumFitDistance);
    }

    /// <summary>Degrees into [0, 360).</summary>
    internal static double Wrap(double degrees)
    {
        double wrapped = degrees % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }
}
