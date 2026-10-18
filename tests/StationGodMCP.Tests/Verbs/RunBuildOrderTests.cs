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
/// build cable, pipe and chute without the game: the run is named one way (waypoints, cells, piece or pieces), a
/// second is a problem and none is missing_target naming all four; a pipe must name its grade where a cable defaults
/// heavy and a chute chute; allow's words; remove_ids, assume_removed, allow_bridge, join_to, root and join_trunk;
/// rows, include, verbose, queue, confirm and a blank acknowledge_gas_lost.
/// </summary>
public sealed class RunBuildOrderTests
{
    private static RunBuildOrder? Cable(string json, List<OptionProblem> problems, out VerbError? missing) =>
        RunBuildOrder.Of(BuildCableOptions.Bind(new OptionReader(BuildCableOptions.Spec, JObject.Parse(json))),
            problems, out missing);

    private static RunBuildOrder? Pipe(string json, List<OptionProblem> problems, out VerbError? missing) =>
        RunBuildOrder.Of(BuildPipeOptions.Bind(new OptionReader(BuildPipeOptions.Spec, JObject.Parse(json))),
            problems, out missing);

    private static RunBuildOrder? Chute(string json, List<OptionProblem> problems, out VerbError? missing) =>
        RunBuildOrder.Of(BuildChuteOptions.Bind(new OptionReader(BuildChuteOptions.Spec, JObject.Parse(json))),
            problems, out missing);

    private static List<string> Ids(IReadOnlyList<NetworkTarget> targets)
    {
        List<string> ids = new List<string>(targets.Count);
        foreach (NetworkTarget target in targets)
        {
            ids.Add(target.Id);
        }

        return ids;
    }

    [Theory]
    [InlineData("""{"waypoints": [[0, 0, 0], [2, 0, 0]]}""", "waypoints")]
    [InlineData("""{"cells": [[0, 0, 0], [0.5, 0, 0]]}""", "cells")]
    [InlineData("""{"piece": {"at": [0, 0, 0], "ends": ["+x", "-x"]}}""", "piece")]
    [InlineData("""{"pieces": [{"at": [0, 0, 0], "ends": ["+x"]}]}""", "pieces")]
    public void TheRunIsNamedOneWay(string json, string form)
    {
        List<OptionProblem> problems = new();

        RunBuildOrder order = Cable(json, problems, out VerbError? missing)!;

        Assert.Equal(form, order.Form);
        Assert.Null(missing);
        Assert.Empty(problems);
        Assert.Equal("cable", order.Kind);
        Assert.Equal("build cable", order.Label);
        Assert.Equal("[undo]", order.Mark);
    }

    [Fact]
    public void TwoWaysIsAProblemAndNoneIsMissingTarget()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Cable("""{"waypoints": [[0, 0, 0]], "cells": [[0, 0, 0]]}""", problems, out VerbError? missing));
        Assert.Null(missing);
        Assert.Equal("cells", Assert.Single(problems).Path);

        problems.Clear();
        Assert.Null(Cable("{}", problems, out missing));
        Assert.Empty(problems);
        Assert.Equal("missing_target", missing!.Code);
        Assert.Equal("build cable: name the run by waypoints, cells, piece or pieces.", missing.Message);
        Assert.Equal(new[] { "waypoints", "cells", "piece", "pieces" },
            missing.Data!["options"]!.Values<string>());
    }

    [Fact]
    public void APipeNamesItsGradeWhereACableAndAChuteDefault()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Pipe("""{"cells": [[0, 0, 0]]}""", problems, out VerbError? missing));
        Assert.Null(missing);
        Assert.Equal("grade", Assert.Single(problems).Path);
        Assert.Equal("is required: gas, liquid, insulated_gas, insulated_liquid",
            Assert.Single(problems).Problem);

        problems.Clear();
        Assert.Equal("gas", Pipe("""{"grade": "gas", "cells": [[0, 0, 0]]}""", problems, out _)!.Grade);
        Assert.Equal("heavy", Cable("""{"cells": [[0, 0, 0]]}""", problems, out _)!.Grade);
        Assert.Equal("chute", Chute("""{"cells": [[0, 0, 0]]}""", problems, out _)!.Grade);
        Assert.Empty(problems);
    }

    [Fact]
    public void AllowNamesTheGuardsAndSplitLongIsRead()
    {
        List<OptionProblem> problems = new();

        RunBuildOrder plain = Cable("""{"cells": [[0, 0, 0]]}""", problems, out _)!;
        RunBuildOrder lifting = Cable(
            """{"cells": [[0, 0, 0]], "allow": ["door_keepout", "split", "lint"], "split_long": false}""",
            problems, out _)!;

        Assert.Empty(problems);
        Assert.False(plain.AllowDoorKeepOut || plain.AllowSplit || plain.AllowLint);
        Assert.True(plain.SplitLong);
        Assert.True(lifting.AllowDoorKeepOut && lifting.AllowSplit && lifting.AllowLint);
        Assert.False(lifting.SplitLong);
    }

    [Fact]
    public void RowsIncludeVerboseQueueAndConfirmAreRead()
    {
        List<OptionProblem> problems = new();

        RunBuildOrder reported = Cable(
            """{"cells": [[0, 0, 0]], "rows": 3, "include": ["links", "notes"], "verbose": true, "queue": false}""",
            problems, out _)!;
        RunBuildOrder acting = Cable("""{"cells": [[0, 0, 0]], "confirm": true}""", problems, out _)!;

        Assert.Empty(problems);
        Assert.Equal(3, reported.Report.Rows);
        Assert.True(reported.Fields.Includes("links") && reported.Fields.Includes("notes"));
        Assert.False(reported.Fields.Includes("network_devices"));
        Assert.True(reported.Fields.Verbose);
        Assert.False(reported.Queue);
        Assert.True(reported.Report.TakesConfirm);
        Assert.False(reported.Report.Acts);
        Assert.True(acting.Report.Acts);
        Assert.True(acting.Queue);
    }

    [Fact]
    public void TheHandlesThePiecesAndTheSourceAreRead()
    {
        List<OptionProblem> problems = new();

        RunBuildOrder order = Pipe("""
            {"grade": "gas", "waypoints": [[0, 0, 0]], "join": "all", "remove_ids": ["1", "2"],
            "assume_removed": ["3"], "allow_bridge": ["5001", {"id": "1378", "port": 0}],
            "join_to": {"id": "1378", "port": 1}, "root": "88", "join_trunk": true,
            "materials": "720", "refund_to": "ground"}
            """, problems, out _)!;

        Assert.Empty(problems);
        Assert.Equal(new[] { "1", "2" }, order.RemoveIds);
        Assert.Equal(new[] { "3" }, order.AssumeRemoved);
        Assert.Equal(new[] { "5001", "1378" }, Ids(order.AllowBridge));
        Assert.Equal(("1378", "1"), (order.JoinTo!.Id, order.JoinTo.Port));
        Assert.Equal(88L, order.Root!.Value.Value);
        Assert.True(order.JoinTrunk);
        Assert.Equal("all", order.Join);
        Assert.Equal("720", Assert.IsType<Payer.Holder>(Assert.Single(order.Materials.Payers)).Id);
        Assert.Same(RefundRoute.WherePieceStood, order.Materials.RefundTo);
        Assert.Null(order.Source.FromId);
        Assert.Same(RefundRoute.WherePieceStood, order.Source.RefundTo);
    }

    [Fact]
    public void AWordThatReadsAsNoneAndABlankAcknowledgementAreProblems()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Cable("""{"cells": [[0, 0, 0]], "join": "middle"}""", problems, out _));
        Assert.Equal("join", Assert.Single(problems).Path);
        problems.Clear();
        Assert.Null(Cable("""{"cells": [[0, 0, 0]], "join_to": {"port": 1}}""", problems, out _));
        Assert.Equal("join_to", Assert.Single(problems).Path);
        problems.Clear();
        Assert.Null(Cable("""{"cells": [[0, 0, 0]], "allow_bridge": [true]}""", problems, out _));
        Assert.Equal("allow_bridge[0]", Assert.Single(problems).Path);
        problems.Clear();
        Assert.Null(Cable("""{"cells": [[0, 0, 0]], "root": "pump"}""", problems, out _));
        Assert.Equal("root", Assert.Single(problems).Path);

        problems.Clear();
        Assert.Null(Pipe("""{"grade": "gas", "cells": [[0, 0, 0]], "acknowledge_gas_lost": "  "}""", problems,
            out _));
        Assert.Equal(GasHoldVerdict.AcknowledgeArgument, Assert.Single(problems).Path);
        problems.Clear();
        Assert.Equal("run-7", Pipe("""{"grade": "gas", "cells": [[0, 0, 0]], "acknowledge_gas_lost": "run-7"}""",
            problems, out _)!.AcknowledgeGasLost);
        Assert.Empty(problems);
    }

    [Fact]
    public void FreeIsACheatOfEveryRunWhatThatTakesNothingAndGivesNothingBackUnlessToldWhere()
    {
        List<OptionProblem> problems = new();

        RunBuildOrder cable = Cable("""{"cells": [[0, 0, 0]], "free": true}""", problems, out _)!;
        RunBuildOrder pipe = Pipe("""{"grade": "gas", "cells": [[0, 0, 0]], "free": true}""", problems, out _)!;
        RunBuildOrder chute = Chute("""{"cells": [[0, 0, 0]], "free": true, "refund_to": "ground"}""", problems, out _)!;
        RunBuildOrder paid = Pipe("""{"grade": "gas", "cells": [[0, 0, 0]]}""", problems, out _)!;

        Assert.Empty(problems);
        Assert.True(cable.Free && pipe.Free && chute.Free);
        Assert.False(paid.Free);
        Assert.Same(RefundRoute.Nothing, cable.Materials.RefundTo);
        Assert.Same(RefundRoute.Nothing, pipe.Source.RefundTo);
        Assert.True(pipe.Source.Free);
        Assert.Same(RefundRoute.WherePieceStood, chute.Materials.RefundTo);
        Assert.Same(RefundRoute.Default, paid.Materials.RefundTo);
        Assert.False(SourceRule.Takes(builds: true, free: true));
        Assert.True(SourceRule.Takes(builds: true, free: false));
        foreach (string what in new[] { "cable", "pipe", "chute" })
        {
            System.Text.Json.Nodes.JsonObject source =
                VerbCatalogue.Whats().Single(entry => entry.Verb == "build" && entry.What == what).Source;
            Assert.Equal("free: true", (string?)source["write"]!["cheat"]);
            Assert.Contains(VerbCatalogue.OptionsOf(source), option => option.Name == "free");
        }
    }
}
