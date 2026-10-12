#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Scheduling;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// drop_job: a waiting job is taken out of the line and its later poll answers the drop; a running job, an ended one
/// and an unknown id are refused (job_not_waiting, job_not_found) and nothing leaves the line. On the wire the method
/// is a catalogued protocol method of write class, answered on the main thread with {job_id, dropped} in the reply
/// envelope.
/// </summary>
public sealed class JobDropTests
{
    private const int Kept = 16;

    [Fact]
    public void AWaitingJobLeavesTheLineAndHandsBackWhatWouldHaveStartedIt()
    {
        JobLine<string> line = Line("upgrade-2", "upgrade-3", "upgrade-4");

        string start = JobDrops.Take(line, "upgrade-3", "upgrade-1", false, Kept);

        Assert.Equal("start upgrade-3", start);
        Assert.Null(line.PositionOf("upgrade-3"));
        Assert.Equal(2, line.PositionOf("upgrade-4"));
    }

    [Fact]
    public void TheRunningJobIsRefusedAsStartedAndTheLineIsUntouched()
    {
        JobLine<string> line = Line("upgrade-2");

        ApiException refused = Assert.Throws<ApiException>(() => JobDrops.Take(line, "upgrade-1", "upgrade-1", false, Kept));

        Assert.Equal("job_not_waiting", refused.Code);
        Assert.StartsWith("Job upgrade-1 has started; it cannot be dropped.", refused.Message);
        Assert.Equal(1, line.Count);
    }

    [Fact]
    public void AnEndedJobIsRefusedAsEnded()
    {
        ApiException refused = Assert.Throws<ApiException>(() =>
            JobDrops.Take(Line("upgrade-2"), "upgrade-0", "upgrade-1", true, Kept));

        Assert.Equal("job_not_waiting", refused.Code);
        Assert.StartsWith("Job upgrade-0 has ended;", refused.Message);
    }

    [Fact]
    public void AnUnknownIdIsNotFoundWordedAsAPollIs()
    {
        ApiException refused = Assert.Throws<ApiException>(() =>
            JobDrops.Take(Line("upgrade-2"), "upgrade-9", null, false, Kept));

        Assert.Equal("job_not_found", refused.Code);
        Assert.Equal("No job upgrade-9 is running, queued or among the last 16.", refused.Message);
    }

    [Fact]
    public void TheDroppedJobsPollAnswersStatusRefusedWithErrorDropped()
    {
        JObject view = JObject.Parse(ApiJson.WriteShared(new JobDroppedView("upgrade-3", "upgrade_pipes", JobDrops.Dropped())));

        Assert.Equal("refused", (string?)view["status"]);
        Assert.Equal("dropped", (string?)view["error"]!["code"]);
        Assert.Equal("Dropped from the queue before it started; nothing was changed.", (string?)view["error"]!["message"]);
    }

    [Fact]
    public void DropJobIsAProtocolMethodOfWriteClassAnsweredOnTheMainThread()
    {
        Assert.Contains(JobDropCalls.DropJobMethod, ProtocolMethods.Names);
        Assert.True(ProtocolMethods.RunsOnMainThread("drop_job"));
        Assert.True(ProtocolMethods.RunsOnMainThread("subscribe"));
        Assert.False(ProtocolMethods.RunsOnMainThread("catalogue"));
        Assert.False(ProtocolMethods.RunsOnMainThread("game_clock"));
        Assert.False(ProtocolMethods.RunsOnMainThread(null));

        Assert.True(TestCatalogue.File.Value.Catalogue.TryGet("drop_job", out CatalogueMethod? method));
        CallProfile profile = CallProfile.Of(method!, new JObject { ["job_id"] = "upgrade-3" });
        Assert.Equal(MethodClass.Write, profile.Class);
        Assert.True(profile.IsOrdered);
        Assert.Equal(CostClass.Instant, profile.Cost);
    }

    [Fact]
    public async Task OnTheWireAWaitingJobIsDroppedAndTheReplyCarriesTheEnvelope()
    {
        using PipeRig rig = Rig(out List<string> dropped);
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("1", "drop_job", new JObject { ["job_id"] = "upgrade-3" });

        Assert.True((bool)reply["ok"]!, reply.ToString());
        Assert.Equal(new JObject { ["job_id"] = "upgrade-3", ["dropped"] = true }, reply["result"]);
        Assert.Equal(42L, (long)reply["frame"]!);
        Assert.NotNull(reply["elapsed_ms"]);
        Assert.NotNull(reply["queue_ms"]);
        Assert.Equal(new[] { "upgrade-3" }, dropped);
    }

    [Theory]
    [InlineData("upgrade-1", "job_not_waiting")]
    [InlineData("upgrade-0", "job_not_waiting")]
    [InlineData("upgrade-9", "job_not_found")]
    public async Task OnTheWireARunningEndedOrUnknownJobIsRefused(string jobId, string code)
    {
        using PipeRig rig = Rig(out List<string> dropped);
        using V2Client client = await V2Client.Connect(rig);

        JObject reply = client.Call("1", "drop_job", new JObject { ["job_id"] = jobId });

        Assert.False((bool)reply["ok"]!, reply.ToString());
        Assert.Equal(code, (string?)reply["error"]!["code"]);
        Assert.Equal(42L, (long)reply["frame"]!);
        Assert.Empty(dropped);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMissingJobIdIsAnInvalidArgument(bool strict)
    {
        using PipeRig rig = Rig(out List<string> dropped, strict);
        using V2Client client = await V2Client.Connect(rig);

        JObject missing = client.Call("1", "drop_job", new JObject());
        JObject extra = client.Call("2", "drop_job", new JObject { ["job_id"] = "upgrade-3", ["force"] = true });

        Assert.Equal("invalid_argument", (string?)missing["error"]!["code"]);
        Assert.Equal("invalid_argument", (string?)extra["error"]!["code"]);
        Assert.Empty(dropped);
    }

    private static JobLine<string> Line(params string[] ids)
    {
        JobLine<string> line = new JobLine<string>(8);
        foreach (string id in ids)
        {
            line.Add(id, "start " + id);
        }

        return line;
    }

    // The job slot as HeldTickJobs.DropWaiting sees it: upgrade-1 runs, upgrade-2..4 wait, upgrade-0 has ended.
    private static PipeRig Rig(out List<string> dropped, bool strict = true)
    {
        JobLine<string> line = Line("upgrade-2", "upgrade-3", "upgrade-4");
        HashSet<string> ended = new HashSet<string>(StringComparer.Ordinal) { "upgrade-0" };
        List<string> taken = new List<string>();
        dropped = taken;
        JobDropCalls drops = new JobDropCalls(id =>
        {
            JobDrops.Take(line, id, "upgrade-1", ended.Contains(id), Kept);
            taken.Add(id);
            return new JobDropReplyView(id);
        });

        PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, strictArguments: strict));
        rig.Mod.CallHook = (call, queueMs) => JobDropCalls.Handles(call.Request.Method)
            ? new CallOutcome(drops.Run(call, queueMs, 42), call.Request.Method)
            : null;
        return rig;
    }
}
