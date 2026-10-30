#nullable enable

using System;
using Lattice.Testing;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink.Json;
using StationGodMCP.LatticeLink.Model;
using Xunit;
using L = Lattice.Api;

namespace StationGodMCP.Tests.LatticeLink;

/// <summary>
/// Lattice 0.22's builders in find and write lattice's JSON: a plan's colour and builders read from JSON and written
/// back, a builder's word on itself (area, word, supply, its answer to a plan) read and written, and a piece's placing.
/// </summary>
[Collection(LatticeWorldCollection.Name)]
public sealed class BuilderJsonTests
{
    [Fact]
    public void APlanTakesItsColourAndABuilderFromJsonAndWritesThemBack()
    {
        using TestWorld world = Start();
        using L.IExecutorHandle dock = Register(world, "dock");
        LatticeIn read = new LatticeIn(world.System);

        L.ChangeSet made = Applied(world, read.Command(JObject.Parse("""
            {"create_plan": {"name": "Hall", "colour": "#ff8800"}}
            """), "command")!);
        long plan = Assert.Single(made.PlansOpened).Id.Value;
        Applied(world, read.Command(JObject.Parse("""
            {"edit_plan": {"plan": @PLAN, "edit": {"assign": "stationgod/dock"}}}
            """.Replace("@PLAN", plan.ToString())), "command")!);

        Assert.Empty(read.Problems);
        L.Plan found = world.System.Plans.Find(new L.PlanId(plan)).TryGet(out L.Plan p) ? p : throw new InvalidOperationException("no plan");
        JObject written = new LatticeOut(world.System).Plan(found);
        Assert.Equal(new JArray("stationgod/dock"), written["build_by"]);
        Assert.Equal(new JArray(1.0, 0.533, 0.0, 1.0), written["colour"]);
    }

    [Fact]
    public void ABuildersWordOnItselfReadsAndWritesBack()
    {
        using TestWorld world = Start();
        using L.IExecutorHandle dock = Register(world, "dock");
        LatticeIn read = new LatticeIn(world.System);

        L.Option<L.BuilderArea>? area = read.Area(JObject.Parse("""
            {"sphere": {"at": [1, 2, 3], "build_reach": 8, "source_range": 12}}
            """), "area");
        L.SupplyReport? supply = read.Supply(JObject.Parse("""
            {"reaches": [{"item": "ItemSteelFrames", "count": 30}], "toward": [{"plan": @PLAN, "items": [{"item": 77, "count": 2}]}]}
            """.Replace("@PLAN", world.System.Plans.Default.Value.ToString())), "supply");
        Assert.Empty(read.Problems);
        dock.Locate(area!.Value);
        Assert.True(dock.Offer(supply!).TryGet(out L.Unit _));

        Assert.True(world.System.Executors.Find(Id("dock")).TryGet(out L.Builder builder));
        JObject written = new LatticeOut(world.System).Builder(builder);
        Assert.Equal("stationgod/dock", (string?)written["id"]);
        Assert.Equal(8.0, (double)written["area"]!["sphere"]!["build_reach"]!);
        Assert.Equal(12.0, (double)written["area"]!["sphere"]!["source_range"]!);
        Assert.Equal(30, (int)written["supply"]!["reaches"]![0]!["count"]!);
        Assert.Equal(77, (int)written["supply"]!["toward"]![0]!["items"]![0]!["item"]!);
        Assert.Equal("idle", (string?)written["state"]);
    }

    [Fact]
    public void AnAssignedBuilderIsAskedUntilItAnswers()
    {
        using TestWorld world = Start();
        using L.IExecutorHandle dock = Register(world, "dock");
        LatticeIn read = new LatticeIn(world.System);
        L.PlanId plan = world.System.Plans.Default;
        Applied(world, read.Command(new JObject
        {
            ["edit_plan"] = new JObject { ["plan"] = plan.Value, ["edit"] = new JObject { ["assign"] = "stationgod/dock" } }
        }, "command")!);

        Assert.Equal("asked", (string?)LatticeOut.Assignment(Assert.Single(world.System.Executors.AssignedTo(plan)))["answer"]);
        Assert.True(dock.Answer(plan, new L.AssignmentAnswer.Accepted()).TryGet(out L.Unit _));
        JObject accepted = LatticeOut.Assignment(Assert.Single(world.System.Executors.AssignedTo(plan)));
        Assert.Equal("stationgod/dock", (string?)accepted["builder"]);
        Assert.Equal("accepted", (string?)accepted["answer"]);
    }

    [Fact]
    public void AnUnknownBareBuilderIsAProblem()
    {
        using TestWorld world = Start();
        LatticeIn read = new LatticeIn(world.System);

        Assert.Null(read.Builder(new JValue("dock"), "args.builder"));
        Assert.Contains("owner/name", Assert.Single(read.Problems));
    }

    [Fact]
    public void APieceWritesHowTheGamePlacesIt()
    {
        using TestWorld world = Start();
        L.PrefabKey frame = TestCatalogue.Hand.Key("StructureFrame").Reduce(default(L.PrefabKey));
        Assert.True(world.System.Catalogue.Piece(frame).TryGet(out L.PieceEntry entry));

        Assert.Equal("grid", (string?)new LatticeOut(world.System).PieceEntry(entry)["placing"]);
    }

    private static L.ExecutorId Id(string name) => LatticeNames.Must(L.ExecutorId.Create(LatticeNames.Mod, name));

    private static L.IExecutorHandle Register(TestWorld world, string name)
    {
        L.ExecutorInfo info = new L.ExecutorInfo(Id(name),
            LatticeNames.Must(L.DisplayName.Create(name)));
        return world.System.Executors.Register(info).TryGet(out L.IExecutorHandle handle)
            ? handle
            : throw new InvalidOperationException("not registered");
    }

    private static TestWorld Start() =>
        TestWorld.Start(TestScenario.Empty).Match(static world => world,
            static why => throw new InvalidOperationException(LatticeText.Of(why)));

    private static L.ChangeSet Applied(TestWorld world, L.Command command)
    {
        L.Submission sent = world.System.Commands.Submit(L.CommandId.New(), command, LatticeNames.Agent);
        L.Submission.Answered answered = Assert.IsType<L.Submission.Answered>(sent);
        return answered.Outcome is L.Outcome.Applied applied
            ? applied.Changes
            : throw new InvalidOperationException(LatticeText.Of(((L.Outcome.Refused)answered.Outcome).Why));
    }
}
