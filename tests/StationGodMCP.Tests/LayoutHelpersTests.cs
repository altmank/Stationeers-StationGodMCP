#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Scheduling;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>1.3.5 layout helpers: the rotation readout, where a planned device's ports land.</summary>
public sealed class RotationReadoutTests
{
    private static IEnumerable<CubeRotation> All24()
    {
        foreach (GridStep forward in GridStep.All)
        {
            foreach (GridStep up in GridStep.All)
            {
                CubeRotation? rotation = CubeRotation.FromFacing(forward, up);
                if (rotation != null)
                {
                    yield return rotation;
                }
            }
        }
    }

    [Fact]
    public void EveryQuarterTurnRotationReadsBackAsEulerTurnsThatPlaceItAgain()
    {
        int count = 0;
        foreach (CubeRotation rotation in All24())
        {
            (int x, int y, int z) = rotation.EulerTurns();
            Assert.Equal(rotation, CubeRotation.FromEuler(x, y, z));
            Assert.Equal(rotation, new RotationSpec.Euler(x, y, z).Resolve(out _));
            count++;
        }

        Assert.Equal(24, count);
    }

    [Fact]
    public void FacingAndUpReadBackAsTheFacingFormPlaceStructureTakes()
    {
        foreach (CubeRotation rotation in All24())
        {
            OrientationView view = OrientationView.Of(rotation);
            Assert.True(GridStep.TryParse(view.Facing, out GridStep facing));
            Assert.True(GridStep.TryParse(view.Up, out GridStep up));
            Assert.Equal(rotation, new RotationSpec.Facing(facing, up).Resolve(out _));
        }
    }

    [Fact]
    public void AnUprightDeviceTurnsOnlyAboutY()
    {
        CubeRotation east = CubeRotation.FromFacing(RunModels.Step("+x"), RunModels.Step("+y"))!;
        Assert.Equal((0, 1, 0), east.EulerTurns());
        OrientationView view = OrientationView.Of(east);
        Assert.Equal("+x", view.Facing);
        Assert.Equal("+y", view.Up);
        Assert.Equal(90.0, view.Euler.Y);
    }

    [Fact]
    public void ReversingTheFacingIsAHalfTurnAboutY()
    {
        CubeRotation north = CubeRotation.FromFacing(RunModels.Step("+z"), RunModels.Step("+y"))!;
        CubeRotation south = CubeRotation.FromFacing(RunModels.Step("-z"), RunModels.Step("+y"))!;
        Assert.Equal((0, 0, 0), north.EulerTurns());
        Assert.Equal((0, 2, 0), south.EulerTurns());
    }

    [Fact]
    public void ARotationOffTheGridHasOnlyEulerAngles()
    {
        JObject json = JObject.Parse(WireCheck.New(OrientationView.OffGrid(new RotationView(12.34, 0, 0))));
        Assert.Equal(JTokenType.Null, json["facing"]!.Type);
        Assert.Equal(JTokenType.Null, json["up"]!.Type);
        Assert.Equal(12.3, json["euler"]!["x"]!.Value<double>());
    }

    [Fact]
    public void FoundStructuresCarryARotationAndOtherThingsLeaveItOut()
    {
        OrientationView rotation =
            OrientationView.Of(CubeRotation.FromFacing(RunModels.Step("-x"), RunModels.Step("+y"))!);
        FoundThingView tank = new FoundThingView(new ThingView(new ThingId(5), "StructureTankSmall", "Tank"), null,
            "Tank", "structure", "DeviceAtmospherics", true, FoundThingView.Built, null, new List<HeldInView>(),
            new PositionView(1, 2, 3), null, true, true, rotation);
        FoundThingView item = new FoundThingView(new ThingView(new ThingId(6), "ItemKitPipe", "Kit"), null, "Kit",
            "item", "Stackable", false, "ground", null, new List<HeldInView>(), new PositionView(1, 2, 3), null,
            false, false);
        JObject found = JObject.Parse(WireCheck.New(tank));
        Assert.Equal("-x", (string?)found["rotation"]!["facing"]);
        Assert.Equal("+y", (string?)found["rotation"]!["up"]);
        Assert.Equal(270.0, found["rotation"]!["euler"]!["y"]!.Value<double>());
        Assert.Null(JObject.Parse(WireCheck.New(item))["rotation"]);
    }

    [Fact]
    public void SurveyDevicesAndConnectionsCarryTheRotation()
    {
        OrientationView rotation = OrientationView.Of(CubeRotation.Identity);
        SurveyDeviceView device = new SurveyDeviceView(new ThingView(new ThingId(7), "StructureValve", "Valve"),
            new PositionView(0, 0, 0), new List<SurveyPortView>(), rotation);
        ConnectionsView connections = new ConnectionsView(new ThingView(new ThingId(7), "StructureValve", "Valve"),
            new PositionView(0, 0, 0), null, new List<ConnectionEndView>(), rotation);
        LookingAtTargetView target = new LookingAtTargetView(new ThingView(new ThingId(7), "StructureValve", "Valve"),
            null, "structure", "Valve", new PositionView(0, 0, 0), 1.0, true, false, null, rotation);
        foreach (object view in new object[] { device, connections, target })
        {
            JObject json = JObject.Parse(WireCheck.New(view));
            Assert.Equal("+z", (string?)json["rotation"]!["facing"]);
            Assert.Equal(0.0, json["rotation"]!["euler"]!["y"]!.Value<double>());
        }
    }
}

/// <summary>build structure's port preview: a planned device's ports where it would stand.</summary>
public sealed class PortPreviewTests
{
    private const int Pipe = 1;
    private const int Power = 2;
    private const int Chute = 4;
    private const int Rails = 64;

    [Fact]
    public void PortsKeepTheirIndexAmongAllEndsAndPointIntoTheDevice()
    {
        GridCell device = RunModels.At(0, 0, 0);
        List<PieceEnd> ends = new List<PieceEnd>
        {
            new PieceEnd(RunModels.At(1, 0, 0), device, Pipe, 1),
            new PieceEnd(RunModels.At(0, 1, 0), device, Rails, 0),
            new PieceEnd(RunModels.At(-1, 0, 0), device, Power, 0)
        };

        List<PortCell> ports = PortCells.Of(ends, Pipe | Power | Chute);

        Assert.Equal(2, ports.Count);
        Assert.Equal(0, ports[0].Index);
        Assert.Equal(RunModels.At(1, 0, 0), ports[0].Cell);
        Assert.Equal("-x", ports[0].Toward!.Value.Name);
        Assert.Equal(2, ports[1].Index);
        Assert.Equal("+x", ports[1].Toward!.Value.Name);
    }

    [Fact]
    public void AnEndWhoseCellsAreNotNeighboursHasNoToward()
    {
        List<PieceEnd> ends = new List<PieceEnd> { new PieceEnd(RunModels.At(0, 0, 0), RunModels.At(2, 0, 0), Pipe, 0) };
        Assert.Null(PortCells.Of(ends, Pipe)[0].Toward);
    }

    [Fact]
    public void APlacementListsPortsOnlyWhenItHasThem()
    {
        PlacementPrefabView prefab = new PlacementPrefabView("StructureActiveVent", 1, "Active Vent", 3);
        PlacementSpotView spot = new PlacementSpotView("grid", new PositionView(1, 2, 3),
            OrientationView.Of(CubeRotation.Identity), null);
        PlacementLookView look = new PlacementLookView(2, null, null);
        List<SurveyPortView> ports = new List<SurveyPortView>
        {
            new SurveyPortView(0, new PositionView(1, 2, 2.5), "+z", "Pipe", "Input", null)
        };

        JObject withPorts = JObject.Parse(WireCheck.New(new PlacementView(0, prefab, spot, look,
            new List<UpgradeAmountView>(), ports)));
        JObject without = JObject.Parse(WireCheck.New(new PlacementView(0, prefab, spot, look,
            new List<UpgradeAmountView>())));

        JToken port = withPorts["ports"]![0]!;
        Assert.Equal(0, (int)port["index"]!);
        Assert.Equal(2.5, port["at"]!["z"]!.Value<double>());
        Assert.Equal("+z", (string?)port["toward"]);
        Assert.Equal("Pipe", (string?)port["type"]);
        Assert.Equal("Input", (string?)port["role"]);
        Assert.Equal(JTokenType.Null, port["network_id"]!.Type);
        Assert.Null(without["ports"]);
    }
}

/// <summary>plan_*_route: in-line tank ends and reserved cells.</summary>
public sealed class RouteEndsAndReservationTests
{
    private static GridCell At(int x, int y, int z) => RunModels.At(x, y, z);

    private static Func<GridCell, CellCost> Open => cell => CellCost.Of(1.0);

    [Fact]
    public void TheOnlyFreeEndIsTaken()
    {
        Assert.Equal(1, EndChoice.Pick(new[] { (0, false), (1, true) }, null, out string? error));
        Assert.Null(error);
    }

    [Fact]
    public void TwoFreeEndsNeedAPort()
    {
        Assert.Null(EndChoice.Pick(new[] { (0, true), (1, true) }, null, out string? error));
        Assert.Contains("2 free ends [0, 1]", error);
        Assert.Equal(1, EndChoice.Pick(new[] { (0, true), (1, true) }, 1, out _));
    }

    [Fact]
    public void AJoinedOrUnknownEndIsRefused()
    {
        Assert.Null(EndChoice.Pick(new[] { (0, false), (1, true) }, 0, out string? joined));
        Assert.Contains("already joined", joined);
        Assert.Null(EndChoice.Pick(new[] { (0, false), (1, true) }, 5, out string? unknown));
        Assert.Contains("not one of its ends", unknown);
        Assert.Null(EndChoice.Pick(new[] { (0, false), (1, false) }, null, out string? none));
        Assert.Contains("no free end", none);
    }

    [Fact]
    public void AReservedCellThatIsAnEndIsReleased()
    {
        RouteReservation reserved = RouteReservation.Of(new[] { At(1, 0, 0), At(4, 0, 0) }, new[] { At(4, 0, 0) });
        Assert.Equal(1, reserved.Count);
        Assert.Equal(new List<GridCell> { At(4, 0, 0) }, reserved.Released);
        Assert.True(reserved.Contains(At(1, 0, 0)));
        Assert.False(reserved.Contains(At(4, 0, 0)));
    }

    [Fact]
    public void NoReservationLeavesTheCostAlone()
    {
        Func<GridCell, CellCost> cost = Open;
        Assert.Same(cost, RouteReservation.None.Guard(cost));
    }

    [Fact]
    public void ASlowSearchStepEndsByTheClockNotAtAFixedBatch()
    {
        // review D, R-D-12: cells that cost a long time to judge end a step before ExpansionsPerStep expansions
        RouteRules box = new RouteRules(2.0, AxisOrder.Any, 4000, At(-12, -3, -3), At(12, 3, 3));
        Func<GridCell, CellCost> slow = cell =>
        {
            System.Diagnostics.Stopwatch spin = System.Diagnostics.Stopwatch.StartNew();
            while (spin.Elapsed.TotalMilliseconds < 0.05)
            {
            }

            return CellCost.Of(1.0);
        };
        Answer<RouteResult> found = new Answer<RouteResult>();
        int steps = 0;
        foreach (object? _ in RoutePlanner.Searching(RouteEnd.Open(At(-10, 0, 0)), new[] { RouteEnd.Open(At(10, 0, 0)) },
                     slow, box, null, found))
        {
            steps++;
        }

        Assert.NotNull(found.Value.Cells);
        Assert.True(steps * RoutePlanner.ExpansionsPerStep > found.Value.Expanded * 2,
            $"{steps} steps for {found.Value.Expanded} expansions");
    }

    [Fact]
    public void AWorkSliceEndsAtTheEarliestOfNestedStepsAndIsPutBack()
    {
        Assert.Equal(double.PositiveInfinity, WorkSlice.EndMs);
        double seen = 0.0;
        Work<int> inner = new Work<int>(answer => Note(() => seen = WorkSlice.EndMs, answer));
        Work<int> outer = new Work<int>(answer => Drive(inner, answer));

        outer.Step(StopwatchClock.Instance, StopwatchClock.Instance.NowMs + 60000.0);

        Assert.True(seen <= StopwatchClock.Instance.NowMs + 60000.0 && seen > 60000.0);
        Assert.Equal(double.PositiveInfinity, WorkSlice.EndMs);
    }

    private static IEnumerable<object?> Note(Action note, Answer<int> answer)
    {
        note();
        answer.Value = 1;
        yield break;
    }

    private static IEnumerable<object?> Drive(Work<int> inner, Answer<int> answer)
    {
        inner.Step(StopwatchClock.Instance, StopwatchClock.Instance.NowMs + 120000.0);
        answer.Value = 1;
        yield break;
    }

    [Fact]
    public void TheRouteGoesAroundAReservedCell()
    {
        RouteRules box = new RouteRules(2.0, AxisOrder.Any, 400, At(-3, -3, -3), At(7, 3, 3));
        RouteResult straight = RoutePlanner.Find(RouteEnd.Open(At(0, 0, 0)), RouteEnd.Open(At(4, 0, 0)), Open, box);
        Assert.Contains(At(2, 0, 0), straight.Cells!);

        RouteReservation reserved = RouteReservation.Of(new[] { At(2, 0, 0) }, new[] { At(0, 0, 0), At(4, 0, 0) });
        RouteResult around = RoutePlanner.Find(RouteEnd.Open(At(0, 0, 0)), RouteEnd.Open(At(4, 0, 0)),
            reserved.Guard(Open), box);

        Assert.NotNull(around.Cells);
        Assert.DoesNotContain(At(2, 0, 0), around.Cells!);
        Assert.Equal(At(4, 0, 0), around.Cells![around.Cells.Count - 1]);
    }

    [Fact]
    public void ADevicesFreePortCellIsKeptOutOfARouteByDefault()
    {
        RouteRules box = new RouteRules(2.0, AxisOrder.Any, 400, At(-3, -3, -3), At(7, 3, 3));
        FreePortCell[] ports =
        {
            new FreePortCell(At(2, 0, 0), taken: false),
            new FreePortCell(At(3, 0, 0), taken: true),
            new FreePortCell(At(20, 0, 0), taken: false)
        };

        List<GridCell> kept = DevicePortCells.Kept(ports, At(-3, -3, -3), At(7, 3, 3), allowed: false);
        RouteReservation reserved = RouteReservation.Of(kept, new[] { At(0, 0, 0), At(4, 0, 0) });
        RouteResult route = RoutePlanner.Find(RouteEnd.Open(At(0, 0, 0)), RouteEnd.Open(At(4, 0, 0)),
            reserved.Guard(Open), box);

        // The free port in the box is kept; a port with a piece there already and one outside the search are not.
        Assert.Equal(new List<GridCell> { At(2, 0, 0) }, kept);
        Assert.DoesNotContain(At(2, 0, 0), route.Cells!);
        Assert.Empty(DevicePortCells.Kept(ports, At(-3, -3, -3), At(7, 3, 3), allowed: true));
    }
}

/// <summary>move gas's report and the label refusal for in-line tanks.</summary>
public sealed class MoveGasReportAndLabelTests
{
    [Fact]
    public void AReportHasNoTransferIdOrStatusAndSaysItIsAReport()
    {
        MoveGasView view = new MoveGasView(null, MoveGasView.Queued, true, true, null, null,
            new List<MovedGasView> { new MovedGasView("Oxygen", 10.0, 2000.0) }, null);
        JObject json = Replies.Tree(StationGodMCP.Pure.Verbs.GasReplies.Reported(view, "[cannot undo: gas move]"));
        Assert.Null(json["transfer_id"]);
        Assert.Null(json["status"]);
        Assert.True((bool)json["report_only"]!);
        Assert.True((bool)json["predicted"]!);
        Assert.Equal("Oxygen", (string?)json["moved"]![0]!["gas"]);
    }

    [Fact]
    public void AQueuedMoveKeepsItsTransferId()
    {
        MoveGasView view = new MoveGasView("12", MoveGasView.Queued, true, true, null, null,
            new List<MovedGasView>(), null);
        Assert.Equal("12", (string?)JObject.Parse(WireCheck.New(view))["transfer_id"]);
    }

    [Fact]
    public void APipeSizeInLineTankIsRefusedWithItsOwnReason()
    {
        string message = LabelRule.NotLabelable("Tank (Small) (9001)", LabelRule.InLineTankClass);
        Assert.StartsWith("Tank (Small) (9001) is a pipe-size in-line tank", message);
        Assert.Contains("StructureInLineTank", message);
        Assert.Contains("keeps no names of its own", message);
    }

    [Fact]
    public void OtherClassesKeepTheGeneralReason()
    {
        Assert.Equal("Pipe (1) is a Piping; the Labeller cannot rename that class (pipes, cables, frames and " +
                     "ordinary items take no label).", LabelRule.NotLabelable("Pipe (1)", "Piping"));
    }
}
