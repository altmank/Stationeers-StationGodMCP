using System.Text.Json.Nodes;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Runs;

/// <summary>
/// A finished run's footer stays short (LIVE 2026-10-24: 84 report-only build probes made a 12 KB reply): reported rows
/// past the first 10 are counted, not listed, yet seen; warnings alike but for their numbers are told once with a count;
/// inspect job whole lists everything.
/// </summary>
public sealed class RunFooterTests
{
    private static RunRecord ReportRun(int rows, int warnings)
    {
        RunRecord run = new("script-7", "...", confirm: false, stream: 1, world: null);
        run.State = RunState.Report;
        for (int index = 0; index < rows; index++)
        {
            string id = (5000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            run.Reported.Add(new ListedRow("build structure", "[undo]",
                new JsonObject { ["id"] = id, ["text"] = $"[{id}, StructureConsole, planned]" }));
        }

        for (int index = 0; index < warnings; index++)
        {
            run.Warnings.Add($"controls_blocked: the console at ({index}, 96, 911) faces a wall");
        }

        run.Warnings.Add("seam: a frame seam under the floor");
        return run;
    }

    [Fact]
    public void ReportedRowsPastTenAreCountedAndStillSeen()
    {
        RunRecord run = ReportRun(84, 0);
        SeenSet seen = new();

        string text = RunText.Final(run, seen);

        Assert.Contains("REPORTED build structure x84 [undo]:", text);
        Assert.Contains("[5009, StructureConsole, planned]", text);
        Assert.DoesNotContain("[5010, StructureConsole", text);
        Assert.Contains("... 74 more (seen too; every row: inspect {what: \"job\", id: \"script-7\", whole: true})", text);
        Assert.True(seen.Contains("5083"));
    }

    [Fact]
    public void WarningsAlikeButForNumbersAreToldOnce()
    {
        string text = RunText.Final(ReportRun(1, 24), null);

        Assert.Contains("WARNINGS: controls_blocked: the console at (0, 96, 911) faces a wall (x24 alike); " +
                        "seam: a frame seam under the floor\n", text);
    }

    [Fact]
    public void WholeListsEveryRow()
    {
        string text = RunText.Final(ReportRun(84, 0), null, whole: true);

        Assert.Contains("[5083, StructureConsole, planned]", text);
        Assert.DoesNotContain("more (seen too", text);
    }
}
