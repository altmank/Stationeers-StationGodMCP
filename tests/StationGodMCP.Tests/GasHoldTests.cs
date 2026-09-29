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
/// The manual lift of the gas hold (LU 2026-09-29): after a pipe job ends gas_lost, a pipe-touching run is refused
/// until the world is left, unless it passes acknowledge_gas_lost with the id of the job that set the hold. The rule
/// (GasHoldRule), the loss a failed check records (GasCheckView.Loss), the reply's gas_hold (GasHoldReply, GasHoldView)
/// and the tools' input schemas.
/// </summary>
public sealed class GasHoldTests
{
    private static GasLoss Loss(string jobId = "run-7") =>
        new GasLoss(jobId, new List<long> { 171240, 171238 }, 1433.0, "GAS LOST: 1433 mol missing.");

    [Fact]
    public void WithoutAHoldEveryRunGoesOnAndSaysNothing()
    {
        GasHoldVerdict verdict = GasHoldRule.Judge(null, true, null);

        Assert.IsType<GasHoldVerdict.Free>(verdict);
        Assert.False(verdict.Applies);
        Assert.False(verdict.Reportable);
    }

    [Fact]
    public void AnAcknowledgementWithoutAHoldIsNotedAndLiftsNothing()
    {
        GasHoldVerdict verdict = GasHoldRule.Judge(null, true, "run-7");

        Assert.IsType<GasHoldVerdict.Free>(verdict);
        Assert.True(verdict.Reportable);
        Assert.Contains("no gas hold is in place", verdict.Note(GasHoldStage.Started));
    }

    [Fact]
    public void AHeldPipeRunWithoutAcknowledgementIsRefusedNamingTheWayOut()
    {
        GasHoldVerdict.Refusing refusing =
            Assert.IsAssignableFrom<GasHoldVerdict.Refusing>(GasHoldRule.Judge(Loss(), true, null));

        Assert.IsType<GasHoldVerdict.Held>(refusing);
        Assert.Equal("gas_check_failed", refusing.Code);
        Assert.Contains("Job run-7's gas check failed: GAS LOST: 1433 mol missing.", refusing.Message);
        Assert.Contains("acknowledge_gas_lost: \"run-7\"", refusing.Message);
        Assert.Contains("ask the user", refusing.Message);
    }

    [Fact]
    public void AnotherJobsIdIsRefusedNamingTheHoldingJobAndItsLoss()
    {
        GasHoldVerdict.Refusing refusing =
            Assert.IsAssignableFrom<GasHoldVerdict.Refusing>(GasHoldRule.Judge(Loss(), true, "run-3"));

        Assert.IsType<GasHoldVerdict.Mismatch>(refusing);
        Assert.Equal("gas_hold_mismatch", refusing.Code);
        Assert.Contains("names \"run-3\"", refusing.Message);
        Assert.Contains("job run-7 lost 1433 mol from pipe network(s) 171240, 171238", refusing.Message);
        Assert.Contains("acknowledge_gas_lost: \"run-7\"", refusing.Message);
    }

    [Theory]
    [InlineData("run-7")]
    [InlineData(" RUN-7 ")]
    public void TheHoldingJobsIdLiftsTheHold(string given)
    {
        GasLoss loss = Loss();
        GasHoldVerdict verdict = GasHoldRule.Judge(loss, true, given);

        GasHoldVerdict.Lifting lifting = Assert.IsType<GasHoldVerdict.Lifting>(verdict);
        Assert.Same(loss, lifting.Hold);
        Assert.True(lifting.Applies);
    }

    [Fact]
    public void ARunThatTouchesNoPipeIsNeverHeldAndLiftsNothing()
    {
        GasHoldVerdict quiet = GasHoldRule.Judge(Loss(), false, null);
        GasHoldVerdict acknowledged = GasHoldRule.Judge(Loss(), false, "run-7");

        Assert.IsType<GasHoldVerdict.Unaffected>(quiet);
        Assert.False(quiet.Reportable);
        Assert.IsType<GasHoldVerdict.Unaffected>(acknowledged);
        Assert.Contains("does not apply", acknowledged.Note(GasHoldStage.Started));
        Assert.Contains("acknowledge_gas_lost was not used", acknowledged.Note(GasHoldStage.Started));
    }

    [Fact]
    public void ASecondLossHoldsAgainUnderItsOwnJob()
    {
        GasHoldVerdict verdict = GasHoldRule.Judge(Loss("upgrade-12"), true, "run-7");

        GasHoldVerdict.Mismatch mismatch = Assert.IsType<GasHoldVerdict.Mismatch>(verdict);
        Assert.Contains("acknowledge_gas_lost: \"upgrade-12\"", mismatch.Message);
    }

    [Fact]
    public void AStartedAcknowledgementRepeatsTheLoss()
    {
        GasHoldView view = GasHoldView.Of(GasHoldRule.Judge(Loss(), true, "run-7"), GasHoldStage.Started);

        Assert.Equal("acknowledged", view.Status);
        Assert.True(view.Lifted);
        Assert.Equal("run-7", view.HeldByJobId);
        Assert.StartsWith("GAS LOSS ACKNOWLEDGED: job run-7 lost 1433 mol from pipe network(s) 171240, 171238",
            view.Note);
        Assert.Equal(1433.0, view.Loss!.MissingMol);
        Assert.Equal(2, view.Loss.Networks.Count);
    }

    [Fact]
    public void ADryRunSaysWhatAcknowledgingWouldLiftAndLiftsNothing()
    {
        GasHoldView acknowledged = GasHoldView.Of(GasHoldRule.Judge(Loss(), true, "run-7"), GasHoldStage.DryRun);
        GasHoldView held = GasHoldView.Of(GasHoldRule.Judge(Loss(), true, null), GasHoldStage.DryRun);

        Assert.False(acknowledged.Lifted);
        Assert.True(acknowledged.Applies);
        Assert.Contains("would accept that loss, lift the hold", acknowledged.Note);
        Assert.Contains("a dry run lifts nothing", acknowledged.Note);
        Assert.Equal("held", held.Status);
        Assert.True(held.Applies);
        Assert.StartsWith("A real run would be refused (gas_check_failed)", held.Note);
    }

    [Fact]
    public void AQueuedOrUnstartedRunDoesNotClaimTheLift()
    {
        GasHoldVerdict verdict = GasHoldRule.Judge(Loss(), true, "run-7");

        Assert.False(GasHoldView.Of(verdict, GasHoldStage.Queued).Lifted);
        Assert.Contains("lifted when this run starts", verdict.Note(GasHoldStage.Queued));
        Assert.Contains("was not lifted", verdict.Note(GasHoldStage.NotStarted));
    }

    [Fact]
    public void AFailedCheckRecordsItsNetworksAndMissingMoles()
    {
        List<NetworkGas> before = new List<NetworkGas>
        {
            new NetworkGas(10, Methane(100), 20, new long[] { 1, 2 }, new long[0]),
            new NetworkGas(99, Methane(7), 5, new long[] { 90 }, new long[0])
        };
        List<NetworkGas> after = new List<NetworkGas>
        {
            new NetworkGas(10, Methane(60), 30, new long[] { 1, 2, 3 }, new long[0]),
            new NetworkGas(99, Methane(7), 5, new long[] { 90 }, new long[0])
        };
        GasCheckView check = GasCheckView.Of(GasAudit.Of(before, after, GasTolerance.Default), new List<GasRefill>(),
            new List<long>());

        GasLoss loss = check.Loss("run-4");

        Assert.False(check.Ok);
        Assert.Equal("run-4", loss.JobId);
        Assert.Equal(new List<long> { 10 }, loss.Networks);
        Assert.Equal(40.0, loss.MissingMol, 9);
        Assert.Contains("acknowledge_gas_lost", check.Summary);
        Assert.Equal("job run-4 lost 40 mol from pipe network(s) 10", loss.Describe());
    }

    [Fact]
    public void TheReplyKeepsALiftOverTheDryRunsBeforeIt()
    {
        GasHoldReply.Begin();
        GasHoldVerdict verdict = GasHoldRule.Judge(Loss(), true, "run-7");
        GasHoldReply.Record(verdict, GasHoldStage.DryRun);
        GasHoldReply.Record(verdict, GasHoldStage.Started);
        GasHoldReply.Record(GasHoldRule.Judge(null, true, "run-7"), GasHoldStage.Queued);

        Assert.Null(GasHoldReply.Refusal);
        GasHoldView? view = GasHoldReply.Take();
        Assert.True(view!.Lifted);
        Assert.Null(GasHoldReply.Take());
    }

    [Fact]
    public void TheReplyRemembersARefusalAmongItsDryRuns()
    {
        GasHoldReply.Begin();
        GasHoldReply.Record(GasHoldRule.Judge(Loss(), false, "run-7"), GasHoldStage.DryRun);
        GasHoldReply.Record(GasHoldRule.Judge(Loss(), true, null), GasHoldStage.DryRun);

        Assert.IsType<GasHoldVerdict.Held>(GasHoldReply.Refusal);
        Assert.Equal("held", GasHoldReply.Take()!.Status);
        Assert.Null(GasHoldReply.Refusal);
    }

    [Fact]
    public void TheReplyCarriesGasHoldInSnakeCase()
    {
        GasHoldView view = GasHoldView.Of(GasHoldRule.Judge(Loss(), true, "run-7"), GasHoldStage.Started);

        JObject reply = Assert.IsType<JObject>(GasHoldReply.Attach(new { status = "waiting" }, view));

        Assert.Equal("waiting", reply.Value<string>("status"));
        JToken hold = reply["gas_hold"]!;
        Assert.True(hold.Value<bool>("lifted"));
        Assert.Equal("run-7", hold.Value<string>("held_by_job_id"));
        Assert.Equal(1433.0, hold["loss"]!.Value<double>("missing_mol"));
        Assert.Equal("171240", hold["loss"]!["networks"]![0]!.ToString());
    }

    [Theory]
    [InlineData("place_pipes", """{"waypoints":[[1,2,3],[1,2,5]],"grade":"gas","acknowledge_gas_lost":"run-7"}""")]
    [InlineData("remove_pipes", """{"reference_ids":["5"],"acknowledge_gas_lost":"run-7"}""")]
    [InlineData("upgrade_pipes", """{"network_id":"5","acknowledge_gas_lost":"run-7"}""")]
    [InlineData("clean_pipes", """{"network_id":"5","operations":["remove_dead_ends"],"acknowledge_gas_lost":"run-7"}""")]
    [InlineData("place_structure", """{"prefab":"StructureFrameIron","at":[1,2,3],"acknowledge_gas_lost":"run-7"}""")]
    [InlineData("remove_structure", """{"reference_ids":["5"],"acknowledge_gas_lost":"run-7"}""")]
    [InlineData("undo_job", """{"job_id":"place-9","acknowledge_gas_lost":"run-7"}""")]
    public void EveryHeldToolDeclaresTheAcknowledgement(string tool, string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);

        Assert.DoesNotContain(ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement),
            problem => problem.Contains("acknowledge_gas_lost"));
    }

    [Theory]
    [InlineData("place_cables")]
    [InlineData("upgrade_cables")]
    [InlineData("replace_walls")]
    public void AToolTheHoldNeverRefusesDoesNotTakeIt(string tool)
    {
        using JsonDocument document = JsonDocument.Parse("""{"acknowledge_gas_lost":"run-7"}""");

        Assert.Contains(ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement),
            problem => problem.StartsWith("Unknown argument 'acknowledge_gas_lost'"));
    }

    private static GasMix Methane(double mol) => new GasMix(new[] { mol }, new[] { mol * 10 });
}
