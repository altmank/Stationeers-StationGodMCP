#nullable enable

namespace StationGodMCP.Pure.Pictures;

/// <summary>
/// Which way a pictured thing's front points (show picture's yaw 0). An entity's body turns on its own child transform
/// (Entity.CharacterRotationY): the game holds a player's ThingTransform at identity every frame
/// (MovementController.cs, Mode.Animation), so ThingTransform.forward is always north for a player. The body's forward
/// wins when the thing has one, else the thing's own forward, else north; the result is levelled, as PicturePose takes
/// it. Pure: the game hands in the vectors it read.
/// </summary>
internal static class PictureFront
{
    internal static readonly Vec3 North = new Vec3(0, 0, 1);

    /// <summary>The level unit front, from the entity body's forward (null for no body) and the thing's own (null for none).</summary>
    internal static Vec3 Of(Vec3? bodyForward, Vec3? thingForward)
    {
        Vec3 chosen = bodyForward ?? thingForward ?? North;
        Vec3 level = new Vec3(chosen.X, 0, chosen.Z).Normalized;
        return level.Length < 0.5 ? North : level;
    }
}
