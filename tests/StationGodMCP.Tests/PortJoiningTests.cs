#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Routing;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// inspect connections names, for every end, the cell a joining piece stands in (at, as build reports a port's at)
/// and the end that piece needs (piece_end), the same for a device's port and a pipe's open end.
/// </summary>
public sealed class PortJoiningTests
{
    [Fact]
    public void ADevicePortIsJoinedFromItsOwnCellWithAnEndBackIntoTheDevice()
    {
        // A device in (0, 0, 0) whose port stands in the cell +x of it: the game files the port's own cell as its
        // local grid and the device's cell as its facing grid.
        PieceModel device = RunModels.Piece(900, RunModels.At(0, 0, 0), "+x");
        (GridCell cell, GridStep? end) = EndCells.Joining(device, RunModels.At(1, 0, 0), RunModels.At(0, 0, 0));

        Assert.Equal(RunModels.At(1, 0, 0), cell);
        Assert.Equal("-x", end!.Value.Name);
    }

    [Fact]
    public void APipesOpenEndIsJoinedFromTheCellBeyondIt()
    {
        PieceModel pipe = RunModels.Piece(901, RunModels.At(0, 0, 0), "+z", "-z");
        (GridCell cell, GridStep? end) = EndCells.Joining(pipe, RunModels.At(0, 0, 0), RunModels.At(0, 0, 1));

        Assert.Equal(RunModels.At(0, 0, 1), cell);
        Assert.Equal("-z", end!.Value.Name);
    }

    [Fact]
    public void AnEndCarriesAtAndPieceEnd()
    {
        ConnectionEndView view = new ConnectionEndView(0, new ConnectionKind("Pipe", "Pipe", "Input", "Input"),
            new PositionView(1.2, 2, 3), null, new List<ThingView>(), new PositionView(1.5, 2, 3), "-x");
        JObject written = JObject.FromObject(view, ApiJson.Serializer);

        Assert.Equal(1.5, (double)written["at"]!["x"]!);
        Assert.Equal("-x", (string)written["piece_end"]!);
    }
}
