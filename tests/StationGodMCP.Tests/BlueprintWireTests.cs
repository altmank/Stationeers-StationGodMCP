#nullable enable

using System.IO;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>paste_blueprint: its replies, the turns it takes, the file it looks for and the time it expects.</summary>
public sealed class BlueprintWireTests
{
    private const string File = @"C:\Blueprints\frame-floor-3x3.blueprint";

    [Fact]
    public void StartedReportsTheCallMade()
    {
        var expected = new
        {
            started = true, file = File, entries = 9, anchor = new[] { 10.0, 2.5, -4.0 }, rotation = 90,
            copy_y_angle = 180f, expected_duration_s = 2.0
        };
        WireCheck.Same(expected,
            new BlueprintPasteStartedView(new BlueprintFileView(File, 9), new[] { 10.0, 2.5, -4.0 }, 90, 180f, 2.0));
    }

    [Fact]
    public void StatusBeforeAnyPasteIsUnknown()
    {
        var expected = new
        {
            known = false, active = false, complete = (bool?)null, cancelled = (bool?)null, created = (int?)null,
            failed = (int?)null, skipped = (int?)null, pasted = (int?)null, standing = (int?)null,
            fingerprint = (string?)null,
            file = (string?)null, entries = (int?)null, other_active = true
        };
        WireCheck.Same(expected, BlueprintPasteStatusView.Unknown(otherActive: true));
    }

    [Fact]
    public void StatusKeepsTheCountsAfterThePasteEnds()
    {
        var expected = new
        {
            known = true, active = false, complete = (bool?)true, cancelled = (bool?)false, created = (int?)52,
            failed = (int?)0, skipped = (int?)1, pasted = (int?)52, standing = (int?)50,
            fingerprint = (string?)"53@1.0,2.0,3.0r90",
            file = (string?)File, entries = (int?)53, other_active = false
        };
        WireCheck.Same(expected, BlueprintPasteStatusView.Of(new BlueprintFileView(File, 53), false,
            new BlueprintPasteCountsView(true, false, 52, 0, 1, 52, 50, "53@1.0,2.0,3.0r90"), false));
    }

    [Fact]
    public void StatusOfAPasteThatEndedInsideItsCallHasNoCounts()
    {
        var expected = new
        {
            known = true, active = false, complete = (bool?)null, cancelled = (bool?)null, created = (int?)null,
            failed = (int?)null, skipped = (int?)null, pasted = (int?)null, standing = (int?)null,
            fingerprint = (string?)null,
            file = (string?)File, entries = (int?)1, other_active = false
        };
        WireCheck.Same(expected, BlueprintPasteStatusView.Of(new BlueprintFileView(File, 1), false, null, false));
    }

    [Fact]
    public void UndoReturnsBlueprintModsMessage()
    {
        WireCheck.Same(new { message = "Nothing to undo." }, new BlueprintUndoView("Nothing to undo."));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    [InlineData(45, false)]
    [InlineData(-90, false)]
    [InlineData(360, false)]
    public void RotationIsAQuarterTurn(int degrees, bool accepted)
    {
        PasteRotation? rotation = PasteRotation.FromDegrees(degrees);
        Assert.Equal(accepted, rotation != null);
        if (rotation != null)
        {
            Assert.Equal(degrees, rotation.Degrees);
        }
    }

    [Theory]
    [InlineData("frame-floor-3x3", "frame-floor-3x3.blueprint")]
    [InlineData("frame-floor-3x3.blueprint", "frame-floor-3x3.blueprint")]
    [InlineData("Frame.BLUEPRINT", "Frame.BLUEPRINT")]
    public void ANameIsAFileInTheBlueprintsFolder(string name, string file)
    {
        Assert.Equal(Path.Combine(@"C:\Game\Blueprints", file), BlueprintFiles.Resolve(name, @"C:\Game\Blueprints"));
    }

    [Fact]
    public void AnAbsolutePathIsTakenAsGiven()
    {
        string path = Path.Combine(Path.GetTempPath(), "scratch", "bathroom.xml");
        Assert.Equal(path, BlueprintFiles.Resolve(path, @"C:\Game\Blueprints"));
        Assert.Equal(path, BlueprintFiles.Resolve(path, null));
    }

    [Fact]
    public void ANameNeedsTheFolder()
    {
        Assert.Null(BlueprintFiles.Resolve("frame-floor-3x3", null));
        Assert.Null(BlueprintFiles.Resolve("frame-floor-3x3", string.Empty));
    }

    [Theory]
    [InlineData(1, 2.0)]
    [InlineData(9, 2.0)]
    [InlineData(52, 7.8)]
    [InlineData(200, 30.0)]
    [InlineData(1000, 30.0)]
    public void ExpectedDurationIsBlueprintModsFormula(int entries, double seconds)
    {
        Assert.Equal(seconds, BlueprintFiles.ExpectedDurationSeconds(entries));
    }
}
