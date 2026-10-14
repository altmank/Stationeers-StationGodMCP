#nullable enable

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests;

/// <summary>
/// The facts a job_ended event carries, taken from the job's final reply: a run's log lists, a structure job's result
/// lists, the error wherever the reply keeps it, and nothing of the reports (preflight, final check, verification),
/// which keeps a large build's event small.
/// </summary>
public sealed class JobEndFactsTests
{
    private readonly ITestOutputHelper _output;

    public JobEndFactsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void AStructureJobsFactsAreItsResultsPlacedAndRemoved()
    {
        BuildLog log = new BuildLog();
        log.Placed.Add(new BuiltPieceView(0, new ThingView(new ThingId(7), "StructureWallIron", "Wall")));
        log.Removed.Add(new BuiltPieceView(1, new ThingView(new ThingId(5), "StructureFrame", "Frame")));
        BuildJobView job = new BuildJobView("place-3", "build structure", "applied_with_differences",
            new { preflight = "kept out" },
            new BuildJobResult(new { final_check = "kept out" }, log,
                new List<BuildCheckView> { new BuildCheckView(0, new ThingId(7), new List<string> { "moved" }) },
                new ErrorView("stopped", "A step failed.")));

        JObject facts = JobEndFacts.Of(Wire(job));

        Assert.Equal(new JObject
        {
            ["job_id"] = "place-3", ["tool"] = "build structure", ["status"] = "applied_with_differences",
            ["error"] = new JObject { ["code"] = "stopped", ["message"] = "A step failed." },
            ["removed"] = new JArray("5"),
            ["placed"] = new JArray(new JObject { ["reference_id"] = "7", ["prefab_name"] = "StructureWallIron" })
        }, facts);
    }

    [Fact]
    public void AJobThatChangedNothingCarriesNoLists()
    {
        JObject facts = JobEndFacts.Of(Wire(new RunJobView("run-2", "build cable", "refused", null,
            new RunJobResultView(null, new RunLogView(), null, null))));

        Assert.Equal(new JObject { ["job_id"] = "run-2", ["tool"] = "build cable", ["status"] = "refused" }, facts);
    }

    [Fact]
    public void ALargeRunsEventStaysSmall()
    {
        const int pieces = 500;
        RunLogView log = new RunLogView();
        for (int index = 0; index < pieces; index++)
        {
            ThingId id = new ThingId(1_000_000_000_000L + index);
            log.PlacedPieces.Add(new ThingView(id, "StructurePipeStraight", "Pipe (Straight)"));
            log.AddCreated("run", id);
        }

        RunJobView job = new RunJobView("run-9", "build pipe", "applied", null,
            new RunJobResultView(null, log, null, null));
        int whole = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(job, ApiJson.Settings));
        int facts = Encoding.UTF8.GetByteCount(JobEndFacts.Of(Wire(job)).ToString(Formatting.None));

        _output.WriteLine($"{pieces} pieces: final view {whole} bytes, job_ended facts {facts} bytes");
        Assert.True(facts < whole, $"facts {facts} bytes, final view {whole} bytes");
        Assert.True(facts < 48 * 1024, $"facts {facts} bytes");
    }

    private static JObject Wire(object view) => JObject.FromObject(view, ApiJson.Fresh());
}
