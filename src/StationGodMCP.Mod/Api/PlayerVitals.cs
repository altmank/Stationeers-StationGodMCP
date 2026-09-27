#nullable enable

using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Clothing;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// player_vitals: the local player's hunger and thirst. Read only.
///
/// The rates copy Human.LifeNutrition and Human.LifeDehydrate, which run once per game tick
/// (GameManager.GameTickSpeedSeconds) from AtmosphericsManager.LifeTicksTick through Entity.OnLifeTick.
/// Entity.OnLifeTick skips both for a robot (IsArtificial) and while the root parent suspends life (ILifeSuspender: a
/// bed, a powered sleeper, a working cryo tube), so the rate right now is then 0. The awake rate is the one out of
/// bed: what the player uses once up and about, 0 only for a robot or a dead body. Both halve while sleeping.
///
/// Human.LifeNutrition: BaseNutritionStorage / (TicksPerThirtyMinutes * 2) per tick, 1.1 times that while Mood is
/// under 0.25, times DifficultySetting.HungerRate and, with the brain offline, OfflineMetabolism.
/// Human.LifeDehydrate: loss + loss * max(0.1, (T - 273.15) / scaleRate) per tick, times HydrationRate and the offline
/// metabolism, where loss and scaleRate are Human's private statics (read, so an update that changes them is
/// followed) and T is the suit's inside while a suit is worn with its helmet closed, otherwise the air at the player.
/// </summary>
internal static class PlayerVitalsApi
{
    private const float LowMood = 0.25f;
    private const float LowMoodHunger = 1.1f;
    private const float SleepingFactor = 0.5f;
    private const float MinimumTemperatureTerm = 0.1f;

    internal static PlayerVitalsView Handle(Args args)
    {
        Human human = PlayerOrigin.RequireHuman();
        DifficultySetting difficulty = DifficultySetting.Current;
        if (difficulty == null)
        {
            throw ApiErrors.Refused("not_ready", "No difficulty setting is loaded yet.");
        }

        DifficultyView rates = new DifficultyView(difficulty.Id, difficulty.HungerRate, difficulty.HydrationRate,
            difficulty.OfflineMetabolism);
        BodyState state = StateOf(human);
        BodyStores stores = new BodyStores(human.Nutrition, human.GetNutritionStorage(), human.Hydration,
            human.GetHydrationStorage(), human.FoodQuality, human.GetFoodQualityMultiplier(), human.Mood);
        VitalsWarningsView warnings = new VitalsWarningsView(human.WarningNutrition, human.CriticalNutrition,
            human.WarningHydration, human.CriticalHydration);
        return new PlayerVitalsView(GameLookup.ViewOf(human), stores, state, Drains(human, state, rates), rates,
            warnings);
    }

    private static BodyState StateOf(Human human)
    {
        bool brainOnline = human.OrganBrain != null && human.OrganBrain.IsOnline;
        bool suspended = human.RootParent is ILifeSuspender suspender && suspender.IsSuspendingLife;
        bool suitWorn = human.SuitSlot != null && human.SuitSlot.Contains<ISuit>();
        bool? helmetClosed = human.HelmetSlot != null && human.HelmetSlot.Contains<GasMask>(out GasMask helmet)
            ? !helmet.IsOpen
            : null;
        return new BodyState(human.IsSleeping, brainOnline, human.IsArtificial, suspended, human.IsDead, suitWorn,
            helmetClosed);
    }

    private static VitalsRates Drains(Human human, BodyState state, DifficultyView difficulty)
    {
        float tickSeconds = GameManager.GameTickSpeedSeconds;
        float metabolism = state.BrainOnline ? 1f : difficulty.OfflineMetabolism;
        float sleepFactor = state.Sleeping ? SleepingFactor : 1f;
        DrainClock clock = new DrainClock(tickSeconds, !state.Robot && !state.Dead, state.LifeSuspended, sleepFactor);

        float moodFactor = human.Mood < LowMood ? LowMoodHunger : 1f;
        float hungerPerTick = human.BaseNutritionStorage / (GameManager.TicksPerThirtyMinutes * 2f) * moodFactor *
                              difficulty.HungerRate * metabolism;
        DrainView hunger = clock.Drain(human.Nutrition, hungerPerTick,
            new HungerFactorsView(moodFactor, metabolism, sleepFactor));

        float lossPerTick = (float)GameMembers.HumanHydrationLossPerTick.GetValue(null)!;
        int scaleRate = (int)GameMembers.HumanThirstScaleRate.GetValue(null)!;
        ThirstAir air = ThirstAir.Of(human);
        double zeroCelsius = Chemistry.Temperature.ZeroDegrees.ToDouble();
        DrainView? thirst = null;
        if (air.Kelvin.HasValue)
        {
            float term = Mathf.Max(MinimumTemperatureTerm, (float)((air.Kelvin.Value - zeroCelsius) / scaleRate));
            float thirstPerTick = (lossPerTick + lossPerTick * term) * difficulty.HydrationRate * metabolism;
            thirst = clock.Drain(human.Hydration, thirstPerTick,
                new ThirstFactorsView(1f + term, metabolism, sleepFactor));
        }

        ThirstTemperatureView temperature = new ThirstTemperatureView(air.Kelvin, air.Source);
        return new VitalsRates(temperature, hunger, thirst, tickSeconds);
    }
}

/// <summary>Turns a per-tick drain into rates per game second, now and when up and about.</summary>
internal sealed class DrainClock
{
    private readonly float _tickSeconds;
    private readonly bool _drainsAwake;
    private readonly bool _suspended;
    private readonly float _sleepFactor;

    internal DrainClock(float tickSeconds, bool drainsAwake, bool suspended, float sleepFactor)
    {
        _tickSeconds = tickSeconds;
        _drainsAwake = drainsAwake;
        _suspended = suspended;
        _sleepFactor = sleepFactor;
    }

    internal DrainView Drain(float store, float perTickAwake, object factors)
    {
        double awake = _drainsAwake ? perTickAwake / _tickSeconds : 0.0;
        double now = _drainsAwake && !_suspended ? awake * _sleepFactor : 0.0;
        return new DrainView(store, awake, now, factors);
    }
}

/// <summary>The air whose temperature thirst depends on, and where it was taken.</summary>
internal sealed class ThirstAir
{
    private ThirstAir(double? kelvin, string source)
    {
        Kelvin = kelvin;
        Source = source;
    }

    internal double? Kelvin { get; }

    /// <summary>suit, room, world or none.</summary>
    internal string Source { get; }

    internal static ThirstAir Of(Human human)
    {
        if (human.SuitSlot != null && human.SuitSlot.Contains<ISuit>(out ISuit suit) &&
            suit.InternalAtmosphere != null && human.HelmetSlot != null &&
            human.HelmetSlot.Contains<GasMask>(out GasMask helmet) && !helmet.IsOpen)
        {
            return new ThirstAir(suit.InternalAtmosphere.Temperature.ToDouble(), "suit");
        }

        GridController? grid = GridController.World;
        AtmosphericsController? atmospherics = grid != null ? grid.AtmosphericsController : null;
        Atmosphere? around = atmospherics?.SampleGlobalAtmosphere(human.WorldGrid);
        if (around == null)
        {
            return new ThirstAir(null, "none");
        }

        return new ThirstAir(around.Temperature.ToDouble(), around.Room != null ? "room" : "world");
    }
}
