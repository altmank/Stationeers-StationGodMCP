#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// 1.7.0 controls_blocked (LU 2026-09-30: warn when the face with slots, buttons or screens faces a wall panel or a
/// frame): the side a prefab's controls face, voted from its interactables' centres against its mesh box.
/// </summary>
public sealed class ControlFaceTests
{
    private static readonly Box3 Body = new Box3(new Vec3(-0.5, 0, -0.5), new Vec3(0.5, 1, 0.5));

    [Fact]
    public void MostControlsOnOneSideDecideIt()
    {
        ControlFace face = ControlFaceRule.Of(new List<Vec3>
        {
            new Vec3(0.1, 0.5, 0.45), new Vec3(-0.2, 0.7, 0.4), new Vec3(0.45, 0.5, 0.0)
        }, Body);

        Assert.Equal("+z", face.Local.Name);
        Assert.Equal(2, face.Votes);
        Assert.Equal(3, face.Considered);
        Assert.False(face.Fallback);
        Assert.StartsWith("CODE", face.Source);
    }

    [Fact]
    public void ControlsNearTheMiddleFallBackToForward()
    {
        ControlFace face = ControlFaceRule.Of(new List<Vec3> { new Vec3(0.1, 0.5, 0.1) }, Body);

        Assert.True(face.Fallback);
        Assert.Equal("+z", face.Local.Name);
        Assert.StartsWith("FALLBACK", face.Source);
    }

    [Fact]
    public void NoControlsFallBackToForward()
    {
        Assert.True(ControlFaceRule.Of(new List<Vec3>(), Body).Fallback);
    }

    [Fact]
    public void ATieGoesToTheSideWhoseControlsSitFurthestOut()
    {
        ControlFace face = ControlFaceRule.Of(new List<Vec3> { new Vec3(-0.49, 0.5, 0), new Vec3(0, 0.5, 0.3) },
            Body);

        Assert.Equal("-x", face.Local.Name);
    }

    [Fact]
    public void ATurnCarriesTheControlSideIntoTheWorld()
    {
        GridStep plusZ = GridStep.All[4];
        CubeRotation facingPlusX = CubeRotation.FromFacing(GridStep.All[0], GridStep.All[2])!;
        CubeRotation upsideDown = CubeRotation.FromFacing(GridStep.All[4], GridStep.All[3])!;

        Assert.Equal("+x", facingPlusX.Turn(plusZ).Name);
        Assert.Equal("-y", upsideDown.Turn(GridStep.All[2]).Name);
        Assert.Equal(facingPlusX.Forward.Name, facingPlusX.Turn(plusZ).Name);
    }

    [Fact]
    public void DescribePrefabReportsTheControlSide()
    {
        ControlFace face = ControlFace.Derived(GridStep.All[1], 4, 5);
        JObject json = JObject.Parse(WireCheck.New(new ControlFaceView(face)));

        Assert.Equal("-x", (string?)json["local"]);
        Assert.Equal(4, (int)json["votes"]!);
        Assert.False((bool)json["fallback"]!);
    }
}
