#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// move gas's replies in the catalogue's keys (a queued move, a deleted one, a room side, a failed or waiting move as
/// inspect job answers them) and its report: the prediction without transfer_id and status, one target per side.
/// </summary>
public sealed class MoveGasReplyTests
{
    private static object OldAtmosphere(double pressure) => new
    {
        pressure_kpa = pressure, temperature_k = 293.15, total_mol = 12.5,
        gases = new List<object> { new { gas = "Oxygen", amount_mol = 12.5 } }
    };

    private static AtmosphereView NewAtmosphere(double pressure) => new AtmosphereView(pressure, 293.15, 12.5,
        new List<GasAmountView> { new GasAmountView("Oxygen", 12.5) });

    private static object OldSide() => new
    {
        members = new List<object>
        {
            new
            {
                owner = new
                {
                    kind = "thing", reference_id = "10", prefab_name = "ItemGasCanisterOxygen",
                    display_name = "Canister"
                },
                atmosphere_id = "11", before = OldAtmosphere(100.0), after = OldAtmosphere(50.0)
            },
            new
            {
                owner = new
                {
                    kind = "pipe_network", reference_id = "20", prefab_name = (string?)null,
                    display_name = "Pipe Network"
                },
                atmosphere_id = "21", before = OldAtmosphere(100.0), after = OldAtmosphere(50.0)
            }
        },
        total = new { before = OldAtmosphere(100.0), after = OldAtmosphere(50.0), after_boiling = (object?)null },
        joined_by = new List<object>
        {
            new { reference_id = "30", prefab_name = "StructureGasTankStorage", display_name = "Tank Storage" }
        },
        room = (object?)null
    };

    private static GasSideView NewSide() => new GasSideView(
        new List<GasMemberView>
        {
            new GasMemberView(new GasOwnerView("thing", new ThingId(10), "ItemGasCanisterOxygen", "Canister"),
                new ThingId(11), NewAtmosphere(100.0), NewAtmosphere(50.0)),
            new GasMemberView(new GasOwnerView("pipe_network", new ThingId(20), null, "Pipe Network"),
                new ThingId(21), NewAtmosphere(100.0), NewAtmosphere(50.0))
        },
        new GasTotalView(NewAtmosphere(100.0), NewAtmosphere(50.0), null),
        new List<ThingView> { new ThingView(new ThingId(30), "StructureGasTankStorage", "Tank Storage") }, null);

    [Fact]
    public void AQueuedMoveAnswersItsPrediction()
    {
        var old = new
        {
            transfer_id = "7", status = "queued", predicted = true, joined = true, from = OldSide(), to = OldSide(),
            deleted = false,
            moved = new List<object> { new { gas = "Oxygen", amount_mol = 6.25, energy_j = 1200.5 } },
            error = (object?)null
        };
        MoveGasView view = new MoveGasView("7", "queued", true, true, NewSide(), NewSide(),
            new List<MovedGasView> { new MovedGasView("Oxygen", 6.25, 1200.5) }, null);
        WireCheck.Same(old, view);
        Assert.Equal(new[] { "transfer_id", "status", "predicted", "joined", "from", "to", "deleted", "moved", "error" },
            GasReplies.Acted(view).Properties().Select(key => key.Name));
    }

    [Fact]
    public void AReportIsThePredictionWithoutTransferIdOrStatusAndASideATarget()
    {
        MoveGasView predicted = new MoveGasView(null, "queued", true, true, NewSide(), RoomSide(),
            new List<MovedGasView> { new MovedGasView("Oxygen", 6.25, 1200.5) }, null);

        JObject report = GasReplies.Reported(predicted, "[cannot undo: gas move]");

        Assert.Equal(new[]
        {
            "predicted", "joined", "from", "to", "deleted", "moved", "error", "problems", "report_only", "targets"
        }, report.Properties().Select(key => key.Name));
        JArray targets = (JArray)report["targets"]!;
        Assert.Equal("[10, Canister, 12.5 -> 12.5] [cannot undo: gas move]", (string?)targets[0]!["text"]);
        Assert.Equal("123", (string?)targets[1]!["id"]);
        Assert.Equal("room 123 (None)", (string?)targets[1]!["name"]);
    }

    [Fact]
    public void APlanetReportNamesThePlanetsAirAsItsTarget()
    {
        JObject removal = new JObject { ["status"] = "queued", ["from"] = "planet", ["amount_mol"] = 5.0, ["ticks"] = 1 };

        JObject report = GasReplies.ReportedPlanet(removal, 20.0, 15.0, "[cannot undo: gas move]");

        Assert.Null(report["status"]);
        Assert.Equal("[planet, the planet's air, 20 -> 15] [cannot undo: gas move]",
            (string?)Assert.Single((JArray)report["targets"]!)["text"]);
    }

    private static GasSideView RoomSide() => new GasSideView(new List<GasMemberView>(),
        new GasTotalView(NewAtmosphere(101.0), NewAtmosphere(95.5), null), new List<ThingView>(),
        new GasRoomView("123", "None", 40, 38, 304000.0));

    [Fact]
    public void ADeletedMoveHasNoTo()
    {
        var old = new
        {
            transfer_id = "8", status = "applied", predicted = false, joined = false, from = OldSide(),
            to = (object?)null, deleted = true, moved = new List<object>(), error = (object?)null
        };
        WireCheck.Same(old,
            new MoveGasView("8", "applied", false, false, NewSide(), null, new List<MovedGasView>(), null));
    }

    [Fact]
    public void LandingPadOwnerAndBoiledTotal()
    {
        var old = new
        {
            owner = new
            {
                kind = "landing_pad_network", reference_id = "40", prefab_name = (string?)null,
                display_name = "Landing Pad Network"
            },
            atmosphere_id = "41", before = OldAtmosphere(900.0), after = OldAtmosphere(0.0)
        };
        WireCheck.Same(old, new GasMemberView(
            new GasOwnerView("landing_pad_network", new ThingId(40), null, "Landing Pad Network"), new ThingId(41),
            NewAtmosphere(900.0), NewAtmosphere(0.0)));
        WireCheck.Same(
            new { before = OldAtmosphere(100.0), after = OldAtmosphere(110.0), after_boiling = OldAtmosphere(450.0) },
            new GasTotalView(NewAtmosphere(100.0), NewAtmosphere(110.0), NewAtmosphere(450.0)));
    }

    [Fact]
    public void FailedAndWaitingMovesAsInspectJobAnswersThem()
    {
        var failed = new
        {
            transfer_id = "9", status = "failed", predicted = false, joined = false, from = (object?)null,
            to = (object?)null, deleted = false, moved = new List<object>(),
            error = new { code = "atmosphere_not_found", message = "Gone." }
        };
        WireCheck.Same(failed, MoveGasView.Failed("9", new ErrorView("atmosphere_not_found", "Gone.")));
        WireCheck.Same(new { transfer_id = "10", status = "queued" }, new GasMoveWaitingView("10"));
    }

    [Fact]
    public void ARoomSideNamesItsRoom()
    {
        var old = new
        {
            members = new List<object>(),
            total = new { before = OldAtmosphere(101.0), after = OldAtmosphere(95.5), after_boiling = (object?)null },
            joined_by = new List<object>(),
            room = new
            {
                room_id = "123", room_type = "None", cell_count = 40, cells_with_air = 38, volume_l = 304000.0
            }
        };
        WireCheck.Same(old, new GasSideView(new List<GasMemberView>(),
            new GasTotalView(NewAtmosphere(101.0), NewAtmosphere(95.5), null), new List<ThingView>(),
            new GasRoomView("123", "None", 40, 38, 304000.0)));
    }
}
