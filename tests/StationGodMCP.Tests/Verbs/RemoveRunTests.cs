#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// remove without the game: a cable, pipe or chute call names its pieces one way, by id, waypoints or cells; two ways
/// is a problem, none is missing_target; a blank acknowledge_gas_lost is a problem; structure's gas_to.
/// </summary>
public sealed class RemoveRunTests
{
    private static string? Form(string json, List<OptionProblem> problems, out VerbError? missing)
    {
        RemovePipeOptions options = RemovePipeOptions.Bind(new OptionReader(RemovePipeOptions.Spec, JObject.Parse(json)));
        return RemoveRunOrders.Form(options.Selector.Ids != null, options.Waypoints, options.Cells, "remove pipe",
            "pipe", problems, out missing);
    }

    [Theory]
    [InlineData("""{"id": ["3763565", "3763566"]}""", "id")]
    [InlineData("""{"waypoints": [[0, 0, 0], [2, 0, 0]]}""", "waypoints")]
    [InlineData("""{"cells": [[0, 0, 0], [0.5, 0, 0]]}""", "cells")]
    public void APipeIsNamedByIdWaypointsOrCells(string json, string form)
    {
        List<OptionProblem> problems = new();

        Assert.Equal(form, Form(json, problems, out VerbError? missing));
        Assert.Null(missing);
        Assert.Empty(problems);
    }

    [Fact]
    public void TwoWaysIsAProblemAndNoneIsMissingTarget()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Form("""{"id": "1", "cells": [[0, 0, 0]]}""", problems, out VerbError? missing));
        Assert.Null(missing);
        Assert.Equal("cells", Assert.Single(problems).Path);

        problems.Clear();
        Assert.Null(Form("{}", problems, out missing));
        Assert.Empty(problems);
        Assert.Equal("missing_target", missing!.Code);
        Assert.Equal(new[] { "id", "waypoints", "cells" }, missing.Data!["options"]!.Select(option => (string)option!));
    }

    [Fact]
    public void ABlankGasAcknowledgementIsAProblem()
    {
        List<OptionProblem> problems = new();

        GasHoldArgs.Check("  ", problems);
        GasHoldArgs.Check("run-7", problems);
        GasHoldArgs.Check(null, problems);

        Assert.Equal("acknowledge_gas_lost", Assert.Single(problems).Path);
    }

    [Fact]
    public void RemoveStructuresGasToIsConnectedOrANetwork()
    {
        List<OptionProblem> problems = new();

        Assert.Null(GasTarget.Of(null, problems));
        Assert.Same(GasTarget.AnyConnected, GasTarget.Of(" Connected ", problems));
        Assert.Equal(5012345L, GasTarget.Of("5012345", problems)!.Network!.Value.Value);
        Assert.Empty(problems);

        Assert.Null(GasTarget.Of("tank", problems));
        Assert.Equal("gas_to", Assert.Single(problems).Path);
    }
    // remove structure records a burnt cable it takes down as burnt (JobSnapshots.Record's burnt list): undo leaves it
    // gone and puts back the rest.
    [Fact]
    public void UndoOfRemoveStructureLeavesABurntCableGoneAndRestoresTheRest()
    {
        ThingSnapshot burnt = new ThingSnapshot(621, "StructureCableStraightBurnt", new Vec3(-1126.5, 232, -701),
            null, 0, null, null, burnt: true);
        ThingSnapshot locker = new ThingSnapshot(622, "StructureStorageLocker", new Vec3(-1124, 232, -701),
            CubeRotation.Identity, 0, null);
        JobFacts job = new JobFacts("remove-1", "remove_structure", "applied", new List<(long, string?)>(),
            new List<long> { 621, 622 }, new Dictionary<long, ThingSnapshot> { [621] = burnt, [622] = locker });

        UndoPlan plan = UndoPlanner.Plan(job, _ => null);

        Assert.True(plan.Ready);
        Assert.Equal(new List<long> { 622 }, plan.RestoreStructures.ConvertAll(snapshot => snapshot.Id));
        Assert.Contains(plan.Notes, note => note.Contains("621") && note.Contains("burnt"));
    }
}
