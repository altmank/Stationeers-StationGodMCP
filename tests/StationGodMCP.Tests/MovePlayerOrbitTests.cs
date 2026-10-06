#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// move_player in the low-orbit area (1.28.2): safe_ground stands the player on the highest structure in the column,
/// not on the planet's terrain 2 km below, and a point or thing up there is a destination like any other.
/// </summary>
public sealed class MovePlayerOrbitTests
{
    // Beside the orbital launch mount StructureLaunchMountOrbital at (425, 2213, 1641).
    private static readonly Vec3 NearMount = new Vec3(425, 2230, 1641);

    [Fact]
    public void SafeGroundStandsThePlayerOnTheHighestStructure()
    {
        Vec3 landed = OrbitLanding.Safe(NearMount,
            new[] { new OrbitSurface(2213.0, true), new OrbitSurface(2216.5, true) });

        Assert.Equal(new Vec3(425, 2216.5 + OrbitLanding.ClearanceM, 1641), landed);
    }

    [Fact]
    public void AnItemFloatingInTheColumnIsNotGround()
    {
        Vec3 landed = OrbitLanding.Safe(NearMount,
            new[] { new OrbitSurface(2213.0, true), new OrbitSurface(2240.0, false) });

        Assert.Equal(2213.0 + OrbitLanding.ClearanceM, landed.Y);
    }

    [Fact]
    public void WithNothingBuiltThereThePointIsKept()
    {
        // No gravity up there: nothing to fall to, so the point is safe as it is.
        Assert.Equal(NearMount, OrbitLanding.Safe(NearMount, new OrbitSurface[0]));
        Assert.Equal(NearMount, OrbitLanding.Safe(NearMount, new[] { new OrbitSurface(2240.0, false) }));
    }

    [Fact]
    public void APointInLowOrbitIsAValidDestination()
    {
        MoveRequest request =
            MoveRequest.Of(new Args(JObject.Parse("""{"at": [425, 2213, 1641], "safe_ground": true}""")));

        MoveDestination.Point point = Assert.IsType<MoveDestination.Point>(request.Destination);
        Assert.Equal(new Vec3(425, 2213, 1641), point.At);
        Assert.True(request.SafeGround);
    }

    [Fact]
    public void BesideAMountLandsLevelWithItsBaseOnTheSideFacingThePlayer()
    {
        // The mount's body in orbit, the player on the ground far below and to the west.
        Box3 mount = new Box3(new Vec3(420, 2210, 1636), new Vec3(430, 2216, 1646));

        Vec3 landed = Landing.Beside(mount, new Vec3(0, 50, 1641));

        Assert.Equal(new Vec3(420 - Landing.ClearanceM, 2210, 1641), landed);
    }
}
