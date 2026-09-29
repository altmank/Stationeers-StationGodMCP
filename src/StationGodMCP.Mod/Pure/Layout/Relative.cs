#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>Three world directions a relative position is measured along: right, up and forward.</summary>
internal sealed class Frame3
{
    internal Frame3(Vec3 right, Vec3 up, Vec3 forward, string name)
    {
        Right = right;
        Up = up;
        Forward = forward;
        Name = name;
    }

    internal Vec3 Right { get; }

    internal Vec3 Up { get; }

    internal Vec3 Forward { get; }

    internal string Name { get; }

    /// <summary>World axes: right +x, up +y, forward +z.</summary>
    internal static Frame3 World { get; } =
        new Frame3(new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1), "world");

    /// <summary>A frame on world axes from a turn's right, up and forward.</summary>
    internal static Frame3 Of(GridStep right, GridStep up, GridStep forward, string name) =>
        new Frame3(Vec3.Of(right), Vec3.Of(up), Vec3.Of(forward), name);

    /// <summary>The point that many metres right, up and forward of an origin.</summary>
    internal Vec3 Offset(Vec3 origin, double right, double up, double forward) =>
        origin + Right * right + Up * up + Forward * forward;
}

/// <summary>Where on a thing's body a relative position starts: its origin or the middle of one side of its box.</summary>
internal enum BodyAnchor
{
    Origin,
    Top,
    Bottom,
    Left,
    Right,
    Front,
    Back
}

/// <summary>Reading anchors and the player's heading as axes.</summary>
internal static class RelativeMath
{
    internal static BodyAnchor? AnchorOf(string? word) => (word ?? "origin").Trim().ToLowerInvariant() switch
    {
        "origin" => BodyAnchor.Origin,
        "top" => BodyAnchor.Top,
        "bottom" => BodyAnchor.Bottom,
        "left" => BodyAnchor.Left,
        "right" => BodyAnchor.Right,
        "front" => BodyAnchor.Front,
        "back" => BodyAnchor.Back,
        _ => null
    };

    /// <summary>
    /// The middle of the box's side the anchor names, sides read along the frame (top is the side furthest along its
    /// up); the origin for Origin.
    /// </summary>
    internal static Vec3 Anchor(Vec3 origin, Box3 box, Frame3 frame, BodyAnchor anchor)
    {
        Vec3 direction = anchor switch
        {
            BodyAnchor.Top => frame.Up,
            BodyAnchor.Bottom => frame.Up * -1,
            BodyAnchor.Right => frame.Right,
            BodyAnchor.Left => frame.Right * -1,
            BodyAnchor.Front => frame.Forward,
            BodyAnchor.Back => frame.Forward * -1,
            _ => Vec3.Zero
        };
        if (direction.Length < 1e-9)
        {
            return origin;
        }

        Vec3 centre = box.Centre;
        double reach = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            reach += Math.Abs(direction[axis]) * (box.Max[axis] - box.Min[axis]) / 2.0;
        }

        return centre + direction * reach;
    }

    /// <summary>
    /// The world axis nearest a level direction (its vertical part dropped), or null with the reason when it lies
    /// within ViewBasis.AmbiguousDegrees of a diagonal or is vertical.
    /// </summary>
    internal static GridStep? LevelAxis(Vec3 direction, out string? ambiguous)
    {
        Vec3 level = new Vec3(direction.X, 0, direction.Z);
        if (level.Length < 1e-6)
        {
            ambiguous = "the direction is straight up or down";
            return null;
        }

        double yaw = Math.Atan2(level.X, level.Z) * 180.0 / Math.PI;
        yaw = yaw < 0 ? yaw + 360.0 : yaw;
        if (Math.Abs(yaw % 90.0 - 45.0) < ViewBasis.AmbiguousDegrees)
        {
            ambiguous = $"the direction (heading {yaw:0} degrees) is near a diagonal between two axes";
            return null;
        }

        ambiguous = null;
        return ViewBasis.Nearest(level);
    }

    /// <summary>
    /// On a face plane seen from the front, the in-plane right and up a viewer means: up is +y on a wall, the viewer's
    /// level forward on a floor or ceiling; right is up cross the face's outward normal, so it runs to the viewer's
    /// right when the viewer faces the face.
    /// </summary>
    internal static (GridStep Right, GridStep Up) FaceAxes(GridStep outward, GridStep viewerForward)
    {
        GridStep up = outward.IsVertical ? viewerForward : GridStep.All[2];
        Vec3 into = Vec3.Of(outward.Opposite);
        Vec3 right = Vec3.Of(up).Cross(into);
        return (ViewBasis.Nearest(right), up);
    }
}
