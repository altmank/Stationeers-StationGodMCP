#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>place_structure: quarter-turn rotations in Unity's conventions.</summary>
public sealed class CubeRotationTests
{
    private static GridStep Step(string name)
    {
        Assert.True(GridStep.TryParse(name, out GridStep step));
        return step;
    }

    [Fact]
    public void TheIdentityFacesPlusZWithItsTopUp()
    {
        Assert.Equal("+z", CubeRotation.Identity.Forward.Name);
        Assert.Equal("+y", CubeRotation.Identity.Up.Name);
        Assert.Equal("+x", CubeRotation.Identity.Right.Name);
        Assert.Equal((0.0, 0.0, 0.0, 1.0), CubeRotation.Identity.ToQuaternion());
    }

    [Fact]
    public void EulerTurnsFollowQuaternionEuler()
    {
        // Quaternion.Euler(0, 90, 0) * Vector3.forward is Vector3.right.
        CubeRotation yaw = CubeRotation.FromEuler(0, 1, 0);
        Assert.Equal("+x", yaw.Forward.Name);
        Assert.Equal("+y", yaw.Up.Name);

        // Quaternion.Euler(90, 0, 0) pitches forward down.
        CubeRotation pitch = CubeRotation.FromEuler(1, 0, 0);
        Assert.Equal("-y", pitch.Forward.Name);
        Assert.Equal("+z", pitch.Up.Name);

        // Z, then X, then Y: forward goes down with the pitch and stays down under the yaw; up turns +z then +x.
        CubeRotation both = CubeRotation.FromEuler(1, 1, 0);
        Assert.Equal("-y", both.Forward.Name);
        Assert.Equal("+x", both.Up.Name);
        Assert.Equal(CubeRotation.AboutY(1).Times(CubeRotation.AboutX(1)), both);
    }

    [Fact]
    public void AQuarterYawIsTheUnityQuaternion()
    {
        (double x, double y, double z, double w) = CubeRotation.FromEuler(0, 1, 0).ToQuaternion();
        Assert.Equal(0.0, x, 6);
        Assert.Equal(0.70710678, y, 6);
        Assert.Equal(0.0, z, 6);
        Assert.Equal(0.70710678, w, 6);
    }

    [Fact]
    public void EveryFacingAndUpIsOneOfTwentyFourRotationsThatRoundTripThroughQuaternions()
    {
        HashSet<CubeRotation> all = new HashSet<CubeRotation>();
        foreach (GridStep forward in GridStep.All)
        {
            foreach (GridStep up in GridStep.All)
            {
                CubeRotation? rotation = CubeRotation.FromFacing(forward, up);
                if (forward.Axis == up.Axis)
                {
                    Assert.Null(rotation);
                    continue;
                }

                Assert.NotNull(rotation);
                Assert.Equal(forward, rotation!.Forward);
                Assert.Equal(up, rotation.Up);
                (double x, double y, double z, double w) = rotation.ToQuaternion();
                Assert.Equal(1.0, x * x + y * y + z * z + w * w, 9);
                Assert.Equal(rotation, CubeRotation.FromQuaternion(x, y, z, w));
                all.Add(rotation);
            }
        }

        Assert.Equal(24, all.Count);
    }

    [Fact]
    public void AQuaternionOffTheGridIsNoCubeRotation()
    {
        // 45 degrees about y.
        Assert.Null(CubeRotation.FromQuaternion(0, 0.38268343, 0, 0.92387953));
        // A near-quarter turn from float noise still reads as the quarter turn.
        Assert.Equal(CubeRotation.FromEuler(0, 1, 0), CubeRotation.FromQuaternion(0, 0.7071, 0, 0.70712));
    }

    [Theory]
    [InlineData(0, true, 0)]
    [InlineData(90, true, 1)]
    [InlineData(270, true, 3)]
    [InlineData(-90, true, 3)]
    [InlineData(360, true, 0)]
    [InlineData(450, true, 1)]
    [InlineData(45, false, 0)]
    [InlineData(89.9, false, 0)]
    public void OnlyMultiplesOfNinetyAreQuarterTurns(double degrees, bool ok, int turns)
    {
        Assert.Equal(ok, CubeRotation.TryQuarterTurns(degrees, out int got));
        if (ok)
        {
            Assert.Equal(turns, got);
        }
    }

    [Fact]
    public void TheCursorReachesFourTurnsAboutYEightWhenMountedAndAllWithTwoAxes()
    {
        HashSet<CubeRotation> yOnly = CubeRotation.Reachable(0, 1, 0);
        Assert.Equal(4, yOnly.Count);
        Assert.All(yOnly, rotation => Assert.Equal("+y", rotation.Up.Name));
        Assert.Equal(24, CubeRotation.Reachable(1, 1, 0).Count);
        Assert.Equal(24, CubeRotation.Reachable(1, 1, 1).Count);
        Assert.Equal(8, CubeRotation.Reachable(2, 1, 0).Count);
        Assert.Single(CubeRotation.Reachable(0, 0, 0));
    }

    [Fact]
    public void CursorTurnsAllowOnlyTheirAxes()
    {
        CubeRotation yaw = CubeRotation.FromFacing(Step("-x"), Step("+y"))!;
        CubeRotation onItsBack = CubeRotation.FromFacing(Step("+y"), Step("-z"))!;
        Assert.True(PlacementRule.CursorCanTurn(yaw, false, true, false, false));
        Assert.False(PlacementRule.CursorCanTurn(onItsBack, false, true, false, false));
        Assert.True(PlacementRule.CursorCanTurn(onItsBack, true, true, false, false));
        Assert.True(PlacementRule.CursorCanTurn(CubeRotation.Identity, false, false, false, false));
        Assert.False(PlacementRule.CursorCanTurn(yaw, false, false, false, false));
    }
}

/// <summary>place_structure: resolving the turn, the build state and free placement.</summary>
public sealed class PlacementResolutionTests
{
    private static GridStep Step(string name)
    {
        Assert.True(GridStep.TryParse(name, out GridStep step));
        return step;
    }

    [Fact]
    public void AFacingTakesUpPlusYOrPlusZWhenItIsVertical()
    {
        CubeRotation? level = new RotationSpec.Facing(Step("-x"), null).Resolve(out string? error);
        Assert.Null(error);
        Assert.Equal("+y", level!.Up.Name);

        CubeRotation? down = new RotationSpec.Facing(Step("-y"), null).Resolve(out error);
        Assert.Null(error);
        Assert.Equal("-y", down!.Forward.Name);
        Assert.Equal("+z", down.Up.Name);
    }

    [Fact]
    public void UpAlongTheFacingIsRefused()
    {
        Assert.Null(new RotationSpec.Facing(Step("+x"), Step("-x")).Resolve(out string? error));
        Assert.Contains("same axis", error);
    }

    [Fact]
    public void APieceOnAFaceLooksIntoTheCell()
    {
        CubeRotation? wall = new RotationSpec.OnFace(Step("+x"), null).Resolve(out string? error);
        Assert.Null(error);
        Assert.Equal("-x", wall!.Forward.Name);
        Assert.Equal("+y", wall.Up.Name);

        CubeRotation? floor = new RotationSpec.OnFace(Step("-y"), null).Resolve(out error);
        Assert.Equal("+y", floor!.Forward.Name);
        Assert.Equal("+z", floor.Up.Name);
    }

    [Fact]
    public void NoTurnIsThePrefabsOwn()
    {
        Assert.Equal(CubeRotation.Identity, RotationSpec.Default.Resolve(out _));
    }

    [Fact]
    public void BuildStatesResolveAgainstThePrefabsCount()
    {
        Assert.Equal(2, BuildStatePick.Finished.Resolve(3, out string? error));
        Assert.Null(error);
        Assert.Equal(0, BuildStatePick.First.Resolve(3, out _));
        Assert.Equal(1, new BuildStatePick.Index(1).Resolve(3, out _));
        Assert.Null(new BuildStatePick.Index(3).Resolve(3, out error));
        Assert.Contains("between 0 and 2", error);
        Assert.Null(BuildStatePick.Finished.Resolve(0, out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void FreePlacementNeedsACreativeWorld()
    {
        Assert.NotNull(PlacementRule.FreeRefusal(true, false));
        Assert.Null(PlacementRule.FreeRefusal(true, true));
        Assert.Null(PlacementRule.FreeRefusal(false, false));
    }
}

/// <summary>remove_structure's guards: what refuses, what an allow flag turns into a warning.</summary>
public sealed class RemovalRuleTests
{
    private static readonly RemovalAllowance None = new RemovalAllowance(false, false);
    private static readonly RemovalAllowance All = new RemovalAllowance(true, true);
    private static readonly RemovalAllowance Broken = new RemovalAllowance(false, false, broken: true);

    [Fact]
    public void APlainPieceHasNoFindings()
    {
        Assert.Empty(RemovalRule.Judge(new RemovalFacts(), None));
    }

    [Fact]
    public void HardRefusalsIgnoreTheAllowFlags()
    {
        RemovalFacts facts = new RemovalFacts
        {
            Indestructible = true, RocketMoving = "Rocket 1 is launching", Broken = true, BeingDestroyed = true,
            GameRefusal = "a device is attached", Attached = "Pipe Analyzer (1)"
        };
        List<GuardFinding> findings = RemovalRule.Judge(facts, All);
        Assert.Equal(new[] { "being_destroyed", "indestructible", "rocket_moving", "broken", "game_refuses", "has_mounted" },
            findings.Select(finding => finding.Code));
        Assert.All(findings, finding => Assert.Equal(GuardLevel.Refusal, finding.Level));
        Assert.True(RemovalRule.Refused(findings));
    }

    [Fact]
    public void BrokenRefusesUnlessAllowedAndTheRefusalNamesTheFlag()
    {
        GuardFinding refused = RemovalRule.Judge(new RemovalFacts { Broken = true }, All).Single();
        Assert.Equal("broken", refused.Code);
        Assert.Equal(GuardLevel.Refusal, refused.Level);
        Assert.Contains("pass allow_broken", refused.Message);
        Assert.Contains("gives nothing back", refused.Message);
        Assert.DoesNotContain("repair it first", refused.Message);

        GuardFinding allowed = RemovalRule.Judge(new RemovalFacts { Broken = true }, Broken).Single();
        Assert.Equal("broken_removed", allowed.Code);
        Assert.Equal(GuardLevel.Warning, allowed.Level);
    }

    [Fact]
    public void AllowBrokenSkipsTheGameRefusalOnlyForABrokenPiece()
    {
        // The game's deconstruction of a broken thing never asks CanDeconstruct (Structure.AttackWith).
        RemovalFacts broken = new RemovalFacts { Broken = true, GameRefusal = "a device is attached" };
        Assert.False(RemovalRule.Refused(RemovalRule.Judge(broken, Broken)));

        RemovalFacts whole = new RemovalFacts { GameRefusal = "a device is attached" };
        Assert.Equal("game_refuses", RemovalRule.Judge(whole, Broken).Single().Code);
    }

    [Fact]
    public void AllowBrokenKeepsEveryOtherGuard()
    {
        RemovalFacts facts = new RemovalFacts
        {
            Broken = true, Unsupported = "Pipe Analyzer (1)", GasMoles = 3, GasFate = GasFate.Lost, BreachKpa = 50,
            BreachWhere = "w"
        };
        facts.Items.Add("ItemIronIngot x5 (9)");
        List<GuardFinding> findings = RemovalRule.Judge(facts, Broken);
        Assert.Equal(new[] { "broken_removed", "has_mounted", "holds_items", "holds_gas", "would_breach" },
            findings.Select(finding => finding.Code));
        Assert.True(RemovalRule.Refused(findings));

        RemovalFacts rocket = new RemovalFacts { Broken = true, RocketMoving = "landing", Indestructible = true };
        Assert.Equal(new[] { "indestructible", "rocket_moving", "broken_removed" },
            RemovalRule.Judge(rocket, Broken).Select(finding => finding.Code));
    }

    [Fact]
    public void ContentsRefuseUnlessAllowedAndThenSayWhatHappens()
    {
        RemovalFacts facts = new RemovalFacts { GasMoles = 12.5, GasFate = GasFate.Released };
        facts.Items.Add("ItemIronIngot x50 (123)");

        List<GuardFinding> refused = RemovalRule.Judge(facts, None);
        Assert.Equal(new[] { "holds_items", "holds_gas" }, refused.Select(finding => finding.Code));
        Assert.Contains("ItemIronIngot x50", refused[0].Message);
        Assert.Contains("allow_contents", refused[1].Message);

        List<GuardFinding> allowed = RemovalRule.Judge(facts, All);
        Assert.Equal(new[] { "items_dropped", "gas_released" }, allowed.Select(finding => finding.Code));
        Assert.False(RemovalRule.Refused(allowed));

        RemovalFacts lost = new RemovalFacts { GasMoles = 1, GasFate = GasFate.Lost };
        Assert.Equal("contents_deleted", RemovalRule.Judge(lost, All).Single().Code);
    }

    [Fact]
    public void TraceGasIsNoContents()
    {
        Assert.Empty(RemovalRule.Judge(new RemovalFacts { GasMoles = 0.0005 }, None));
    }

    [Fact]
    public void ABreachStartsAtOneKilopascal()
    {
        Assert.Empty(RemovalRule.Judge(new RemovalFacts { BreachKpa = 0.99 }, None));
        GuardFinding breach = RemovalRule.Judge(new RemovalFacts { BreachKpa = 101.3, BreachWhere = "w" }, None)
            .Single();
        Assert.Equal("would_breach", breach.Code);
        Assert.Contains("101.3 kPa", breach.Message);
        Assert.Equal("breach", RemovalRule.Judge(new RemovalFacts { BreachKpa = 5 }, All).Single().Code);
    }

    [Fact]
    public void TheSpreadIsTheLargestDifference()
    {
        Assert.Null(RemovalRule.Spread(new List<double> { 101.3 }));
        Assert.Equal(101.3, RemovalRule.Spread(new List<double> { 50, 101.3, 0 })!.Value, 6);
        Assert.Equal(0.0, RemovalRule.Spread(new List<double> { 20, 20 })!.Value, 6);
    }
}

/// <summary>place_structure's and remove_structure's wire shapes.</summary>
public sealed class BuildViewTests
{
    [Fact]
    public void APlacementReportIsSnakeCase()
    {
        PlacementView placement = new PlacementView(0,
            new PlacementPrefabView("StructureWallLight", 123, "Wall Light", 2),
            new PlacementSpotView("face_mount", new PositionView(1, 2.5, 3),
                new OrientationView("-x", "+y", new RotationView(0, 270, 0)), null),
            new PlacementLookView(1, "Hall", new ColorView(3, "Blue")),
            new List<UpgradeAmountView> { new UpgradeAmountView("ItemWallLight", 1) });
        PlaceReportView report = new PlaceReportView(
            new BuildHeader("dry_run", null, new List<BuildIssueView>(),
                new List<BuildIssueView> { new BuildIssueView("network_piece", "m", 0) }, new List<string>()),
            new List<PlacementView> { placement },
            new List<BuildMaterialView> { new BuildMaterialView("ItemWallLight", 1, 4) },
            new ThingView(new ThingId(9), "Human", "Player"), false);

        JObject json = JObject.Parse(WireCheck.New(report));
        Assert.Equal("place_structure", (string?)json["tool"]);
        Assert.True((bool)json["ready"]!);
        Assert.Equal("network_piece", (string?)json["warnings"]![0]!["code"]);
        JToken first = json["placements"]![0]!;
        Assert.Equal("face_mount", (string?)first["placement"]);
        Assert.Equal("-x", (string?)first["orientation"]!["facing"]);
        Assert.Equal(270.0, (double)first["orientation"]!["euler"]!["y"]!);
        Assert.Equal(1, (int)first["build_state"]!);
        Assert.Equal(2, (int)first["build_states"]!);
        Assert.Equal("ItemWallLight", (string?)first["cost"]![0]!["prefab_name"]);
        Assert.Equal(4, (int)json["materials"]![0]!["available"]!);
        Assert.Equal("9", (string?)json["from"]!["reference_id"]);
    }

    [Fact]
    public void AFinishedJobCarriesItsChecks()
    {
        BuildLog log = new BuildLog();
        log.Removed.Add(new BuiltPieceView(0, new ThingView(new ThingId(5), "StructureWallIron", "Wall")));
        BuildJobView job = new BuildJobView("remove-1", "remove_structure", "applied", new { tool = "x" },
            new BuildJobResult(null, log,
                new List<BuildCheckView> { new BuildCheckView(0, new ThingId(5), new List<string>()) }, null));

        JObject json = JObject.Parse(WireCheck.New(job));
        Assert.Equal("remove-1", (string?)json["job_id"]);
        Assert.Equal("5", (string?)json["result"]!["removed"]![0]!["reference_id"]);
        Assert.True((bool)json["result"]!["verification"]![0]!["ok"]!);
        Assert.Equal(JTokenType.Null, json["result"]!["stopped_at"]!.Type);
    }
}
