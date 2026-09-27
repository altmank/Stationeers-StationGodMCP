#nullable enable

namespace StationGodMCP.Pure;

/// <summary>What a swap does to the air and gravity a piece blocks.</summary>
internal enum AirChange
{
    /// <summary>Blocks exactly what the old piece blocked.</summary>
    Keeps,

    /// <summary>Blocks air or gravity the old piece let through (a frame finished, a leaky wall sealed).</summary>
    Seals,

    /// <summary>Would let through air or gravity the old piece blocked: refused.</summary>
    WouldOpen
}

/// <summary>
/// Never open: a swap may keep or close what the old piece blocks, never open it. Air is BuildState.BlockAir
/// (Structure.CanAirPass); rooms are walled by BlockGravity (Structure.CanGravityPass, RoomEvaluator's
/// IsGridBlockedByStructure), so a piece that stopped blocking gravity would merge or break rooms.
/// </summary>
internal static class AirRule
{
    internal static AirChange Judge(PieceBlocking before, PieceBlocking after)
    {
        if ((before.Air && !after.Air) || (before.Gravity && !after.Gravity))
        {
            return AirChange.WouldOpen;
        }

        return (!before.Air && after.Air) || (!before.Gravity && after.Gravity) ? AirChange.Seals : AirChange.Keeps;
    }
}

/// <summary>What one build state of a piece blocks (BuildState.BlockAir, BuildState.BlockGravity).</summary>
internal readonly struct PieceBlocking
{
    internal PieceBlocking(bool air, bool gravity)
    {
        Air = air;
        Gravity = gravity;
    }

    internal bool Air { get; }

    internal bool Gravity { get; }
}

/// <summary>How a face would bear its pressure difference with a new wall.</summary>
internal enum StressVerdict
{
    Ok,

    /// <summary>Above the new wall's MaxPressureDelta times Thing.StressedRatio: the game marks it stressed.</summary>
    Stressed,

    /// <summary>At or above the face's summed MaxPressureDelta with the new wall: the game would damage it.</summary>
    Overstressed
}

/// <summary>
/// The game's wall stress rule (Atmosphere.ReactWithStructures): per face, the absolute difference of the two cells'
/// PressureGassesAndLiquids against the summed MaxPressureDelta of the face's air-blocking structures with a positive
/// MaxPressureDelta; above the sum, each such structure whose own MaxPressureDelta is below the difference is
/// damaged; above MaxPressureDelta times StressedRatio it is marked stressed. A structure with MaxPressureDelta 0 or
/// less is never damaged or stressed. A face next to a cell whose centre structure blocks the grid (a frame) is
/// skipped by the game, so it is never stressed.
/// </summary>
internal static class WallStress
{
    internal static StressVerdict Judge(FaceLoad load, double targetMaxDeltaKpa, double stressedRatio)
    {
        if (load.Shielded || targetMaxDeltaKpa <= 0.0)
        {
            return StressVerdict.Ok;
        }

        double faceSum = load.OtherMaxDeltaKpa + targetMaxDeltaKpa;
        if (load.DifferenceKpa >= faceSum)
        {
            return StressVerdict.Overstressed;
        }

        return load.DifferenceKpa > targetMaxDeltaKpa * stressedRatio ? StressVerdict.Stressed : StressVerdict.Ok;
    }
}

/// <summary>
/// A face's load: the pressure difference across it now, the summed MaxPressureDelta of the other air-blocking
/// structures on it (the piece being replaced left out), and whether the game skips the face (a frame beside it).
/// </summary>
internal readonly struct FaceLoad
{
    internal FaceLoad(double differenceKpa, double otherMaxDeltaKpa, bool shielded)
    {
        DifferenceKpa = differenceKpa;
        OtherMaxDeltaKpa = otherMaxDeltaKpa;
        Shielded = shielded;
    }

    internal double DifferenceKpa { get; }

    internal double OtherMaxDeltaKpa { get; }

    internal bool Shielded { get; }
}
