#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The ghost board without the game: ids, a ghost on the spot of one of the same prefab and turn replacing it, a build
/// taking its ghost off, the mirror a client keeps, the save file, and the wires between games (the board to a
/// client, a player's requests to the host).
/// </summary>
public sealed class GhostBoardTests
{
    private static readonly Rgba Cyan = new Rgba(0.2f, 0.9f, 1f, 0.4f);

    private static BoardGhost Ghost(int hash, double x, double yTurn = 0, string? label = null,
        GhostSource source = GhostSource.Player) =>
        new BoardGhost(0, hash, "Prefab" + hash, 2, new Vec3(x, 96.25, 898.5), new Vec3(0, yTurn, 0), Cyan, source,
            "LU", label, 1_760_000_000.5);

    [Fact]
    public void IdsCountUpAndAreNeverReused()
    {
        GhostBoard board = new GhostBoard();

        BoardGhost first = board.Add(Ghost(1, 0), out _)!;
        BoardGhost second = board.Add(Ghost(1, 5), out _)!;
        Assert.True(board.Remove(second.Id));
        BoardGhost third = board.Add(Ghost(1, 10), out _)!;

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.Equal(3, third.Id);
        Assert.False(board.Remove(99));
    }

    [Fact]
    public void AGhostOnTheSameSpotInTheSameTurnReplacesTheOldOne()
    {
        GhostBoard board = new GhostBoard();
        BoardGhost old = board.Add(Ghost(7, 0, label: "first"), out _)!;

        BoardGhost replacing = board.Add(Ghost(7, 0.05, label: "second"), out BoardGhost? replaced)!;

        Assert.Same(old, replaced);
        Assert.Equal("second", Assert.Single(board.All).Label);
        Assert.NotEqual(old.Id, replacing.Id);

        // Another turn or another prefab on the spot is another ghost.
        board.Add(Ghost(7, 0, yTurn: 90), out BoardGhost? none);
        board.Add(Ghost(8, 0), out BoardGhost? other);
        Assert.Null(none);
        Assert.Null(other);
        Assert.Equal(3, board.Count);
    }

    [Fact]
    public void ABuildOfThePrefabInTheTurnTakesItsGhostOff()
    {
        GhostBoard board = new GhostBoard();
        BoardGhost ghost = board.Add(Ghost(7, 0, yTurn: 90), out _)!;

        Assert.Null(board.Built(7, new Vec3(0, 96.25, 898.5), new Vec3(0, 0, 0)));
        Assert.Null(board.Built(8, new Vec3(0, 96.25, 898.5), new Vec3(0, 90, 0)));
        Assert.Null(board.Built(7, new Vec3(0.5, 96.25, 898.5), new Vec3(0, 90, 0)));

        // The game's own eulerAngles for the same turn read as the same turn.
        Assert.Same(ghost, board.Built(7, new Vec3(0.02, 96.25, 898.5), new Vec3(360, 90, 0)));
        Assert.Equal(0, board.Count);
    }

    [Theory]
    [InlineData(0, 180, 0, 180, 0, 180, true)]
    [InlineData(0, 90, 0, 0, -270, 0, true)]
    [InlineData(90, 0, 0, 0, 0, 90, false)]
    [InlineData(0, 0, 0, 0, 0, 0.5, true)]
    [InlineData(0, 0, 0, 0, 0, 3, false)]
    public void TurnsAreComparedAsRotations(double ax, double ay, double az, double bx, double by, double bz,
        bool same)
    {
        Assert.Equal(same, GhostBoard.SameTurn(new Vec3(ax, ay, az), new Vec3(bx, by, bz)));
    }

    [Fact]
    public void TheBoardIsFull()
    {
        GhostBoard board = new GhostBoard();
        for (int index = 0; index < GhostBoard.Maximum; index++)
        {
            Assert.NotNull(board.Add(Ghost(1, index), out _));
        }

        Assert.Null(board.Add(Ghost(1, -5), out _));

        // A replacement still goes in.
        Assert.NotNull(board.Add(Ghost(1, 3), out BoardGhost? replaced));
        Assert.NotNull(replaced);
    }

    [Fact]
    public void TheFileRoundTripsAndKeepsTheNextId()
    {
        GhostBoard board = new GhostBoard();
        board.Add(Ghost(1, 0, label: "pump"), out _);
        BoardGhost gone = board.Add(Ghost(2, 1, yTurn: 270, source: GhostSource.Agent), out _)!;
        board.Add(Ghost(3, 2), out _);
        board.Remove(gone.Id);
        board.Remove(3);

        GhostBoard read = GhostBoard.FromJson(JObject.Parse(board.ToJson().ToString()), out int skipped);

        Assert.Equal(0, skipped);
        BoardGhost ghost = Assert.Single(read.All);
        Assert.Equal(1, ghost.Id);
        Assert.Equal("pump", ghost.Label);
        Assert.Equal(new Vec3(0, 96.25, 898.5), ghost.Position);
        Assert.Equal(Cyan, ghost.Tint);
        Assert.Equal(GhostSource.Player, ghost.Source);
        Assert.Equal(1_760_000_000.5, ghost.PlacedAt);
        Assert.Equal(4, read.Add(Ghost(9, 9), out _)!.Id);
    }

    [Fact]
    public void AnEntryThatDoesNotReadIsSkipped()
    {
        JObject json = JObject.Parse("""
            {"next": 3, "ghosts": [
              {"id": 1, "prefab_hash": 5, "position": [0, 0, 0], "euler": [0, 0, 0], "tint": [1, 1, 1, 1]},
              {"id": 2, "prefab_hash": 5, "position": [0, 0], "euler": [0, 0, 0], "tint": [1, 1, 1, 1]},
              {"id": "x"}, 7]}
            """);

        GhostBoard board = GhostBoard.FromJson(json, out int skipped);

        Assert.Equal(3, skipped);
        Assert.Single(board.All);
    }

    [Fact]
    public void AMirrorFollowsTheHostsResetAddsAndRemovals()
    {
        GhostBoard mirror = new GhostBoard();
        BoardGhost a = Ghost(1, 0).WithId(5);
        BoardGhost b = Ghost(2, 1).WithId(6);

        mirror.Apply(true, new[] { a, b }, new long[0]);
        mirror.Apply(false, new[] { Ghost(3, 2).WithId(7) }, new long[] { 5 });

        Assert.Equal(new long[] { 6, 7 }, mirror.All.ConvertIds());
        mirror.Apply(true, new[] { a }, new long[0]);
        Assert.Equal(new long[] { 5 }, mirror.All.ConvertIds());
    }

    // ---- wires ----

    [Fact]
    public void TheBoardRoundTripsOnTheWire()
    {
        BoardGhost ghost = Ghost(-1234, 716.25, yTurn: 90, label: "Second pump").WithId(41);
        byte[] bytes = DrawWire.Encode(new DrawCommand.Board(true, new[] { ghost }, new long[] { 3, 4 }));

        DrawCommand.Board read = Assert.IsType<DrawCommand.Board>(
            Assert.IsType<WireRead<DrawCommand>.Read>(DrawWire.Decode(bytes)).Value);

        Assert.True(read.Replace);
        BoardGhost back = Assert.Single(read.Added);
        Assert.Equal(41, back.Id);
        Assert.Equal(-1234, back.PrefabHash);
        Assert.Equal("Prefab-1234", back.PrefabName);
        Assert.Equal(2, back.BuildState);
        Assert.Equal(ghost.Position, back.Position);
        Assert.Equal(ghost.Euler, back.Euler);
        Assert.Equal(Cyan, back.Tint);
        Assert.Equal(GhostSource.Player, back.Source);
        Assert.Equal("LU", back.Owner);
        Assert.Equal("Second pump", back.Label);
        Assert.Equal(ghost.PlacedAt, back.PlacedAt);
        Assert.Equal(new long[] { 3, 4 }, read.Removed);
    }

    [Fact]
    public void EveryTruncationOfTheBoardIsRefused()
    {
        byte[] bytes = DrawWire.Encode(new DrawCommand.Board(false, new[] { Ghost(1, 0).WithId(1) },
            new long[] { 2 }));

        for (int length = 0; length < bytes.Length; length++)
        {
            Assert.IsType<WireRead<DrawCommand>.Malformed>(DrawWire.Decode(bytes.AsSpan(0, length).ToArray()));
        }
    }

    [Fact]
    public void APlayersRequestsRoundTrip()
    {
        GhostPlacement placement = new GhostPlacement(99, 1, new Vec3(1, 2, 3), new Vec3(0, 270, 90), Cyan, "tank");

        GhostRequest.Place place = Assert.IsType<GhostRequest.Place>(Assert.IsType<WireRead<GhostRequest>.Read>(
            GhostWire.Decode(GhostWire.Encode(new GhostRequest.Place(new[] { placement })))).Value);
        GhostRequest.Remove remove = Assert.IsType<GhostRequest.Remove>(Assert.IsType<WireRead<GhostRequest>.Read>(
            GhostWire.Decode(GhostWire.Encode(new GhostRequest.Remove(new long[] { 8, 9 })))).Value);

        GhostPlacement back = Assert.Single(place.Ghosts);
        Assert.Equal(99, back.PrefabHash);
        Assert.Equal(1, back.BuildState);
        Assert.Equal(new Vec3(0, 270, 90), back.Euler);
        Assert.Equal("tank", back.Label);
        Assert.Equal(new long[] { 8, 9 }, remove.Ids);
    }

    [Fact]
    public void ARequestOfAnotherProtocolOrPastItsCapIsNotRead()
    {
        byte[] bytes = GhostWire.Encode(new GhostRequest.Remove(new long[] { 1 }));
        bytes[0] = 2;
        Assert.Equal(2, Assert.IsType<WireRead<GhostRequest>.OtherProtocol>(GhostWire.Decode(bytes)).Protocol);

        WireWriter writer = new WireWriter();
        writer.Byte(ViewProtocol.Current);
        writer.Byte(2);
        writer.Int32(GhostWire.MaximumIds + 1);
        Assert.IsType<WireRead<GhostRequest>.Malformed>(GhostWire.Decode(writer.ToArray()));
    }

    // ---- find ghost's filter and build arguments ----

    [Fact]
    public void TheFilterKeepsByIdsPrefabOwnerSourceAndDistance()
    {
        GhostBoard board = new GhostBoard();
        board.Add(Ghost(1, 0), out _);
        board.Add(Ghost(2, 12, source: GhostSource.Agent), out _);
        board.Add(Ghost(1, 4), out _);

        List<(BoardGhost Ghost, double? Distance)> near = new GhostFilter(null, null, null, null)
            .Apply(board.All, new Vec3(5, 96.25, 898.5), 5.5);
        Assert.Equal(new long[] { 3, 1 }, near.ConvertAll(entry => entry.Ghost.Id));
        Assert.Equal(1.0, near[0].Distance!.Value, 6);

        Assert.Equal(new long[] { 2 }, new GhostFilter(null, null, null, GhostSource.Agent)
            .Apply(board.All, null, null).ConvertAll(entry => entry.Ghost.Id));
        Assert.Empty(new GhostFilter(null, null, "agent", GhostSource.Player).Apply(board.All, null, null));
        Assert.Equal(new long[] { 1, 2, 3 }, new GhostFilter(null, null, "lu", null)
            .Apply(board.All, null, null).ConvertAll(entry => entry.Ghost.Id));
        Assert.Equal(new long[] { 3 }, new GhostFilter(new long[] { 3, 99 }, null, null, null)
            .Apply(board.All, null, null).ConvertAll(entry => entry.Ghost.Id));
    }

    [Fact]
    public void AGhostsBuildArgumentsAreBuildStructures()
    {
        BoardGhost ghost = Ghost(7, 716.25, yTurn: 90).WithId(4);

        JObject one = GhostFilter.BuildArguments(ghost);
        JObject all = GhostFilter.BuildArguments(new[] { ghost, ghost })!;

        Assert.Equal("structure", (string)one["what"]!);
        Assert.Equal("Prefab7", (string)one["prefab"]!);
        Assert.Equal(new[] { 716.25, 96.25, 898.5 }, one["at"]!.ToObject<double[]>());
        Assert.Equal(new[] { 0.0, 90.0, 0.0 }, one["rotation"]!.ToObject<double[]>());
        Assert.Equal(2, (int)one["build_state"]!);
        Assert.Equal("structure", (string)all["what"]!);
        Assert.Equal(2, ((JArray)all["placements"]!).Count);
        Assert.Null(all["placements"]![0]!["what"]);
        Assert.Null(GhostFilter.BuildArguments(new BoardGhost[0]));
    }
}

internal static class GhostListExtensions
{
    internal static long[] ConvertIds(this IReadOnlyList<BoardGhost> ghosts)
    {
        long[] ids = new long[ghosts.Count];
        for (int index = 0; index < ghosts.Count; index++)
        {
            ids[index] = ghosts[index].Id;
        }

        return ids;
    }
}
