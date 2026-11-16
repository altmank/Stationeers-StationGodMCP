#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// write needs: a player's nutrition, hydration and waste set, and read back.
///
/// Nutrition and hydration are set through Entity.Nutrition and Entity.Hydration, the properties the console's
/// player setnutrition and sethydration set (PlayerCommand.cs:80-115): each clamps to its range and raises network
/// flag 1024 on the server. A share is of Entity.GetNutritionStorage (BaseNutritionStorage, 50 for a human) for
/// nutrition, and of the 5 that Entity.HydrationRatio divides by for hydration, whose clamp 8.75 is a share of 1.75.
///
/// Waste is the stomach's PollutedWater (Stomach.cs): its ratio is the moles times Chemistry.MolarVolumeLiquid over the
/// capacity (Volume times 0.5, 5 L), clamped to 0..1, and 0 with Sanitation off. The game changes stomach gas through
/// atmospheric events (Stomach.OnLifeTick: AtmosphericEventInstance.CreateAdd and CreateRemove, applied at the start of
/// the next atmospherics tick, on the worker thread), so the waste is set the same way: an add or remove of the
/// difference at body temperature. The read-back steps over frames until the ratio reads what was asked, at most
/// ReadBackSeconds.
/// </summary>
internal static class PlayerNeeds
{
    internal const float ReadBackSeconds = 2f;

    /// <summary>Entity.HydrationRatio's divisor.</summary>
    internal const float HydrationDivisor = 5f;

    /// <summary>The player named (by name or id, in the game or not), else the player; refused on a client or when dead.</summary>
    internal static Human Require(string? wanted)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host sets needs.");
        }

        Human player = wanted == null ? PlayerOrigin.RequireHuman() : MovablePlayers.Find(wanted);
        if (!PlayerOrigin.IsAlive(player))
        {
            throw ApiErrors.Refused("player_dead",
                $"{player.DisplayName} is dead ({player.State}); a dead body has no needs to set.");
        }

        return player;
    }

    /// <summary>Every check, before anything is set: throws the refusal one of them gives.</summary>
    internal static void Check(Human player, NeedsRequest request)
    {
        BodyFacts body = new BodyFacts(player.OrganStomach != null, SanitationOn());
        if (NeedsCheck.Of(request, body) is NeedsRefused refused)
        {
            throw ApiErrors.Refused(refused.Code, refused.Message);
        }
    }

    /// <summary>The needs as they read now, with the asked values as the afters: the report's reading.</summary>
    internal static NeedsWrittenView Reading(Human player, NeedsRequest request)
    {
        NeedLevels now = LevelsOf(player);
        NeedChange nutrition = new NeedChange(now.Nutrition, request.Nutrition ?? now.Nutrition, now.NutritionCapacity);
        NeedChange hydration = new NeedChange(now.Hydration, request.Hydration ?? now.Hydration, HydrationDivisor);
        WasteChange? waste = now.Waste is { } before ? new WasteChange(before, request.Waste ?? before) : null;
        return View(player, nutrition, hydration, waste, request);
    }

    /// <summary>The needs set (nutrition and hydration at once, the waste's event queued), then read back over frames.</summary>
    internal static VerbOutcome Write(Human player, NeedsRequest request)
    {
        NeedLevels before = LevelsOf(player);
        if (request.Nutrition is { } nutrition)
        {
            player.Nutrition = (float)(nutrition * before.NutritionCapacity);
        }

        if (request.Hydration is { } hydration)
        {
            player.Hydration = (float)(hydration * HydrationDivisor);
        }

        if (request.Waste is { } waste && player.OrganStomach != null)
        {
            QueueWaste(player.OrganStomach, waste);
        }

        return WorkResult<NeedsWrittenView>.Of(answer => ReadingBack(player, request, before, answer),
            reading => reading);
    }

    private static IEnumerable<object?> ReadingBack(Human player, NeedsRequest request, NeedLevels before,
        Answer<NeedsWrittenView> answer)
    {
        float startedAt = Time.realtimeSinceStartup;
        while (request.Waste is { } asked && player.OrganStomach != null &&
               !WasteReadBack.Reached(asked, player.OrganStomach.GetWasteRatio()) &&
               Time.realtimeSinceStartup - startedAt < ReadBackSeconds)
        {
            yield return null;
        }

        NeedLevels after = LevelsOf(player);
        NeedChange nutrition = new NeedChange(before.Nutrition, after.Nutrition, before.NutritionCapacity);
        NeedChange hydration = new NeedChange(before.Hydration, after.Hydration, HydrationDivisor);
        WasteChange? waste = after.Waste is { } read ? new WasteChange(before.Waste ?? read, read) : null;
        answer.Value = View(player, nutrition, hydration, waste, request);
    }

    private static NeedsWrittenView View(Human player, NeedChange nutrition, NeedChange hydration, WasteChange? waste,
        NeedsRequest request) =>
        new NeedsWrittenView(GameLookup.ViewOf(player), nutrition, hydration, waste, SanitationOn(),
            NeedsWarnings.Of(request));

    // Queues the add or remove that takes the stomach's PollutedWater to the ratio asked, at body temperature, as
    // Stomach.OnLifeTick queues its own: the difference from the moles read now.
    private static void QueueWaste(Stomach stomach, double ratio)
    {
        Atmosphere atmosphere = stomach.InternalAtmosphere;
        double litresPerMole = Chemistry.MolarVolumeLiquid(Chemistry.GasType.PollutedWater).ToFloat();
        double current = atmosphere.GasMixture.PollutedWater.Quantity.ToDouble();
        double change = WasteMoles.Difference(current, WasteMoles.MolesFor(ratio, litresPerMole));
        if (change == 0.0)
        {
            return;
        }

        MoleQuantity moles = new MoleQuantity(System.Math.Abs(change));
        MoleEnergy energy = IdealGas.Energy(Entity.BodyTemperature,
            Mole.SpecificHeat(Chemistry.GasType.PollutedWater), moles);
        GasMixture mixture = new GasMixture(new Mole(Chemistry.GasType.PollutedWater, moles, energy));
        if (change > 0.0)
        {
            AtmosphericEventInstance.CreateAdd(atmosphere, mixture);
        }
        else
        {
            AtmosphericEventInstance.CreateRemove(atmosphere, mixture);
        }
    }

    private static NeedLevels LevelsOf(Human player)
    {
        float capacity = player.GetNutritionStorage();
        double? waste = player.OrganStomach != null && SanitationOn() ? player.OrganStomach.GetWasteRatio() : null;
        return new NeedLevels(player.Nutrition / capacity, capacity, player.Hydration / HydrationDivisor, waste);
    }

    private static bool SanitationOn() => DifficultySetting.Current != null && DifficultySetting.Current.Sanitation;

    private sealed record NeedLevels(double Nutrition, double NutritionCapacity, double Hydration, double? Waste);
}
