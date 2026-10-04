#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Screens;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>screen_read's boxes and screen_press's hit test, as ScriptedScreens places elements.</summary>
public sealed class ScreenLayoutTests
{
    private static ScreenElement Px(string id, string type, double x, double y, double w, double h, double z = 0,
        string? parent = null, bool visible = true) =>
        new ScreenElement(id, type, false, x, y, w, h, parent, z, visible);

    [Fact]
    public void PixelRectsOffsetFromTheirParentAndNormalizedOnesScaleIt()
    {
        List<ScreenElement> elements = new List<ScreenElement>
        {
            Px("card", "panel", 100, 50, 200, 100),
            Px("ok", "button", 10, 20, 50, 30, parent: "card"),
            new ScreenElement("half", "panel", true, 0.5, 0, 0.5, 1, "card", 0, true),
            new ScreenElement("root", "panel", true, 0, 0, 1, 0.5, null, 0, true)
        };
        ScreenLayout layout = new ScreenLayout(elements, 640, 480);

        ScreenBox ok = layout.BoxOf("ok")!.Value;
        Assert.Equal((110.0, 70.0, 50.0, 30.0), (ok.X, ok.Y, ok.W, ok.H));
        ScreenBox half = layout.BoxOf("half")!.Value;
        Assert.Equal((200.0, 50.0, 100.0, 100.0), (half.X, half.Y, half.W, half.H));
        Assert.Equal(240.0, layout.BoxOf("root")!.Value.H);
        Assert.Null(new ScreenLayout(elements, null, null).BoxOf("root"));
    }

    [Fact]
    public void ATouchTakesTheShownControlWithTheHighestZIndex()
    {
        List<ScreenElement> elements = new List<ScreenElement>
        {
            Px("bg", "panel", 0, 0, 640, 640, 1),
            Px("tab1", "button", 8, 8, 308, 66, 5),
            Px("label", "label", 8, 8, 308, 66, 9),
            Px("under", "button", 0, 0, 400, 100, 2),
            Px("hidden", "button", 0, 0, 640, 640, 50, visible: false),
            Px("tray", "panel", 0, 300, 640, 100, 1, visible: false),
            Px("inTray", "button", 0, 0, 100, 100, 60, parent: "tray")
        };
        ScreenLayout layout = new ScreenLayout(elements, 640, 640);

        Assert.Equal("tab1", layout.ControlAt(elements, 20, 20)!.Id);
        Assert.Equal("under", layout.ControlAt(elements, 350, 50)!.Id);
        Assert.Null(layout.ControlAt(elements, 50, 350));
        Assert.False(layout.IsShown("inTray"));
    }

    [Fact]
    public void ARowInAScrollViewIsPlacedInIt()
    {
        List<ScreenElement> elements = new List<ScreenElement>
        {
            Px("list", "scrollview", 0, 100, 300, 200),
            Px("list/row1", "button", 0, 10, 300, 20),
            Px("note/row1", "label", 0, 10, 300, 20)
        };
        ScreenLayout layout = new ScreenLayout(elements, 640, 640);

        Assert.Equal(110.0, layout.BoxOf("list/row1")!.Value.Y);
        Assert.Equal(10.0, layout.BoxOf("note/row1")!.Value.Y);
    }

    [Fact]
    public void AnElementViewCarriesItsBoxAndPropsAndLeavesStyleOut()
    {
        JObject wire = JObject.Parse(WireCheck.New(new ScreenElementView("tab1", "button",
            new ScreenBoxView(8, 8, 308, 66), true, true, new JObject { ["text"] = "Pad", ["z_index"] = "5" }, null)));

        Assert.Equal("{\"id\":\"tab1\",\"type\":\"button\",\"box\":{\"x\":8.0,\"y\":8.0,\"w\":308.0,\"h\":66.0}," +
                     "\"shown\":true,\"touch\":true,\"props\":{\"text\":\"Pad\",\"z_index\":\"5\"}}",
            wire.ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal(5.0, ScreenElement.ZOf("5"));
        Assert.False(ScreenElement.VisibleOf("0"));
        Assert.True(ScreenElement.VisibleOf(null));
    }
}
