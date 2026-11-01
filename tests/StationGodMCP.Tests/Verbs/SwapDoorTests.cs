#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// swap door (2.8.0) without the game: the what takes walls' options with a required into, reports until confirm, and
/// what it hands over follows DoorHandoverRule. Pins, outputs and mirrors are repointed. A program that names the old
/// id, and a batch of another prefab, still point at the old door. The handover rows are written into a piece's
/// report and left out for walls and frames.
/// </summary>
public sealed class SwapDoorTests
{
    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("swap")!, JObject.Parse(options), out _);

    [Fact]
    public void ADoorNeedsIntoAndNamesItsDoorsByIdOrRoom()
    {
        Assert.Null(Check("""{"what": "door", "id": "4410", "into": "StructureAirlock"}"""));
        Assert.Null(Check("""{"what": "door", "room": "17", "into": "StructureAirlock", "confirm": true}"""));
        Assert.Equal("invalid_argument", Check("""{"what": "door", "id": "4410"}""")!.Code);
        SwapDoorOptions options = SwapDoorOptions.Bind(new OptionReader(SwapDoorOptions.Spec,
            JObject.Parse("""{"id": ["1", "2"], "into": "StructureAirlock", "refund_to": "ground"}""")));
        Assert.Equal("StructureAirlock", options.Into);
        Assert.Equal(new[] { "1", "2" }, options.Selector.Ids!.Ids);
    }

    [Fact]
    public void ADoorSwapReportsFirstAndCannotBeUndone()
    {
        WriteSpec write = SwapDoorOptions.Spec.Write!;
        Assert.Equal("confirm", write.Route);
        Assert.Equal("cannot", write.Undo);
        Assert.Null(write.Cheat);
    }

    [Theory]
    [InlineData("d0")]
    [InlineData("d5")]
    [InlineData("db")]
    [InlineData("output")]
    [InlineData("mirror")]
    public void PinsOutputsAndMirrorsAreRepointed(string via)
    {
        Assert.Null(DoorHandoverRule.StillPointing(via, 4410, samePrefab: false));
    }

    [Fact]
    public void AProgramNamingTheOldIdStillPointsAtIt()
    {
        Assert.Contains("4410", DoorHandoverRule.StillPointing("id", 4410, samePrefab: true));
    }

    [Theory]
    [InlineData("sb")]
    [InlineData("sbn")]
    [InlineData("prefab_hash")]
    public void ABatchReachesTheNewDoorOnlyWhenItIsTheSamePrefab(string via)
    {
        Assert.Null(DoorHandoverRule.StillPointing(via, 4410, samePrefab: true));
        Assert.Contains("prefab", DoorHandoverRule.StillPointing(via, 4410, samePrefab: false));
    }

    [Fact]
    public void OnlyD0ToD5ArePins()
    {
        Assert.True(DoorHandoverRule.IsPin("d3"));
        Assert.False(DoorHandoverRule.IsPin("d6"));
        Assert.False(DoorHandoverRule.IsPin("db"));
        Assert.False(DoorHandoverRule.IsPin("id"));
    }

    [Fact]
    public void APieceCarriesItsHandoverAndAWallsLeavesItOut()
    {
        StructurePieceView door = Piece(new System.Collections.Generic.List<DoorHandoverView>
        {
            new DoorHandoverView(new ThingId(77), "StructureCircuitHousing", "d0", DoorHandoverRule.Repoint, null),
            new DoorHandoverView(new ThingId(4410), "StructureGlassDoor", "open", DoorHandoverRule.Keep, null)
        });
        JObject written = JObject.FromObject(door, ApiJson.Serializer);
        JArray handover = (JArray)written["handover"]!;
        Assert.Equal("d0", (string?)handover[0]["how"]);
        Assert.Equal("repoint", (string?)handover[0]["action"]);
        Assert.Null(handover[0]["note"]);
        Assert.Null(JObject.FromObject(Piece(null), ApiJson.Serializer)["handover"]);
    }

    private static StructurePieceView Piece(System.Collections.Generic.List<DoorHandoverView>? handover) =>
        new StructurePieceView(new ThingView(new ThingId(4410), "StructureGlassDoor", "Glass Door"),
            new PositionView(1f, 2f, 3f), new RotationView(0f, 90f, 0f),
            new StructureTargetView("StructureGlassDoor", 2, 3),
            new StructureBlockingView(true, true, false, false, "keeps", false), null,
            new System.Collections.Generic.List<string> { "17" },
            new System.Collections.Generic.List<StructureMaterialLineView>(), handover);
}
