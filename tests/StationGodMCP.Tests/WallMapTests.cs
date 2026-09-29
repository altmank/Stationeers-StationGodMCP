#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>wall_map and find_spot (1.4.3): the elevation text, free rectangles, the spot filter and rank.</summary>
public sealed class WallMapTests
{
    // LU's wall z = 668 seen from +z: columns x 717 to 720 (right is -x seen from +z, so column 0 is x 720), rows y
    // 202 down to 200. A console (key A) at x 718.5 to 719, y 200.5 to 201; a pipe at x 719.5, y 201.
    private static WallMap Lu()
    {
        const int rows = 5;
        const int columns = 7;
        WallCell[,] cells = new WallCell[rows, columns];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                double x = 720 - column * 0.5;
                double y = 202 - row * 0.5;
                char thing = (x == 718.5 || x == 719) && (y == 200.5 || y == 201) ? 'A'
                    : x == 719.5 && y == 201 ? 'p'
                    : '\0';
                cells[row, column] = new WallCell(FaceLook.Wall, thing, false);
            }
        }

        return new WallMap(720, -0.5, 202, cells);
    }

    [Fact]
    public void TheElevationReadsLeftToRightAsSeen()
    {
        List<string> lines = Lu().Lines();
        Assert.Equal("|   |  ", lines[0]);
        Assert.Equal("WWWWWWW", lines[1]);
        Assert.Equal("WWWWWWW", lines[2]);
        Assert.Equal("WpAAWWW", lines[3]);
        Assert.Equal("WWAAWWW", lines[4]);
        Assert.Equal("WWWWWWW", lines[5]);
    }

    [Fact]
    public void SymbolsFollowTheLegend()
    {
        Assert.Equal('G', new WallCell(FaceLook.Window, '\0', false).Symbol);
        Assert.Equal('D', new WallCell(FaceLook.Door, '\0', true).Symbol);
        Assert.Equal('x', new WallCell(FaceLook.Open, '\0', true).Symbol);
        Assert.Equal('F', new WallCell(FaceLook.Frame, '\0', false).Symbol);
        Assert.Equal('.', new WallCell(FaceLook.Open, '\0', false).Symbol);
        Assert.Equal('c', new WallCell(FaceLook.Wall, 'c', true).Symbol);
    }

    [Fact]
    public void FreeRectanglesKeepOffThingsAndSeams()
    {
        WallMap map = Lu();
        List<(int Row, int Column)> anywhere = map.FreeRects(2, 2, false, true, 50);
        Assert.Contains((0, 0), anywhere);
        Assert.DoesNotContain((2, 2), anywhere);
        // One section: x 718 and y 202 are seams. Row 0 (y 202) and column 4 (x 718) are seam cells; columns 3-4
        // straddle x 718; rows 1-2 (y 201.5 and 201) at columns 5-6 (x 717.5 and 717) lie in one section.
        List<(int Row, int Column)> oneSection = map.FreeRects(2, 2, true, true, 50);
        Assert.DoesNotContain((0, 5), oneSection);
        Assert.DoesNotContain((1, 3), oneSection);
        Assert.DoesNotContain((1, 4), oneSection);
        Assert.Contains((1, 5), oneSection);
    }

    [Fact]
    public void AWindowIsNotFreeWallWhenWallIsRequired()
    {
        WallCell[,] cells = { { new WallCell(FaceLook.Window, '\0', false), new WallCell(FaceLook.Wall, '\0', false) } };
        WallMap map = new WallMap(701, 0.5, 201, cells);
        Assert.Equal(new List<(int, int)> { (0, 1) }, map.FreeRects(1, 1, false, true, 10));
        Assert.Equal(2, map.FreeRects(1, 1, false, false, 10).Count);
    }

    [Fact]
    public void TheSpotFilterNamesEveryFailure()
    {
        SpotRequirements require = new SpotRequirements(true, 1.0, false, true, 0.5, true);
        Assert.Empty(SpotSearch.Filter(new SpotGeometry(4, 0, 0, 1, 1.2, 0), require));
        List<string> failed = SpotSearch.Filter(new SpotGeometry(4, 1, 2, 2, 0.5, 1), require);
        Assert.Equal(5, failed.Count);
        Assert.Equal(1, require.FrontClearCells);
        SpotRequirements lax = new SpotRequirements(false, null, false, false, 0, false);
        Assert.Empty(SpotSearch.Filter(new SpotGeometry(4, 0, 2, 2, 0.0, 0), lax));
    }

    [Fact]
    public void SpotsRankByPenaltyThenDistance()
    {
        List<(int, double)> spots = new List<(int, double)> { (10, 0.5), (0, 3.0), (0, 1.0), (0, 1.0) };
        Assert.Equal(new List<int> { 2, 3, 1, 0 }, SpotSearch.Rank(spots));
    }
}

/// <summary>lint_layout (1.4.3): rule levels, report order, counts, controls.</summary>
public sealed class LintLayoutTests
{
    [Fact]
    public void FindingsComeWarningsFirstInRuleOrder()
    {
        List<LintFinding> findings = new List<LintFinding>
        {
            new LintFinding(LintCodes.ControlsNotOnWall, "c", 1, Vec3.Zero),
            new LintFinding(LintCodes.FloatingRun, "f", 2, Vec3.Zero),
            new LintFinding(LintCodes.RunInDoorKeepOut, "d", 3, Vec3.Zero, 983),
            new LintFinding(LintCodes.FloatingRun, "f2", 4, Vec3.Zero)
        };
        List<LintFinding> ordered = LintReport.Ordered(findings);
        Assert.Equal(new List<string> { "d", "f", "f2", "c" }, ordered.ConvertAll(finding => finding.Message));
        Assert.Equal(ConflictLevel.Info, ordered[3].Level);
        Dictionary<string, int> counts = LintReport.Counts(findings);
        Assert.Equal(2, counts[LintCodes.FloatingRun]);
    }

    [Fact]
    public void EveryRuleHasALevel()
    {
        foreach ((string code, ConflictLevel level) in LintCodes.Rules)
        {
            Assert.Equal(level, LintCodes.LevelOf(code));
        }

        Assert.Throws<System.ArgumentException>(() => LintCodes.LevelOf("nope"));
    }

    [Theory]
    [InlineData("StructureConsole3x3", true)]
    [InlineData("StructureComputerUpright", true)]
    [InlineData("StructureLogicSwitch", true)]
    [InlineData("StructureGasSensor", false)]
    [InlineData(null, false)]
    public void ControlsAreKnownByName(string? prefab, bool has)
    {
        Assert.Equal(has, Controls.Has(prefab));
    }
}
