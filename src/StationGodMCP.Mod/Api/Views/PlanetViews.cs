#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Api.Views;

/// <summary>planet, before a world is loaded: only whether Terraforming Reloaded is there.</summary>
internal sealed class PlanetNotLoadedView
{
    internal PlanetNotLoadedView(TerraformingModView? terraformingMod)
    {
        TerraformingMod = terraformingMod;
    }

    public bool Loaded => false;

    public TerraformingModView? TerraformingMod { get; }
}

/// <summary>
/// planet: the planet's own atmosphere, its reservoirs and its temperature. Built from readings grouped by what they
/// describe; the wire keeps the old flat order.
/// </summary>
internal sealed class PlanetView
{
    private readonly PlanetClock _clock;
    private readonly PlanetTank _tank;
    private readonly PlanetWeather _weather;
    private readonly PlanetAir _air;

    internal PlanetView(PlanetClock clock, PlanetTank tank, PlanetWeather weather, PlanetAir air,
        List<PlanetGasView> gases, ReservoirsView reservoirs, StartingAirView startingAir,
        TerraformingModView? terraformingMod)
    {
        _clock = clock;
        _tank = tank;
        _weather = weather;
        _air = air;
        Gases = gases;
        Reservoirs = reservoirs;
        StartingAir = startingAir;
        TerraformingMod = terraformingMod;
    }

    public bool Loaded => true;

    public double WorldTimeS => _clock.WorldSeconds;

    public double DayLengthS => _clock.DayLengthSeconds;

    public double VolumeL => _tank.VolumeL;

    public double GasVolumeL => _tank.GasVolumeL;

    public double Cells => _tank.Cells;

    /// <summary>The planet's size as a fraction of the size it shipped with.</summary>
    public double? SizeRatio => _tank.SizeShare;

    public double GasMol => _tank.GasMoles;

    public double LiquidMol => _tank.LiquidMoles;

    public double LiquidL => _tank.LiquidLitres;

    public SeaView Sea => _tank.Sea;

    public double PressureKpa => _weather.PressureKpa;

    public double GamePressureKpa => _weather.GamePressureKpa;

    public double TemperatureK => _weather.TemperatureK;

    public TemperaturePartsView TemperatureParts => _weather.TemperatureParts;

    public float SunAngleDeg => _weather.SunAngleDeg;

    public float SolarEnergyPercent => _weather.SolarEnergyPercent;

    public TemperatureRangeView Range => _weather.Range;

    public StormView? Storm => _weather.Storm;

    public double OxygenKpa => _air.OxygenKpa;

    public double ToxinsKpa => _air.ToxinsKpa;

    public double FuelMol => _air.FuelMoles;

    public double OxidiserMol => _air.OxidiserMoles;

    /// <summary>Carbon dioxide's fraction of the gas moles.</summary>
    public double CarbonDioxideRatio => _air.CarbonDioxideShare;

    public List<PlanetGasView> Gases { get; }

    public ReservoirsView Reservoirs { get; }

    /// <summary>The air this world starts with, at the planet's present size.</summary>
    public StartingAirView StartingAir { get; }

    public TerraformingModView? TerraformingMod { get; }
}

/// <summary>
/// The air the world ships with (GlobalAtmosphereData.GlobalGasMixData, moles per outdoor cell), scaled to the
/// planet's present cells: what the planet held when it was created or last reset.
/// </summary>
internal sealed class StartingAirView
{
    internal StartingAirView(double perCellMol, double mol, List<ReservoirGasView> gases)
    {
        PerCellMol = perCellMol;
        Mol = mol;
        Gases = gases;
    }

    public double PerCellMol { get; }

    public double Mol { get; }

    public List<ReservoirGasView> Gases { get; }
}

/// <summary>World time, for PlanetView.</summary>
internal sealed class PlanetClock
{
    internal PlanetClock(double worldSeconds, double dayLengthSeconds)
    {
        WorldSeconds = worldSeconds;
        DayLengthSeconds = dayLengthSeconds;
    }

    internal double WorldSeconds { get; }

    internal double DayLengthSeconds { get; }
}

/// <summary>The size and contents of the planet's gas tank, for PlanetView.</summary>
internal sealed class PlanetTank
{
    internal PlanetTank(double volumeL, double gasVolumeL, double cells, double? sizeShare, double gasMoles,
        double liquidMoles, double liquidLitres, SeaView sea)
    {
        VolumeL = volumeL;
        GasVolumeL = gasVolumeL;
        Cells = cells;
        SizeShare = sizeShare;
        GasMoles = gasMoles;
        LiquidMoles = liquidMoles;
        LiquidLitres = liquidLitres;
        Sea = sea;
    }

    internal double VolumeL { get; }

    internal double GasVolumeL { get; }

    internal double Cells { get; }

    internal double? SizeShare { get; }

    internal double GasMoles { get; }

    internal double LiquidMoles { get; }

    internal double LiquidLitres { get; }

    internal SeaView Sea { get; }
}

/// <summary>Pressure, temperature, sun and storm, for PlanetView.</summary>
internal sealed class PlanetWeather
{
    internal PlanetWeather(double pressureKpa, double gamePressureKpa, double temperatureK,
        TemperaturePartsView temperatureParts, float sunAngleDeg, float solarEnergyPercent, TemperatureRangeView range,
        StormView? storm)
    {
        PressureKpa = pressureKpa;
        GamePressureKpa = gamePressureKpa;
        TemperatureK = temperatureK;
        TemperatureParts = temperatureParts;
        SunAngleDeg = sunAngleDeg;
        SolarEnergyPercent = solarEnergyPercent;
        Range = range;
        Storm = storm;
    }

    internal double PressureKpa { get; }

    internal double GamePressureKpa { get; }

    internal double TemperatureK { get; }

    internal TemperaturePartsView TemperatureParts { get; }

    internal float SunAngleDeg { get; }

    internal float SolarEnergyPercent { get; }

    internal TemperatureRangeView Range { get; }

    internal StormView? Storm { get; }
}

/// <summary>What the air means for a player: oxygen, toxins, fuel and oxidiser, for PlanetView.</summary>
internal sealed class PlanetAir
{
    internal PlanetAir(double oxygenKpa, double toxinsKpa, double fuelMoles, double oxidiserMoles,
        double carbonDioxideShare)
    {
        OxygenKpa = oxygenKpa;
        ToxinsKpa = toxinsKpa;
        FuelMoles = fuelMoles;
        OxidiserMoles = oxidiserMoles;
        CarbonDioxideShare = carbonDioxideShare;
    }

    internal double OxygenKpa { get; }

    internal double ToxinsKpa { get; }

    internal double FuelMoles { get; }

    internal double OxidiserMoles { get; }

    internal double CarbonDioxideShare { get; }
}

internal sealed class SeaView
{
    internal SeaView(double litres, double thresholdLitres, bool shown)
    {
        LiquidL = litres;
        ThresholdL = thresholdLitres;
        Shown = shown;
    }

    public double LiquidL { get; }

    /// <summary>The liquid volume at which the game draws the sea (GlobalAtmosphereLiquid.RenderThreshold).</summary>
    public double ThresholdL { get; }

    public bool Shown { get; }
}

/// <summary>The parts the game adds up into the planet's temperature, in kelvin.</summary>
internal sealed class TemperaturePartsView
{
    internal TemperaturePartsView(double sunAngle, double sunDistance, double greenhouse, double density,
        double weather, double latent, double external)
    {
        SunAngleK = sunAngle;
        SunDistanceK = sunDistance;
        GreenhouseK = greenhouse;
        DensityK = density;
        WeatherK = weather;
        LatentK = latent;
        ExternalK = external;
    }

    public double SunAngleK { get; }

    public double SunDistanceK { get; }

    public double GreenhouseK { get; }

    public double DensityK { get; }

    public double WeatherK { get; }

    public double LatentK { get; }

    public double ExternalK { get; }
}

internal sealed class TemperatureRangeView
{
    internal TemperatureRangeView(TemperatureSweepView today, double orbitMinK, double orbitMaxK)
    {
        TodayMinK = today.MinK;
        TodayMinSunAngleDeg = today.MinAngle;
        TodayMaxK = today.MaxK;
        TodayMaxSunAngleDeg = today.MaxAngle;
        OrbitMinK = orbitMinK;
        OrbitMaxK = orbitMaxK;
    }

    public double TodayMinK { get; }

    public double TodayMinSunAngleDeg { get; }

    public double TodayMaxK { get; }

    public double TodayMaxSunAngleDeg { get; }

    public double OrbitMinK { get; }

    public double OrbitMaxK { get; }
}

/// <summary>The coldest and hottest temperature over a sweep of sun angles, and where each was.</summary>
internal sealed class TemperatureSweepView
{
    internal TemperatureSweepView(double minK, double minAngle, double maxK, double maxAngle)
    {
        MinK = minK;
        MinAngle = minAngle;
        MaxK = maxK;
        MaxAngle = maxAngle;
    }

    internal double MinK { get; }

    internal double MinAngle { get; }

    internal double MaxK { get; }

    internal double MaxAngle { get; }
}

internal sealed class StormView
{
    internal StormView(string? id)
    {
        Id = id;
    }

    public string? Id { get; }
}

internal sealed class PlanetGasView
{
    internal PlanetGasView(PlanetGasAmount amount, PlanetGasPhases phases)
    {
        Gas = amount.Gas;
        DisplayName = amount.DisplayName;
        State = amount.State;
        AmountMol = amount.Moles;
        PerCellMol = amount.PerCell;
        PartialKpa = amount.PartialKpa;
        FreezesBelowK = phases.FreezesBelowK;
        MinLiquidPressureKpa = phases.MinLiquidPressureKpa;
        CondensesBelowTodayColdestK = phases.CondensesBelowKTodayColdest;
        CondensesBelowOrbitColdestK = phases.CondensesBelowKOrbitColdest;
    }

    public string Gas { get; }

    public string? DisplayName { get; }

    public string State { get; }

    public double AmountMol { get; }

    public double PerCellMol { get; }

    public double? PartialKpa { get; }

    public double? FreezesBelowK { get; }

    public double? MinLiquidPressureKpa { get; }

    public double? CondensesBelowTodayColdestK { get; }

    public double? CondensesBelowOrbitColdestK { get; }
}

/// <summary>How much of one gas the planet holds, for PlanetGasView.</summary>
internal sealed class PlanetGasAmount
{
    internal PlanetGasAmount(string gas, string? displayName, string state, double moles, double perCell,
        double? partialKpa)
    {
        Gas = gas;
        DisplayName = displayName;
        State = state;
        Moles = moles;
        PerCell = perCell;
        PartialKpa = partialKpa;
    }

    internal string Gas { get; }

    internal string? DisplayName { get; }

    internal string State { get; }

    internal double Moles { get; }

    internal double PerCell { get; }

    internal double? PartialKpa { get; }
}

/// <summary>Where one gas changes state on the planet, for PlanetGasView.</summary>
internal sealed class PlanetGasPhases
{
    internal PlanetGasPhases(double? freezesBelowK, double? minLiquidPressureKpa, double? condensesBelowKTodayColdest,
        double? condensesBelowKOrbitColdest)
    {
        FreezesBelowK = freezesBelowK;
        MinLiquidPressureKpa = minLiquidPressureKpa;
        CondensesBelowKTodayColdest = condensesBelowKTodayColdest;
        CondensesBelowKOrbitColdest = condensesBelowKOrbitColdest;
    }

    internal double? FreezesBelowK { get; }

    internal double? MinLiquidPressureKpa { get; }

    internal double? CondensesBelowKTodayColdest { get; }

    internal double? CondensesBelowKOrbitColdest { get; }
}

internal sealed class ReservoirsView
{
    internal ReservoirsView(ReservoirView? liquidClouds, ReservoirView? iceClouds, ReservoirView? iceCaps)
    {
        LiquidClouds = liquidClouds;
        IceClouds = iceClouds;
        IceCaps = iceCaps;
    }

    public ReservoirView? LiquidClouds { get; }

    public ReservoirView? IceClouds { get; }

    public ReservoirView? IceCaps { get; }
}

internal sealed class ReservoirView
{
    internal ReservoirView(double volumeL, double liquidLitres, double moles, List<ReservoirGasView> contents)
    {
        VolumeL = volumeL;
        LiquidL = liquidLitres;
        AmountMol = moles;
        Contents = contents;
    }

    public double VolumeL { get; }

    public double LiquidL { get; }

    public double AmountMol { get; }

    public List<ReservoirGasView> Contents { get; }
}

internal sealed class ReservoirGasView
{
    internal ReservoirGasView(string gas, double moles)
    {
        Gas = gas;
        AmountMol = moles;
    }

    public string Gas { get; }

    public double AmountMol { get; }
}

internal sealed class TerraformingModView
{
    internal const string LiveState = "live";

    internal TerraformingModView(string? version, string? state, double? temperatureAdjustmentK)
    {
        Version = version;
        State = state;
        TemperatureAdjustmentK = temperatureAdjustmentK;
    }

    public string Name => "Terraforming Reloaded";

    public string? Version { get; }

    public string? State { get; }

    public bool Live => State == LiveState;

    public double? TemperatureAdjustmentK { get; }
}
