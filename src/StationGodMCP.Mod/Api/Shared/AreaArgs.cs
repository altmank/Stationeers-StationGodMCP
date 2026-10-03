#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// The area arguments a list filters on: min and max (the corners of a box, metres) or near ([x, y, z] or {x, y, z})
/// with radius_m. Neither: anywhere. Both forms, a corner without the other, near without radius_m or the reverse are
/// invalid_argument.
/// </summary>
internal static class AreaArgs
{
    internal const double MaximumRadiusM = 1000.0;

    internal static readonly string[] Names = { "min", "max", "near", "radius_m" };

    internal static PointArea Parse(Args args)
    {
        bool box = args.Has("min") || args.Has("max");
        bool near = args.Has("near") || args.Has("radius_m");
        if (box && near)
        {
            throw ApiErrors.InvalidArgument("Give min and max, or near with radius_m, not both.");
        }

        if (box)
        {
            return PointArea.Box(Point(args, "min"), Point(args, "max"));
        }

        if (!near)
        {
            return PointArea.Anywhere;
        }

        double radius = args.OptionalDouble("radius_m") ??
                        throw ApiErrors.InvalidArgument("near needs radius_m, in metres.");
        if (!(radius > 0.0) || radius > MaximumRadiusM)
        {
            throw ApiErrors.InvalidArgument($"Argument 'radius_m' must be above 0 and at most {MaximumRadiusM:0} m.");
        }

        return PointArea.Near(Point(args, "near"), radius);
    }

    private static Vec3 Point(Args args, string name)
    {
        JToken token = args.Optional(name) ??
                       throw ApiErrors.InvalidArgument(name == "near"
                           ? "radius_m needs near: [x, y, z] metres."
                           : "Give both min and max, the corners of the box.");
        Metres point = BuildArgs.PositionOf(token, name);
        return new Vec3(point.X, point.Y, point.Z);
    }
}
