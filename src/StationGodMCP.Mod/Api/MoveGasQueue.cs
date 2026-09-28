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
    private const int MaximumPending = 64;
    private const int KeptOutcomes = 64;

    private static readonly object Gate = new object();
    private static readonly Queue<PendingGasMove> Pending = new Queue<PendingGasMove>();
    private static readonly Dictionary<long, MoveGasView> Outcomes = new Dictionary<long, MoveGasView>();
    private static readonly Queue<long> OutcomeOrder = new Queue<long>();
    private static long _nextId;

    internal static long Enqueue(GasPlan plan)
    {
        lock (Gate)
        {
            if (Pending.Count >= MaximumPending)
            {
                throw ApiErrors.Refused("busy",
                    $"{MaximumPending} moves are already waiting for the next atmospherics tick " +
                    "(is the game paused?).");
            }

            long id = ++_nextId;
            Pending.Enqueue(new PendingGasMove(id, plan));
            return id;
        }
    }

    internal static object Outcome(long id)
    {
        lock (Gate)
        {
            if (Outcomes.TryGetValue(id, out MoveGasView outcome))
            {
                return outcome;
            }

            foreach (PendingGasMove move in Pending)
            {
                if (move.Id == id)
                {
                    return new GasMoveWaitingView(id.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        throw ApiErrors.Refused("transfer_not_found",
            $"No move {id} is waiting or among the last {KeptOutcomes} applied.");
    }

    // Called by the Harmony postfix on the atmospherics thread, where Mole getters are live and no atmospherics job
    // runs yet. Never throws into the game's tick.
    internal static void ApplyPending()
    {
        if (!ThreadedManager.IsThread)
        {
            return;
        }

        while (TryDequeue(out PendingGasMove? move))
        {
            Keep(move!.Id, ApplySafely(move));
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

    private static bool TryDequeue(out PendingGasMove? move)
    {
        lock (Gate)
        {
            if (Pending.Count > 0)
            {
                move = Pending.Dequeue();
                return true;
            }
        }

        move = null;
        return false;
    }

    private static void Keep(long id, MoveGasView outcome)
    {
        lock (Gate)
        {
            Outcomes[id] = outcome;
            OutcomeOrder.Enqueue(id);
            while (OutcomeOrder.Count > KeptOutcomes)
            {
                Outcomes.Remove(OutcomeOrder.Dequeue());
            }
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
