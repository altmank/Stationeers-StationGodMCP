#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// highlight (1.7.0): targets parsed as given, the bearing and distance a far point's label shows, where its marker
/// sits when the point is out of view, and the reply's shape.
/// </summary>
public sealed class HighlightTests
{
    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(10, 0, 90)]
    [InlineData(0, -10, 180)]
    [InlineData(-10, 0, 270)]
    [InlineData(10, 10, 45)]
    public void BearingRunsClockwiseFromNorth(double dx, double dz, double bearing)
    {
        Heading heading = Heading.From(new Vec3(100, 50, 100), new Vec3(100 + dx, 50, 100 + dz));

        Assert.Equal(bearing, heading.Bearing, 6);
    }

    [Fact]
    public void AHeadingCountsTheClimbAndNamesTheCompassPoint()
    {
        Heading heading = Heading.From(new Vec3(0, 0, 0), new Vec3(300, 40, 400));

        Assert.Equal(500, heading.Ground, 6);
        Assert.Equal(40, heading.Rise, 6);
        Assert.Equal("NE", heading.CompassPoint);
        Assert.Equal("502 m, bearing 37 deg NE, 40 m up", heading.Text());
        Assert.Equal("N", Heading.From(new Vec3(0, 0, 0), new Vec3(-1, 0, 100)).CompassPoint);
    }

    [Fact]
    public void APointStraightAboveHasBearingNorth()
    {
        Heading heading = Heading.From(new Vec3(5, 0, 5), new Vec3(5, 30, 5));

        Assert.Equal(0, heading.Bearing);
        Assert.Equal(30, heading.Distance, 6);
    }

    [Fact]
    public void APointInViewIsMarkedWhereItIs()
    {
        ScreenMarker marker = ScreenMarker.Of(0.3, 0.7, 50, 0.04);

        Assert.True(marker.OnScreen);
        Assert.Equal(0.3, marker.X, 6);
        Assert.Equal(0.7, marker.Y, 6);
    }

    [Fact]
    public void APointOffToTheRightIsPinnedToTheRightEdge()
    {
        ScreenMarker marker = ScreenMarker.Of(1.8, 0.5, 50, 0.04);

        Assert.False(marker.OnScreen);
        Assert.Equal(0.96, marker.X, 6);
        Assert.Equal(0.5, marker.Y, 6);
        Assert.Equal(0, marker.Angle, 6);
    }

    [Fact]
    public void APointBehindPointsTheOtherWay()
    {
        // Behind the camera, the projection mirrors: a point projected left lies behind and to the right.
        ScreenMarker marker = ScreenMarker.Of(0.2, 0.5, -20, 0.04);

        Assert.False(marker.OnScreen);
        Assert.True(marker.X > 0.9);
    }

    [Fact]
    public void PulseSwellsBetweenAFloorAndFull()
    {
        Assert.Equal(1.0, PulseCurve.Strength(0), 6);
        Assert.Equal(0.25, PulseCurve.Strength(0.5), 6);
    }

    [Fact]
    public void TargetsParseEachFormWithPaletteColoursInTurn()
    {
        HighlightRequest request = HighlightRequest.Parse(Args("""
            {"targets":[{"reference_ids":["5","6"]},{"network_id":"77","label":" Fuel "},
             {"at":[700,200,650],"color":"red","pulse":true},{"reference_id":"9","color":[0.5,0,1]}],
             "seconds":120,"keep":true}
            """));

        Assert.Equal(120, request.Seconds);
        Assert.True(request.Keep);
        HighlightTarget.Things things = Assert.IsType<HighlightTarget.Things>(request.Targets[0]);
        Assert.Equal(2, things.Ids.Count);
        Assert.Equal("cyan", things.ColorName);
        HighlightTarget.Network network = Assert.IsType<HighlightTarget.Network>(request.Targets[1]);
        Assert.Equal(77, network.Id.Value);
        Assert.Equal("magenta", network.ColorName);
        Assert.Equal("Fuel", network.Label);
        HighlightTarget.Point point = Assert.IsType<HighlightTarget.Point>(request.Targets[2]);
        Assert.Equal(new Vec3(700, 200, 650), point.At);
        Assert.Equal("red", point.ColorName);
        Assert.True(point.Pulse);
        Assert.Equal("custom", request.Targets[3].ColorName);
        Assert.Equal(0.5, request.Targets[3].Tint.Red);
    }

    [Theory]
    [InlineData("""{"targets":[{"reference_id":"5","at":[1,2,3]}]}""", "exactly one of")]
    [InlineData("""{"targets":[{"label":"x"}]}""", "exactly one of")]
    [InlineData("""{"targets":[{"reference_id":"5","color":"purple"}]}""", "color must be one of")]
    [InlineData("""{"targets":[{"reference_id":"5","color":[2,0,0]}]}""", "color must be one of")]
    [InlineData("""{"targets":[{"reference_id":"5"}],"seconds":601}""", "seconds is at most")]
    public void BadTargetsAreRefused(string json, string message)
    {
        ApiException refused = Assert.Throws<ApiException>(() => HighlightRequest.Parse(Args(json)));

        Assert.Equal("invalid_argument", refused.Code);
        Assert.Contains(message, refused.Message);
    }

    [Fact]
    public void APointTargetReportsWhereItIs()
    {
        HighlightRequest request =
            HighlightRequest.Parse(Args("""{"targets":[{"at":[300,40,400],"label":"Cobalt"}]}"""));
        HighlightTarget.Point point = (HighlightTarget.Point)request.Targets[0];
        JObject json = JObject.Parse(WireCheck.New(new HighlightView(
            new List<HighlightTargetView>
            {
                HighlightTargetView.OfPoint(0, point, Heading.From(new Vec3(0, 0, 0), point.At))
            }, 0, 60, "built-in", new List<string>())));

        JToken target = json["targets"]![0]!;
        Assert.Equal("point", (string?)target["kind"]);
        Assert.Equal("Cobalt", (string?)target["label"]);
        Assert.Equal(501.6, (double)target["distance_m"]!, 1);
        Assert.Equal(37.0, (double)target["bearing_deg"]!);
        Assert.Equal("NE", (string?)target["compass"]);
        Assert.Null(target["things"]);
        Assert.Equal("built-in", (string?)json["renderer"]);
    }

    [Fact]
    public void AThingsTargetListsWhatWasMissing()
    {
        HighlightRequest request = HighlightRequest.Parse(Args("""{"targets":[{"reference_ids":["5","6"]}]}"""));
        JObject json = JObject.Parse(WireCheck.New(HighlightTargetView.OfThings(0, request.Targets[0], "things", null,
            1, new List<ThingId> { new ThingId(6) }, null, false)));

        Assert.Equal(1, (int)json["things"]!);
        Assert.Equal("6", (string?)json["missing"]![0]);
        Assert.Null(json["truncated"]);
        Assert.Equal(JTokenType.Null, json["distance_m"]!.Type);
    }

    [Theory]
    [InlineData("""{"targets":[{"at":[700,200,650],"label":"Cobalt","color":"orange","pulse":true}],"seconds":300}""")]
    [InlineData("""{"targets":[{"network_id":"77","color":[1,0.5,0]},{"reference_ids":["5"]}],"keep":true}""")]
    [InlineData("""{"clear":true}""")]
    public void TheSidecarTakesHighlightArguments(string arguments)
    {
        Assert.Empty(Problems("highlight", arguments));
    }

    [Fact]
    public void ShowPreviewTakesXray()
    {
        Assert.Empty(Problems("show_preview", """{"cells":[[1,1,1]],"xray":true}"""));
    }

    private static Args Args(string json) => new Args(JObject.Parse(json));

    private static IReadOnlyList<string> Problems(string tool, string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        return ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement);
    }
}
