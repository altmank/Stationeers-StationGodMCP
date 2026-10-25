#nullable enable

using System;
using System.Collections.Generic;
using Lattice.Testing;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink.Json;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Pure;
using Xunit;
using L = Lattice.Api;

namespace StationGodMCP.Tests.LatticeLink;

/// <summary>
/// find, write and show lattice's JSON (LatticeIn, LatticeOut) against Lattice's own test world: commands read from an
/// agent's JSON do what Lattice's typed commands do, answers write back in the same forms, and a problem names its path.
/// </summary>
[Collection(LatticeWorldCollection.Name)]
public sealed class LatticeJsonTests
{
    [Fact]
    public void APlaceCommandInJsonPlacesTheGhostWithItsLookAndWritesItBack()
    {
        using TestWorld world = Start();
        LatticeIn read = new LatticeIn(world.System);
        LatticeLookIn looks = new LatticeLookIn(read);

        L.Command? command = read.Command(JObject.Parse("""
            {"place": [{"build": {"piece": "StructureFrame", "at": [1, 1, 1], "turn": [0, 90, 0]},
                        "label": "north frame", "tint": "#ff8800", "groups": ["hall"],
                        "look": {"look": "quiet", "xray_strength": 0.6}}]}
            """), "command", looks);

        Assert.Empty(read.Problems);
        L.Ghost ghost = Assert.Single(Applied(world, command!).Added);
        JObject written = new LatticeOut(world.System).Ghost(ghost);
        Assert.Equal("StructureFrame", (string?)written["build"]!["piece"]);
        Assert.Equal(new JArray(1.0, 1.0, 1.0), written["build"]!["at"]);
        Assert.Equal(new JArray(0, 90, 0), written["build"]!["turn"]);
        Assert.Equal("north frame", (string?)written["label"]);
        Assert.Equal(new JArray("hall"), written["groups"]);
        Assert.Equal("quiet", (string?)written["look"]!["look"]);
        Assert.Equal(0.6, (double)written["look"]!["xray_strength"]!);
        Assert.False((bool)written["look"]!["markers"]!["ports"]!);
        Assert.Single(looks.Looks);
    }

    [Fact]
    public void ABatchMakesAPlanEditsItsGhostAndRevertsWholeAsOneChangeSet()
    {
        using TestWorld world = Start();
        LatticeIn read = new LatticeIn(world.System);

        L.ChangeSet made = Applied(world, read.Command(JObject.Parse("""
            {"create_plan": {"name": "Hall", "ghosts": [{"build": {"piece": "StructureFrame", "at": [3, 1, 1]}}]}}
            """), "command")!);
        long ghost = made.Added[0].Id.Value;
        L.ChangeSet edited = Applied(world, read.Command(JObject.Parse("""
            {"batch": [{"change": {"which": [@GHOST], "edit": {"label": "door frame"}}},
                       {"change": {"which": {"plan": @MADEPLAN}, "edit": {"attach": {"key": "notes", "value": {"by": "agent"}}}}}]}
            """.Replace("@GHOST", ghost.ToString()).Replace("@MADEPLAN", made.PlansOpened[0].Id.Value.ToString())), "command")!);

        Assert.Empty(read.Problems);
        Assert.Equal("Hall", made.PlansOpened[0].Name.Value);
        L.Ghost after = Assert.Single(edited.Changed).After;
        Assert.Equal("door frame", after.Label.Reduce(default(L.Label)).Value);
        Assert.True(after.Attached.TryGetValue(LatticeNames.Must(L.AttachmentKey.Create(LatticeNames.Mod, "notes")),
            out L.Attachment notes));
        Assert.Equal("""{"by":"agent"}""", notes.Value);

        L.ChangeSet reverted = Applied(world, read.Command(new JObject { ["revert"] = edited.Id.Value }, "command")!);
        Assert.False(Assert.Single(reverted.Changed).After.Label.IsSome);
    }

    [Fact]
    public void ARouteEndsOnAGhostsPortAsLatticeLaysIt()
    {
        using TestWorld world = Start();
        LatticeIn read = new LatticeIn(world.System);
        LatticeOut write = new LatticeOut(world.System);
        L.Ghost tank = Assert.Single(Applied(world, read.Command(JObject.Parse("""
            {"place": [{"build": {"piece": "StructureTankSmall", "at": [11, 1, 11]}}]}
            """), "command")!).Added);
        L.Port port = Assert.IsType<L.Intent.Build>(tank.Intent) is { } build &&
                      world.System.Catalogue.PortsAt(build.Target).TryGet(out L.ValueList<L.Port> ports) && ports.Count > 0
            ? ports[0]
            : throw new InvalidOperationException("the tank has no port");
        JArray joins = LatticeOut.Cell(port.Joins);
        L.Cell start = port.Joins.Neighbour(port.Toward, L.PieceGrid.Small).Neighbour(port.Toward, L.PieceGrid.Small)
            .Neighbour(port.Toward, L.PieceGrid.Small);

        L.Command? route = read.Command(new JObject
        {
            ["plan_route"] = new JObject
            {
                ["kit"] = "ItemKitPipe", ["from"] = LatticeOut.Cell(start), ["to"] = joins,
                ["to_port"] = new JObject { ["ghost"] = tank.Id.Value, ["port"] = port.Index.Value },
                ["lay"] = new JObject { ["single_pieces_only"] = true },
            }
        }, "command");
        Assert.Empty(read.Problems);

        L.Preview preview = Previewed(world, route!);
        JObject run = (JObject)write.Preview(preview)["run"]!;
        JObject end = (JObject)Assert.Single((JArray)run["port_ends"]!);
        Assert.Equal(tank.Id.Value, (long)end["end"]!["ghost"]!);
        Assert.NotNull(end["joined_by"]);
        Assert.Equal(joins, end["port"]!["joins"]);
    }

    [Fact]
    public void AStyleReadsAndWritesBackInTheSameForm()
    {
        using TestWorld world = Start();
        LatticeIn read = new LatticeIn(world.System);
        JObject given = JObject.Parse("""
            {"fill": "wire", "fill_paint": [0.0, 1.0, 0.0, 1.0], "edge": {"line": {"style": "dashed", "width": 2.0, "paint": "fail"}},
             "pattern": "cross_hatch", "glow": {"lit": {"paint": "owner", "strength": 0.5}},
             "effect": {"blink": {"period": 1.0, "duty": 0.25}}, "see_through": {"through": {"strength": 1.0, "range": 200.0}},
             "beyond_clip": {"dim": 0.5}, "distance_fade": [10.0, 40.0], "label": {"shown": {"says": {"literal": "here"}, "anchor": "below"}},
             "icon": "swap", "open_ends": false, "ports": true, "flow_arrows": true, "waits_links": false, "pickable": false}
            """);

        L.Style style = read.Style(given, "style")!;

        Assert.Empty(read.Problems);
        JObject written = LatticeOut.Style(style);
        foreach (string same in new[] { "fill", "fill_paint", "edge", "pattern", "glow", "effect", "see_through", "beyond_clip",
                     "distance_fade", "icon", "open_ends", "ports", "flow_arrows", "waits_links", "pickable" })
        {
            Assert.True(JToken.DeepEquals(given[same], written[same]), $"{same}: {written[same]}");
        }

        Assert.Equal("here", (string?)written["label"]!["shown"]!["says"]!["literal"]);
    }

    [Fact]
    public void EveryProblemNamesItsPathAndNothingIsRead()
    {
        using TestWorld world = Start();
        LatticeIn read = new LatticeIn(world.System);

        L.Command? command = read.Command(JObject.Parse("""
            {"batch": [{"remove": {"tagged": {"key": "Bad Key"}}},
                       {"place": [{"build": {"piece": "StructureNoSuch", "at": [0, 0]}}]},
                       {"transform": {"which": "everything", "how": {"rotate": {"pivot": [0, 0, 0], "turn": [45, 0, 0]}}}},
                       {"fly": 1}]}
            """), "command");

        Assert.Null(command);
        Assert.True(read.Failed);
        Assert.Contains(read.Problems, problem => problem.StartsWith("command.batch[0].remove.tagged.key:", StringComparison.Ordinal));
        Assert.Contains(read.Problems, problem => problem.StartsWith("command.batch[1].place[0].build.piece:", StringComparison.Ordinal));
        Assert.Contains(read.Problems, problem => problem.StartsWith("command.batch[1].place[0].build.at:", StringComparison.Ordinal));
        Assert.Contains(read.Problems, problem => problem.StartsWith("command.batch[2].transform.how.rotate.turn:", StringComparison.Ordinal));
        Assert.Contains(read.Problems, problem => problem.StartsWith("command.batch[3]:", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryCommandKindReads()
    {
        using TestWorld world = Start();
        LatticeIn read = new LatticeIn(world.System);
        L.Ghost frame = Assert.Single(Applied(world, read.Command(JObject.Parse("""
            {"place": [{"build": {"piece": "StructureFrame", "at": [5, 1, 5]}}]}
            """), "command")!).Added);
        long id = frame.Id.Value;
        long plan = frame.Plan.Value;
        string[] commands =
        {
            """{"remove": [@ID]}""",
            """{"change": {"which": [@ID], "edit": {"tint": null}}}""",
            """{"move": {"to": {"@ID": {"piece": "StructureFrame", "at": [7, 1, 5]}}}}""",
            """{"create_plan": "Annex"}""",
            """{"edit_plan": {"plan": @PLAN, "edit": {"set_data": {"key": "stage", "value": "draft"}}}}""",
            """{"move_to_plan": {"which": [@ID], "to": @PLAN}}""",
            """{"revert": 1}""",
            """{"batch": [{"remove": [@ID]}, {"remove": "everything"}]}""",
            """{"finish_plan": @PLAN}""",
            """{"catch_up": @PLAN}""",
            """{"mark_removal": {"which": {"box": [[0, 0, 0], [2, 2, 2]]}}}""",
            """{"plan_swap": {"which": {"piece": "StructureCableStraight"}, "to": {"same_shape_from": "ItemCableCoilHeavy"}}}""",
            """{"plan_finish": {"which": {"box": [[0, 0, 0], [2, 2, 2]]}, "finish": {"colour": 3}}}""",
            """{"plan_run": {"kit": "ItemCableCoil", "from": [0.25, 0.25, 0.25], "via": [[2.25, 0.25, 0.25]], "to": [2.25, 0.25, 2.25]}}""",
            """{"plan_route": {"kit": "ItemKitPipe", "from": [0.25, 0.25, 0.25], "to": [3.25, 0.25, 0.25], "toggles": {"fewest_bends": true, "first": "level"}, "branches": [{"from": [1.25, 0.25, 2.25]}]}}""",
            """{"transform": {"which": [@ID], "how": {"mirror": {"normal": "x", "at": 10}}, "copy": true}}""",
            """{"place_template": {"template": {"plan": @PLAN}, "origin": [21, 1, 21], "as": {"name": "Copy"}}}""",
            """{"place_template_in": {"template": {"name": "one", "ghosts": [{"build": {"piece": "StructureFrame", "at": [1, 1, 1]}}]}, "origin": [31, 1, 31]}}""",
            """{"repair": @PLAN}""",
            """{"take_down": {"plan": @PLAN, "made_by": "takedown"}}""",
            """{"reopen": @PLAN}""",
            """{"plan_split": {"ghosts": [@ID]}}""",
        };

        foreach (string text in commands)
        {
            L.Command? command = read.Command(JObject.Parse(text.Replace("@ID", id.ToString()).Replace("@PLAN", plan.ToString())), "command");
            Assert.True(command != null, $"{text}: {string.Join("; ", read.Problems)}");
        }

        // place is the setup's own.
        Assert.Equal(LatticeIn.CommandKinds.Length - 1, commands.Length);
    }

    private static TestWorld Start() =>
        TestWorld.Start(TestScenario.Empty).Match(static world => world,
            static why => throw new InvalidOperationException(LatticeText.Of(why)));

    private static L.ChangeSet Applied(TestWorld world, L.Command command)
    {
        L.Submission sent = world.System.Commands.Submit(L.CommandId.New(), command, LatticeNames.Agent);
        if (sent is L.Submission.Sent)
        {
            Assert.True(world.Settle().TryGet(out int _));
            throw new InvalidOperationException("the command was worked out over frames");
        }

        L.Submission.Answered answered = Assert.IsType<L.Submission.Answered>(sent);
        return answered.Outcome is L.Outcome.Applied applied
            ? applied.Changes
            : throw new InvalidOperationException(LatticeText.Of(((L.Outcome.Refused)answered.Outcome).Why));
    }

    private static L.Preview Previewed(TestWorld world, L.Command command)
    {
        for (int tries = 0; tries < 20; tries++)
        {
            if (world.System.Preview.Of(command, LatticeNames.Agent) is L.Answer<L.Result<L.Preview, L.ValueList<L.Refusal>>>.Ready ready)
            {
                return ready.Value.TryGet(out L.Preview preview)
                    ? preview
                    : throw new InvalidOperationException(ready.Value.TryGetError(out L.ValueList<L.Refusal> why) ? LatticeText.Of(why) : "refused");
            }

            Assert.True(world.Settle().TryGet(out int _));
        }

        throw new InvalidOperationException("the preview never came");
    }
}
