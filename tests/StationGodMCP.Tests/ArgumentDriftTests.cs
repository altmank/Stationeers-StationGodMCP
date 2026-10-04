#nullable enable

using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Shaping;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// A reader several methods share never counts a read of an argument the calling method does not declare
/// (mod_info.runtime.catalogue_drift): Reject skips such names, and shared readers ask Declares first.
/// </summary>
public sealed class ArgumentDriftTests
{
    private static Args Held(string method, JObject parameters, params string[] declared) =>
        new Args(parameters, new ArgumentNames(method, declared), ShapeRequest.None);

    private static long Reads(string method, string argument) =>
        ArgumentDrift.Counts.Snapshot().Where(count => count.Method == method && count.Argument == argument)
            .Sum(count => count.Reads);

    [Fact]
    public void RejectSkipsANameTheMethodDoesNotDeclare()
    {
        const string method = "drift_test_reject";
        Args args = Held(method, new JObject { ["job_id"] = "j1" }, "job_id", "dry_run");

        args.Reject("job_id", "dry_run", "to", GasHoldVerdict.AcknowledgeArgument);

        Assert.Equal(0, Reads(method, "to"));
        Assert.Equal(0, Reads(method, GasHoldVerdict.AcknowledgeArgument));
    }

    [Fact]
    public void RejectStillRefusesADeclaredArgumentOfAnotherForm()
    {
        Args args = Held("drift_test_refuse", new JObject { ["job_id"] = "j1", ["dry_run"] = true }, "job_id", "dry_run");

        ApiException refused = Assert.Throws<ApiException>(() => args.Reject("job_id", "dry_run"));
        Assert.Contains("dry_run", refused.Message);
    }

    [Fact]
    public void TheGasAcknowledgementIsNotReadForAMethodWithoutIt()
    {
        const string method = "drift_test_cables";
        Args args = Held(method, new JObject(), "network_id");

        Assert.Null(GasHoldArgs.Acknowledgement(args));
        Assert.Equal(0, Reads(method, GasHoldVerdict.AcknowledgeArgument));
    }

    [Fact]
    public void TheGasAcknowledgementIsReadForAMethodThatTakesIt()
    {
        Args args = Held("drift_test_pipes", new JObject { [GasHoldVerdict.AcknowledgeArgument] = "job-7" },
            GasHoldVerdict.AcknowledgeArgument);

        Assert.Equal("job-7", GasHoldArgs.Acknowledgement(args));
    }

    [Fact]
    public void AnUndeclaredReadIsStillCounted()
    {
        const string method = "drift_test_counted";
        Args args = Held(method, new JObject(), "network_id");

        Assert.False(args.Has("min"));

        Assert.Equal(1, Reads(method, "min"));
    }

    [Fact]
    public void ParametersHeldToNoCatalogueEntryDeclareEverything() =>
        Assert.True(new Args(new JObject(), ShapeRequest.None).Declares("anything"));
}
