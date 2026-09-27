#nullable enable

using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>player_vitals: the local player's stores, state, and hunger and thirst drain.</summary>
internal sealed class PlayerVitalsView
{
    private const string NoteText =
        "Rates are per game second, from the game's own per-tick formulas. per_second is the rate right now: 0 while " +
        "lying in a bed, powered sleeper or working cryo tube (the game skips hunger and thirst there) and halved " +
        "while sleeping elsewhere. awake_per_second is the rate when up and about. time_left_s is the store in the " +
        "body divided by the current rate: food and water carried or stored are not counted here (see consumables " +
        "and water_sources).";

    internal PlayerVitalsView(ThingView player, BodyStores stores, BodyState state, VitalsRates rates,
        DifficultyView difficulty, VitalsWarningsView warnings)
    {
        ReferenceId = player.ReferenceId;
        DisplayName = player.DisplayName;
        Nutrition = stores.Nutrition;
        NutritionCapacity = stores.NutritionCapacity;
        Hydration = stores.Hydration;
        HydrationCapacity = stores.HydrationCapacity;
        FoodQuality = stores.FoodQuality;
        FoodQualityMultiplier = stores.FoodQualityMultiplier;
        Mood = stores.Mood;
        _state = state;
        ThirstTemperature = rates.ThirstTemperature;
        Difficulty = difficulty;
        Hunger = rates.Hunger;
        Thirst = rates.Thirst;
        Warnings = warnings;
        GameTickS = rates.GameTickS;
    }

    private readonly BodyState _state;

    public ThingId ReferenceId { get; }

    public string? DisplayName { get; }

    public float Nutrition { get; }

    public float NutritionCapacity { get; }

    public float Hydration { get; }

    /// <summary>Entity.GetHydrationStorage: 5 times the food quality multiplier; drinking stops there.</summary>
    public float HydrationCapacity { get; }

    public float FoodQuality { get; }

    public float FoodQualityMultiplier { get; }

    public float Mood { get; }

    public bool Sleeping => _state.Sleeping;

    public bool BrainOnline => _state.BrainOnline;

    public bool Robot => _state.Robot;

    public bool LifeSuspended => _state.LifeSuspended;

    public bool Dead => _state.Dead;

    public bool SuitWorn => _state.SuitWorn;

    public bool? HelmetClosed => _state.HelmetClosed;

    public ThirstTemperatureView ThirstTemperature { get; }

    public DifficultyView Difficulty { get; }

    public DrainView Hunger { get; }

    /// <summary>Null when no atmosphere is found around the player: the game cannot compute thirst then.</summary>
    public DrainView? Thirst { get; }

    public VitalsWarningsView Warnings { get; }

    public float GameTickS { get; }

    public string Note => NoteText;
}

/// <summary>What the body holds.</summary>
internal sealed class BodyStores
{
    internal BodyStores(float nutrition, float nutritionCapacity, float hydration, float hydrationCapacity,
        float foodQuality, float foodQualityMultiplier, float mood)
    {
        Nutrition = nutrition;
        NutritionCapacity = nutritionCapacity;
        Hydration = hydration;
        HydrationCapacity = hydrationCapacity;
        FoodQuality = foodQuality;
        FoodQualityMultiplier = foodQualityMultiplier;
        Mood = mood;
    }

    internal float Nutrition { get; }

    internal float NutritionCapacity { get; }

    internal float Hydration { get; }

    internal float HydrationCapacity { get; }

    internal float FoodQuality { get; }

    internal float FoodQualityMultiplier { get; }

    internal float Mood { get; }
}

/// <summary>The player's state flags.</summary>
internal sealed class BodyState
{
    internal BodyState(bool sleeping, bool brainOnline, bool robot, bool lifeSuspended, bool dead, bool suitWorn,
        bool? helmetClosed)
    {
        Sleeping = sleeping;
        BrainOnline = brainOnline;
        Robot = robot;
        LifeSuspended = lifeSuspended;
        Dead = dead;
        SuitWorn = suitWorn;
        HelmetClosed = helmetClosed;
    }

    internal bool Sleeping { get; }

    internal bool BrainOnline { get; }

    internal bool Robot { get; }

    internal bool LifeSuspended { get; }

    internal bool Dead { get; }

    internal bool SuitWorn { get; }

    /// <summary>Null when no helmet is worn.</summary>
    internal bool? HelmetClosed { get; }
}

/// <summary>Hunger and thirst drain, the temperature thirst depends on, and the tick they are computed per.</summary>
internal sealed class VitalsRates
{
    internal VitalsRates(ThirstTemperatureView thirstTemperature, DrainView hunger, DrainView? thirst,
        float gameTickS)
    {
        ThirstTemperature = thirstTemperature;
        Hunger = hunger;
        Thirst = thirst;
        GameTickS = gameTickS;
    }

    internal ThirstTemperatureView ThirstTemperature { get; }

    internal DrainView Hunger { get; }

    internal DrainView? Thirst { get; }

    internal float GameTickS { get; }
}

internal sealed class ThirstTemperatureView
{
    internal ThirstTemperatureView(double? temperatureK, string source)
    {
        TemperatureK = temperatureK;
        Source = source;
    }

    public double? TemperatureK { get; }

    /// <summary>
    /// suit: inside the suit (helmet closed); room: a sealed room's air; world: the air at the player outside any
    /// room; none: no atmosphere found.
    /// </summary>
    public string Source { get; }
}

internal sealed class DifficultyView
{
    internal DifficultyView(string? id, float hungerRate, float hydrationRate, float offlineMetabolism)
    {
        Id = id;
        HungerRate = hungerRate;
        HydrationRate = hydrationRate;
        OfflineMetabolism = offlineMetabolism;
    }

    public string? Id { get; }

    public float HungerRate { get; }

    public float HydrationRate { get; }

    public float OfflineMetabolism { get; }
}

/// <summary>A store's drain per game second, now and when up and about, and how long the store lasts.</summary>
internal sealed class DrainView
{
    private const double SecondsPerHour = 3600.0;

    internal DrainView(float store, double awakePerSecond, double perSecond, object factors)
    {
        PerSecond = perSecond;
        PerHour = perSecond * SecondsPerHour;
        AwakePerSecond = awakePerSecond;
        AwakePerHour = awakePerSecond * SecondsPerHour;
        TimeLeftS = perSecond > 0 ? store / perSecond : null;
        TimeLeftAwakeS = awakePerSecond > 0 ? store / awakePerSecond : null;
        Factors = factors;
    }

    public double PerSecond { get; }

    public double PerHour { get; }

    public double AwakePerSecond { get; }

    public double AwakePerHour { get; }

    public double? TimeLeftS { get; }

    public double? TimeLeftAwakeS { get; }

    /// <summary>A HungerFactorsView or ThirstFactorsView.</summary>
    public object Factors { get; }
}

internal sealed class HungerFactorsView
{
    internal HungerFactorsView(float mood, float metabolism, float sleeping)
    {
        Mood = mood;
        Metabolism = metabolism;
        Sleeping = sleeping;
    }

    public float Mood { get; }

    public float Metabolism { get; }

    public float Sleeping { get; }
}

internal sealed class ThirstFactorsView
{
    internal ThirstFactorsView(float temperature, float metabolism, float sleeping)
    {
        Temperature = temperature;
        Metabolism = metabolism;
        Sleeping = sleeping;
    }

    public float Temperature { get; }

    public float Metabolism { get; }

    public float Sleeping { get; }
}

internal sealed class VitalsWarningsView
{
    internal VitalsWarningsView(float nutritionWarning, float nutritionCritical, float hydrationWarning,
        float hydrationCritical)
    {
        NutritionWarning = nutritionWarning;
        NutritionCritical = nutritionCritical;
        HydrationWarning = hydrationWarning;
        HydrationCritical = hydrationCritical;
    }

    public float NutritionWarning { get; }

    public float NutritionCritical { get; }

    public float HydrationWarning { get; }

    public float HydrationCritical { get; }
}
