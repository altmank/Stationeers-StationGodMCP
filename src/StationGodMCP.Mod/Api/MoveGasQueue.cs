#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using HarmonyLib;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// move_gas's pending moves, applied on the atmospherics thread by a postfix on
/// AtmosphericsController.HandleMainThreadEvents, and the recent outcomes.
/// </summary>
internal static class GasMoves
{
    private static readonly TransferLedger<PendingGasMove, MoveGasView> Ledger =
        new TransferLedger<PendingGasMove, MoveGasView>(64, 64);

    internal static long Enqueue(GasPlan plan) =>
        Ledger.TryEnqueue(id => new PendingGasMove(id, plan)) ??
        throw ApiErrors.Refused("busy",
            $"{Ledger.MaximumPending} moves are already waiting for the next atmospherics tick " +
            "(is the game paused?).");

    // A move being applied reads as queued until its outcome is kept.
    internal static object Outcome(long id) =>
        Ledger.Find(id) switch
        {
            TransferState<MoveGasView>.Done done => done.Outcome,
            TransferState<MoveGasView>.Waiting => new GasMoveWaitingView(id.ToString(CultureInfo.InvariantCulture)),
            _ => throw ApiErrors.Refused("transfer_not_found",
                $"No move {id} is waiting or among the last {Ledger.KeptOutcomes} applied.")
        };

    // Called by the Harmony postfix on the atmospherics thread, where Mole getters are live and no atmospherics job
    // runs yet. Never throws into the game's tick.
    internal static void ApplyPending()
    {
        if (!ThreadedManager.IsThread)
        {
            return;
        }

        while (Ledger.TryBegin(out long id, out PendingGasMove? move))
        {
            Ledger.Complete(id, ApplySafely(move!));
        }
    }

    private static MoveGasView ApplySafely(PendingGasMove move)
    {
        string id = move.Id.ToString(CultureInfo.InvariantCulture);
        try
        {
            return move.Apply();
        }
        catch (ApiException exception)
        {
            return MoveGasView.Failed(id, new ErrorView(exception.Code, exception.Message));
        }
        catch (Exception exception)
        {
            // GasMixture.Remove and GasMixture.Add run inside the game's atmospherics tick; nothing may escape it.
            return MoveGasView.Failed(id, new ErrorView("move_failed", exception.Message));
        }
    }
}

/// <summary>One queued move, applied with the game's GasMixture calls on the atmospherics thread.</summary>
internal sealed class PendingGasMove
{
    internal PendingGasMove(long id, GasPlan plan)
    {
        Id = id;
        Plan = plan;
    }

    internal long Id { get; }

    internal GasPlan Plan { get; }

    // The sides are the sets found when the move was asked for; a join made or broken within that half second is not
    // seen. Before and after are read live here.
    internal MoveGasView Apply()
    {
        GasSide source = Plan.Source;
        GasSide? target = Plan.Target;
        if (source.AnyGone() || (target != null && target.AnyGone()))
        {
            throw ApiErrors.Refused("atmosphere_not_found",
                "An atmosphere of this move was destroyed before the next atmospherics tick.");
        }

        GasSnapshot[] sourceBefore = source.Live();
        GasSnapshot[]? targetBefore = target?.Live();
        List<MovedGasView> moved = new List<MovedGasView>();
        foreach (Chemistry.GasType gas in Plan.Request.Gases)
        {
            MovedGasView? one = MoveOne(gas, source.For(gas), target);
            if (one != null)
            {
                moved.Add(one);
            }
        }

        if (moved.Count == 0)
        {
            throw ApiErrors.Refused("nothing_to_move",
                "By the time the move was applied the source held none of the requested gases; nothing moved.");
        }

        GasSideView? to = target == null ? null : AppliedView(target, targetBefore!);
        return new MoveGasView(Id.ToString(CultureInfo.InvariantCulture), "applied", false, Plan.Request.Joined,
            AppliedView(source, sourceBefore), to, moved, null);
    }

    private static GasSideView AppliedView(GasSide side, GasSnapshot[] before)
    {
        for (int index = 0; index < before.Length; index++)
        {
            side.Before[index] = before[index];
        }

        return side.ToView(side.Live());
    }

    // Every member of the set still there gives its share: GasMixture.Remove(GasType, MoleQuantity) returns the
    // removed Mole with its share of that member's energy. The target receives the sum, moles and energy together
    // (GasMixture.Add), where its place puts it: the named atmosphere, or a room's cells by volume. Deleting drops it.
    private MovedGasView? MoveOne(Chemistry.GasType gas, JoinedSet set, GasSide? target)
    {
        List<GasEnd> live = set.Members.FindAll(member => !member.IsGone);
        double[] held = new double[live.Count];
        for (int index = 0; index < held.Length; index++)
        {
            held[index] = live[index].Atmosphere.GasMixture.GetMoleValue(gas).Quantity.ToDouble();
        }

        double[] taken = GasShares.Proportional(held, Plan.Request.AmountMol);
        double movedMoles = 0.0;
        double movedEnergy = 0.0;
        for (int index = 0; index < taken.Length; index++)
        {
            if (!(taken[index] > 0.0))
            {
                continue;
            }

            Mole removed = live[index].Atmosphere.GasMixture.Remove(gas, new MoleQuantity(taken[index]));
            if (removed.IsValid)
            {
                movedMoles += removed.Quantity.ToDouble();
                movedEnergy += removed.Energy.ToDouble();
            }
        }

        if (!(movedMoles > 0.0))
        {
            return null;
        }

        target?.Deliver(gas, movedMoles, movedEnergy);
        return new MovedGasView(gas.ToString(), movedMoles, movedEnergy);
    }
}

[HarmonyPatch(typeof(AtmosphericsController), nameof(AtmosphericsController.HandleMainThreadEvents))]
internal static class GasMovesTickPatch
{
    private static void Postfix()
    {
        GasMoves.ApplyPending();
        PlanetGasRemoval.ApplyPending();
    }
}
