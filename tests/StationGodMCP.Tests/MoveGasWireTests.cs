#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>move_gas: the old StationApi.GasMove shapes against the new views; no renames.</summary>
public sealed class MoveGasWireTests
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
        total = new { before = OldAtmosphere(100.0), after = OldAtmosphere(50.0) },
        joined_by = new List<object>
        {
            new { reference_id = "30", prefab_name = "StructureGasTankStorage", display_name = "Tank Storage" }
        }
    };

    private static GasSideView NewSide() => new GasSideView(
        new List<GasMemberView>
        {
            new GasMemberView(new GasOwnerView("thing", new ThingId(10), "ItemGasCanisterOxygen", "Canister"),
                new ThingId(11), NewAtmosphere(100.0), NewAtmosphere(50.0)),
            new GasMemberView(new GasOwnerView("pipe_network", new ThingId(20), null, "Pipe Network"),
                new ThingId(21), NewAtmosphere(100.0), NewAtmosphere(50.0))
        },
        new GasTotalView(NewAtmosphere(100.0), NewAtmosphere(50.0)),
        new List<ThingView> { new ThingView(new ThingId(30), "StructureGasTankStorage", "Tank Storage") });

    [Fact]
    public void QueuedMoveSameWire()
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
    }

    [Fact]
    public void DeletedMoveSameWire()
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
    public void FailedAndWaitingSameWire()
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
}
