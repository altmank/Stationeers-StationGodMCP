using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The player-placement rule's decisions that do not need the game (PlayerPlacementRule, lint codes).</summary>
public sealed class PlayerPlacementRuleTests
{
    private const string BlockedBySelf = "Placement is blocked by Active Vent";

    [Fact]
    public void NoRefusalIsPlaceableWithoutAskingTheNeighbours()
    {
        bool asked = false;
        PlacementVerdict verdict = PlayerPlacementRule.Judge(new[] { new GameCheck(null, true), new GameCheck(null, false) },
            () =>
            {
                asked = true;
                return "never";
            });

        Assert.IsType<PlacementVerdict.Allowed>(verdict);
        Assert.False(asked);
    }

    [Fact]
    public void ARefusalNotBlamingTheReplacedThingStandsWithItsRule()
    {
        PlacementVerdict verdict = PlayerPlacementRule.Judge(new[] { new GameCheck("Placement requires support", false) },
            () => null);

        PlacementVerdict.Refused refused = Assert.IsType<PlacementVerdict.Refused>(verdict);
        Assert.Equal("Placement requires support", refused.Reason);
        Assert.Equal(PlacementRules.Support, refused.Rule);
    }

    [Fact]
    public void ARefusalBlamingOnlyTheReplacedThingIsDecidedByTheNeighbours()
    {
        Assert.IsType<PlacementVerdict.Allowed>(
            PlayerPlacementRule.Judge(new[] { new GameCheck(BlockedBySelf, true) }, () => null));

        PlacementVerdict clash = PlayerPlacementRule.Judge(new[] { new GameCheck(BlockedBySelf, true) },
            () => "Pipe (StructurePipeStraight 7) is in the way");
        PlacementVerdict.Refused refused = Assert.IsType<PlacementVerdict.Refused>(clash);
        Assert.Equal("Pipe (StructurePipeStraight 7) is in the way", refused.Reason);
        Assert.Equal(PlacementRules.Collision, refused.Rule);
    }

    // A face-mounted sensor has no CanConstruct of its own: its CanConstruct stops at itself, and only the cursor's
    // separate CanMountOnWall finds that nothing is behind it (the floating vent LU found).
    [Fact]
    public void AMountRefusalStandsThoughCanConstructOnlyMetTheThingItself()
    {
        PlacementVerdict verdict = PlayerPlacementRule.Judge(
            new[] { new GameCheck(BlockedBySelf, true), new GameCheck("Placement requires support", false) },
            () => null);

        Assert.Equal(PlacementRules.Support, Assert.IsType<PlacementVerdict.Refused>(verdict).Rule);
    }

    [Theory]
    [InlineData("Placement requires a Frame below for support", "support")]
    [InlineData("Placement requires support", "support")]
    [InlineData("Placement is only allowed on Iron/Steel Frame", "support")]
    [InlineData("Wall Iron does not allow mounting", "mount")]
    [InlineData("Must be mounted to a straight pipe", "host")]
    [InlineData("Cannot place on burst pipe", "host")]
    [InlineData("Must be placed outside", "location")]
    [InlineData("the cell belongs to a rocket", "location")]
    [InlineData("Cannot place outside of a rocket", "location")]
    [InlineData("Placement that connects to a Rocket needs to be inside a Fuselage or via Umbilical", "location")]
    [InlineData("Rocket Engine must be placed inside an Engine Fuselage", "location")]
    [InlineData("Umbilical (Gas) must be built inside a Rocket Tower", "location")]
    [InlineData("Fuselage must be constructed on top of a fully constructed launch mount", "support")]
    [InlineData("Placement requires a support frame below each pillar. 2 out of 4 supports are present.", "support")]
    [InlineData("Rockets can not be placed next to each other", "adjacent")]
    [InlineData("Cannot place adjacent to Volume Pump", "adjacent")]
    [InlineData("Placement is blocked by Pipe", "collision")]
    [InlineData("Locker (StructureStorageLocker 12) is in the way", "collision")]
    [InlineData("Elevator shaft rotation fail", null)]
    public void RefusalsFallUnderTheirRule(string reason, string? rule)
    {
        Assert.Equal(rule, PlayerPlacementRule.RuleOf(reason));
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.00009, true)]
    [InlineData(0.25, false)]
    public void OnGridWithinACentimetre(double squared, bool on)
    {
        Assert.Equal(on, PlayerPlacementRule.OnGrid(squared));
    }

    [Fact]
    public void TheReplyShapeIsTheValidatorsContract()
    {
        Assert.Equal("""{"results":[{"reference_id":"7","prefab_name":"StructureActiveVent","replaceable":false,"rule":"support","reason":"Placement requires support"},{"reference_id":"8","prefab_name":"StructureFrame","replaceable":true,"rule":null,"reason":null},{"reference_id":"9","prefab_name":null,"replaceable":null,"rule":null,"reason":"no thing has reference id 9"},{"reference_id":"10","prefab_name":"StructureWreck","replaceable":false,"rule":"no_kit","reason":"no kit builds StructureWreck, so no player can place one"}]}""",
            WireCheck.New(new StationGodMCP.Api.Views.CheckReplaceableView(new System.Collections.Generic.List<StationGodMCP.Api.Views.ReplaceableView>
            {
                new(new StationGodMCP.Api.Shared.ThingId(7), "StructureActiveVent", PlacementVerdict.Refuse("Placement requires support")),
                new(new StationGodMCP.Api.Shared.ThingId(8), "StructureFrame", PlacementVerdict.Placeable),
                new(new StationGodMCP.Api.Shared.ThingId(9), null, PlacementVerdict.Unchecked("no thing has reference id 9")),
                new(new StationGodMCP.Api.Shared.ThingId(10), "StructureWreck", PlacementVerdict.Refuse("no kit builds StructureWreck, so no player can place one", PlacementRules.NoKit))
            })));
    }

    [Theory]
    [InlineData(BlockedBySelf, true)]
    [InlineData(" Placement is blocked by Active Vent ", true)]
    [InlineData("Placement is blocked by Active Vent Large", false)]
    [InlineData("Placement requires support", false)]
    public void OnlyTheExactTextNamingAReplacedThingBlamesIt(string refusal, bool blames)
    {
        Assert.Equal(blames, PlayerPlacementRule.BlamesReplaced(refusal,
            new[] { "", "Cannot merge with Active Vent", BlockedBySelf }));
    }

    /// <summary>
    /// The 2026-10-08 hub smelter slab: three freshly built Combustors reported not_replaceable, the reason naming only
    /// the Combustor. StructureCombustor's inner pipe ends (PipeConnection at (0.5, 0, 0.5) facing +x and (0, 0, 0.5)
    /// facing -x) lie in the cells its Input2 and Output1 ports face, so Device.CanConstruct's adjacency check
    /// (SmallGrid.FillConnected) on a cursor where it stands meets the Combustor itself: "Cannot place adjacent to
    /// Combustor". That refusal is the replaced thing's; the devices met are checked again without it.
    /// </summary>
    [Fact]
    public void ACombustorMeetingOnlyItselfIsReplaceable()
    {
        const string adjacentToSelf = "Cannot place adjacent to Combustor";
        const long combustor = 4410;

        Assert.True(PlayerPlacementRule.BlamesReplaced(adjacentToSelf,
            new[] { "Placement is blocked by Combustor", adjacentToSelf }));
        Assert.Equal(-1,
            PlayerPlacementRule.FirstAdjacent(new[] { combustor, combustor }, new HashSet<long> { combustor }));
        Assert.IsType<PlacementVerdict.Allowed>(
            PlayerPlacementRule.Judge(new[] { new GameCheck(adjacentToSelf, true) }, () => null));
    }

    [Fact]
    public void ANeighbourCombustorTheSelfMatchHidStillRefusesUnderAdjacent()
    {
        const long replaced = 4410;
        const long neighbour = 4411;

        Assert.Equal(1,
            PlayerPlacementRule.FirstAdjacent(new[] { replaced, neighbour }, new HashSet<long> { replaced }));
        PlacementVerdict verdict = PlayerPlacementRule.Judge(
            new[] { new GameCheck("Cannot place adjacent to Combustor", true) },
            () => "Cannot place adjacent to Combustor (StructureCombustor 4411)");
        Assert.Equal(PlacementRules.Adjacent, Assert.IsType<PlacementVerdict.Refused>(verdict).Rule);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void AFrameRefusalIsTheReplacedThingsOnlyWhenItFillsTheCellAndTheFrameIsThere(bool requiresFrame,
        bool fills, bool frameBelow, bool blames)
    {
        Assert.Equal(blames, PlayerPlacementRule.FrameRefusalBlamesReplaced(requiresFrame, fills, frameBelow));
    }

    [Fact]
    public void OnlyAStraightPipeAlongAPipeMountedDeviceOfItsContentPassesUnderIt()
    {
        Assert.Null(PlayerPlacementRule.PipeUnderMountedDevice(true, 2, 2, true, "Pipe Analyzer (1)"));
        Assert.Contains("straight pipe along it",
            PlayerPlacementRule.PipeUnderMountedDevice(false, 2, 2, true, "Pipe Analyzer (1)"));
        Assert.Contains("straight pipe along it",
            PlayerPlacementRule.PipeUnderMountedDevice(true, 0, 2, true, "Pipe Analyzer (1)"));
        Assert.Contains("another pipe content",
            PlayerPlacementRule.PipeUnderMountedDevice(true, 2, 2, false, "Pipe Analyzer (1)"));
    }

    [Fact]
    public void NotReplaceableIsAProblemReportedFirstAndUncheckedIsInfo()
    {
        Assert.Equal("not_replaceable", LintCodes.NotReplaceable);
        Assert.Equal(ConflictLevel.Problem, LintCodes.LevelOf(LintCodes.NotReplaceable));
        Assert.Equal(ConflictLevel.Info, LintCodes.LevelOf(LintCodes.ReplaceableUnchecked));

        var ordered = LintReport.Ordered(new System.Collections.Generic.List<LintFinding>
        {
            new LintFinding(LintCodes.FloatingRun, "floats", 1, new Vec3(0, 0, 0)),
            new LintFinding(LintCodes.ReplaceableUnchecked, "unchecked", 2, new Vec3(0, 0, 0)),
            new LintFinding(LintCodes.NotReplaceable, "floating vent", 3, new Vec3(0, 0, 0))
        });

        Assert.Equal(new long?[] { 3, 1, 2 }, ordered.ConvertAll(finding => finding.ThingId).ToArray());
    }
}
