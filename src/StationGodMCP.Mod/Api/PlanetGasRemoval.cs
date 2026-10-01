#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;

namespace StationGodMCP.Api;

/// <summary>
/// move_gas from "planet" with delete: take named gases out of the planet's own air. Writes. Needs Terraforming
/// Reloaded: without it the stock game keeps the planet read-only (PlanetaryAtmosphereSimulation.IsGlobalInteraction
/// is false), so there is nothing a removal could change for good.
///
/// The store (CODE, Assets.Scripts.PlanetaryAtmosphereSimulation, GlobalGasMix): the planet's air is the static
/// GlobalGasMix _globalGasMix, the mix Terraforming Reloaded reads through GetGlobalGasMix; the clouds and ice caps
/// are three more GlobalGasMix reservoirs. GlobalGasMix holds one MoleQuantity per gas and no energy, so removing a
/// gas is GlobalGasMix.Remove(GasMixture) with that gas's quantity (the only side effect is its debug counter
/// _removed). The game takes PlanetaryAtmosphereSimulation.GlobalInteraction around every change to the mix, and so
/// does this, on the atmospherics thread at the start of its tick (the move_gas postfix on HandleMainThreadEvents).
///
/// Give-back (LIVE 2026-09-28, CE): outdoor cells hand a residual of the gas back to the planet for a few ticks after
/// it is cleared. A removal of all of a gas (no amount_mol) is therefore applied again every tick for
/// SweepTicks ticks. Terraforming Reloaded keeps no per-gas baseline (only GasLostToSpaceMoles), so nothing puts the
/// gas back afterwards.
/// </summary>
internal static class PlanetGasRemoval
{
    private const int SweepTicks = 30;

    private static readonly object Gate = new object();
    private static readonly List<PendingPlanetRemoval> Pending = new List<PendingPlanetRemoval>();

    // The arguments are checked before the mod, so a malformed call answers the same with or without it.
    internal static object Handle(Args args)
    {
        args.Reject("planet", "to", "force", "joined", "transfer_id", "dry_run");
        if (args.OptionalBool("delete") != true)
        {
            throw ApiErrors.InvalidArgument("From 'planet' only delete: true is supported.");
        }

        if (!args.Has("gases"))
        {
            throw ApiErrors.InvalidArgument("Name the gases to take out of the planet's air ('gases'); " +
                                            "removing all of it is refused.");
        }

        Chemistry.GasType[] gases = GasMoveRequest.ParseGases(args.Array("gases", 64));
        double? amountMol = args.OptionalPositiveDouble("amount_mol");
        if (GameMembers.TerraformingGate.OrNull == null)
        {
            throw ApiErrors.Refused("terraforming_mod_required",
                "Removing gas from the planet needs Terraforming Reloaded: the stock game keeps the planet's air " +
                "read-only, so a removal would not last.");
        }

        GlobalGasMix planet = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
        List<PlanetGasLine> before = new List<PlanetGasLine>();
        foreach (Chemistry.GasType gas in gases)
        {
            before.Add(new PlanetGasLine(gas.ToString(), planet.Get(gas).ToDouble()));
        }

        lock (Gate)
        {
            Pending.Add(new PendingPlanetRemoval(gases, amountMol, amountMol == null ? SweepTicks : 1));
        }

        return new PlanetGasRemovalView(before, amountMol, amountMol == null ? SweepTicks : 1);
    }

    /// <summary>The world was left: removals still sweeping its planet stop.</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            Pending.Clear();
        }
    }

    // On the atmospherics thread, from the move_gas tick postfix. Never throws into the game's tick.
    internal static void ApplyPending()
    {
        if (!ThreadedManager.IsThread)
        {
            return;
        }

        PendingPlanetRemoval[] work;
        lock (Gate)
        {
            if (Pending.Count == 0)
            {
                return;
            }

            work = Pending.ToArray();
        }

        foreach (PendingPlanetRemoval removal in work)
        {
            try
            {
                removal.ApplyOnce();
            }
            catch (Exception exception)
            {
                removal.Fail(exception.Message);
            }
        }

        lock (Gate)
        {
            Pending.RemoveAll(removal => removal.Finished);
        }
    }

    internal static GlobalGasMix?[] Stores() => new[]
    {
        PlanetaryAtmosphereSimulation.GetGlobalGasMix(),
        GameMembers.PlanetLiquidClouds.GetValue(null) as GlobalGasMix,
        GameMembers.PlanetIceClouds.GetValue(null) as GlobalGasMix,
        GameMembers.PlanetIceCaps.GetValue(null) as GlobalGasMix,
    };
}

/// <summary>One planet removal: named gases, all of them (swept for several ticks) or an amount (once).</summary>
internal sealed class PendingPlanetRemoval
{
    private readonly Chemistry.GasType[] _gases;
    private readonly double? _amountMol;
    private int _ticksLeft;

    internal PendingPlanetRemoval(Chemistry.GasType[] gases, double? amountMol, int ticks)
    {
        _gases = gases;
        _amountMol = amountMol;
        _ticksLeft = ticks;
    }

    internal bool Finished => _ticksLeft <= 0;

    internal void ApplyOnce()
    {
        object gate = GameMembers.PlanetGlobalInteraction.GetValue(null);
        lock (gate)
        {
            GlobalGasMix?[] stores = PlanetGasRemoval.Stores();
            for (int i = 0; i < stores.Length; i++)
            {
                GlobalGasMix? store = stores[i];
                if (store == null || (_amountMol != null && i > 0))
                {
                    continue; // an amount comes out of the planet's air only; a full removal clears every store
                }

                GasMixture take = GasMixtureHelper.Create();
                foreach (Chemistry.GasType gas in _gases)
                {
                    double have = store.Get(gas).ToDouble();
                    double amount = _amountMol == null ? have : Math.Min(have, _amountMol.Value);
                    if (amount > 0.0)
                    {
                        take.Add(new Mole(gas, new MoleQuantity(amount), MoleEnergy.Zero));
                    }
                }

                store.Remove(take);
            }
        }

        _ticksLeft--;
    }

    internal void Fail(string message)
    {
        _ticksLeft = 0;
        UnityEngine.Debug.LogWarning("[StationGodMCP] planet gas removal failed: " + message);
    }
}

internal sealed class PlanetGasLine
{
    internal PlanetGasLine(string gas, double amountMol)
    {
        Gas = gas;
        AmountMol = amountMol;
    }

    public string Gas { get; }

    public double AmountMol { get; }
}

internal sealed class PlanetGasRemovalView
{
    internal PlanetGasRemovalView(List<PlanetGasLine> before, double? amountMol, int ticks)
    {
        Before = before;
        AmountMol = amountMol;
        Ticks = ticks;
    }

    public string Status => "queued";

    public string From => "planet";

    public List<PlanetGasLine> Before { get; }

    public double? AmountMol { get; }

    public int Ticks { get; }

    public string Note =>
        "Applied on the next atmospherics ticks; read the planet tool to see the result. A removal of all of a gas " +
        "is repeated for " + Ticks + " ticks to take back what outdoor cells hand back, and also clears the clouds " +
        "and ice caps.";
}
