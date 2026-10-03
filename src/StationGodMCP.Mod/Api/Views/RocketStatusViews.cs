#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Rockets;

namespace StationGodMCP.Api.Views;

/// <summary>rocket_status: every rocket asked for.</summary>
internal sealed class RocketStatusListView
{
    internal RocketStatusListView(List<RocketStatusView> rockets, List<string> explanations)
    {
        Rockets = rockets;
        Count = rockets.Count;
        Explanations = explanations;
    }

    public List<RocketStatusView> Rockets { get; }

    public int Count { get; }

    /// <summary>What the numbers mean, said once for every rocket in the reply.</summary>
    public List<string> Explanations { get; }
}

/// <summary>Where a rocket is and where it goes.</summary>
internal sealed class RocketWhereView
{
    internal RocketWhereView(string state, string mode, SpaceNodeView? current, SpaceNodeView? target, double progress,
        double? altitudeM, double velocityMs, double accelerationMs2, string flightRule, List<RouteHopView> route,
        List<string> uncharted)
    {
        State = state;
        Mode = mode;
        Current = current;
        Target = target;
        Progress = RocketRound.Of(progress, 4);
        AltitudeM = altitudeM.HasValue ? RocketRound.Of(altitudeM.Value, 1) : null;
        VelocityMs = RocketRound.Of(velocityMs, 2);
        AccelerationMs2 = RocketRound.Of(accelerationMs2, 3);
        FlightRule = flightRule;
        Route = route;
        Uncharted = uncharted.Count == 0 ? null : uncharted;
    }

    /// <summary>OnLaunchMount (pad), Launching, InSpace (parked or travelling), Landing.</summary>
    public string State { get; }

    /// <summary>RocketMode: the action at the node (Mine, Chart, Discover...).</summary>
    public string Mode { get; }

    public SpaceNodeView? Current { get; }

    public SpaceNodeView? Target { get; }

    public double Progress { get; }

    /// <summary>Rocket.GetAltitude: above the launch mount while launching or landing; null otherwise.</summary>
    public double? AltitudeM { get; }

    public double VelocityMs { get; }

    public double AccelerationMs2 { get; }

    public string FlightRule { get; }

    /// <summary>The hops left to the target, as the game's pathfinder will fly them.</summary>
    public List<RouteHopView> Route { get; }

    /// <summary>Uncharted nodes on the route (the pathfinder goes through them; DestinationCode refuses an uncharted target).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Uncharted { get; }
}

/// <summary>What the rocket weighs, as RocketNetwork.CombinedMass counts it.</summary>
internal sealed class RocketMassView
{
    internal RocketMassView(double totalKg, double structureKg, double gasKg, double cargoKg,
        List<MassPartView>? parts, double payloadKg)
    {
        TotalKg = RocketRound.Of(totalKg, 1);
        StructureKg = RocketRound.Of(structureKg, 1);
        GasKg = RocketRound.Of(gasKg, 1);
        CargoKg = RocketRound.Of(cargoKg, 0);
        PayloadKg = RocketRound.Of(payloadKg, 0);
        Parts = parts;
    }

    public double TotalKg { get; }

    /// <summary>RocketNetwork.DryMass: every IRocketMassContributor, cargo slots included.</summary>
    public double StructureKg { get; }

    /// <summary>RocketNetwork.GasMass: gas in the atmospheres the rocket lists (pipes, tanks, engines).</summary>
    public double GasKg { get; }

    /// <summary>The filled cargo slots' share of the structure mass (1 kg each).</summary>
    public double CargoKg { get; }

    /// <summary>The payloads attached to payload bays (200 kg each; they leave on Deploy).</summary>
    public double PayloadKg { get; }

    /// <summary>Each kind of part that weighs something, with its count (left out in compact mode).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<MassPartView>? Parts { get; }
}

internal sealed class MassPartView
{
    internal MassPartView(string name, int count, double kg)
    {
        Name = name;
        Count = count;
        Kg = RocketRound.Of(kg, 1);
    }

    public string Name { get; }

    public int Count { get; }

    /// <summary>All of them together.</summary>
    public double Kg { get; }
}

/// <summary>One engine fuel line: the pipe network the engines draw from and the tanks on it.</summary>
internal sealed class FuelLineView
{
    internal FuelLineView(ThingId networkId, int engines, double pipeLitres, double pipeMol, double totalMol,
        double temperatureK, Dictionary<string, double> mix, List<FuelTankView>? tanks, double liquidMol = 0.0,
        double liquidLitres = 0.0, double gasKpa = 0.0, List<string>? feeds = null)
    {
        LiquidMol = RocketRound.Of(liquidMol, 1);
        LiquidLitres = RocketRound.Of(liquidLitres, 1);
        PipeGasKpa = RocketRound.Of(gasKpa, 1);
        Feeds = feeds;
        NetworkId = networkId;
        Engines = engines;
        PipeLitres = RocketRound.Of(pipeLitres, 1);
        PipeMol = RocketRound.Of(pipeMol, 1);
        TotalMol = RocketRound.Of(totalMol, 1);
        TemperatureK = RocketRound.Of(temperatureK, 1);
        Mix = mix;
        Tanks = tanks;
    }

    public ThingId NetworkId { get; }

    public int Engines { get; }

    public double PipeLitres { get; }

    public double PipeMol { get; }

    public double TotalMol { get; }

    public double TemperatureK { get; }

    /// <summary>Liquid moles in the pipe and tanks together.</summary>
    public double LiquidMol { get; }

    /// <summary>The litres that liquid fills.</summary>
    public double LiquidLitres { get; }

    /// <summary>The pipe's gas pressure (Atmosphere.PressureGasses), what the pressure-fed engines read.</summary>
    public double PipeGasKpa { get; }

    /// <summary>Which engine inputs draw from it (e.g. "Pumped Liquid Engine input 2").</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Feeds { get; }

    /// <summary>Mole fraction of each gas and liquid in the pipe the engines draw from.</summary>
    public Dictionary<string, double> Mix { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<FuelTankView>? Tanks { get; }
}

internal sealed class FuelTankView
{
    internal FuelTankView(ThingId referenceId, string name, double litres, double mol, double temperatureK,
        double pressureKpa, bool countsForMass)
    {
        ReferenceId = referenceId;
        Name = name;
        Litres = RocketRound.Of(litres, 0);
        Mol = RocketRound.Of(mol, 1);
        TemperatureK = RocketRound.Of(temperatureK, 1);
        PressureKpa = RocketRound.Of(pressureKpa, 0);
        CountsForMass = countsForMass;
    }

    public ThingId ReferenceId { get; }

    public string Name { get; }

    public double Litres { get; }

    public double Mol { get; }

    public double TemperatureK { get; }

    public double PressureKpa { get; }

    /// <summary>Whether RocketNetwork.CalculateGasMass counts it (a canister in a tank storage slot is not counted).</summary>
    public bool CountsForMass { get; }
}

internal sealed class RocketEngineView
{
    internal RocketEngineView(ThingId referenceId, string name, string kind, bool on, bool powered, double throttle,
        double thrustN, double passedMol, double exhaustVelocity, double exhaustK, double maxThrustN, double usedW,
        ThingId? inputNetworkId, bool modelled, string? engine = null, string? feed = null, ThingId? input2NetworkId = null,
        ThingId? heatExchangeNetworkId = null, string? missingInput = null)
    {
        Engine = engine;
        Feed = feed;
        Input2NetworkId = input2NetworkId;
        HeatExchangeNetworkId = heatExchangeNetworkId;
        MissingInput = missingInput;
        ReferenceId = referenceId;
        Name = name;
        Kind = kind;
        On = on;
        Powered = powered;
        Throttle = RocketRound.Of(throttle, 2);
        ThrustN = RocketRound.Of(thrustN, 0);
        PassedMol = RocketRound.Of(passedMol, 2);
        ExhaustVelocityMs = RocketRound.Of(exhaustVelocity, 0);
        ExhaustK = RocketRound.Of(exhaustK, 0);
        PrefabMaxThrustN = RocketRound.Of(maxThrustN, 0);
        UsedW = usedW;
        InputNetworkId = inputNetworkId;
        Modelled = modelled;
    }

    public ThingId ReferenceId { get; }

    public string Name { get; }

    /// <summary>The engine's class (GovernedGasEngine is the Pumped Gas Engine).</summary>
    public string Kind { get; }

    /// <summary>The player-facing engine name.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Engine { get; }

    /// <summary>Its feed law, with the game's numbers.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Feed { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? Input2NetworkId { get; }

    /// <summary>A Pressure Fed Liquid Engine's input 2: a heat exchanger, not propellant.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? HeatExchangeNetworkId { get; }

    /// <summary>An input it needs and lacks: the game keeps it inoperable.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? MissingInput { get; }

    public bool On { get; }

    public bool Powered { get; }

    public double Throttle { get; }

    /// <summary>RocketEngineBase.Force from its last tick.</summary>
    public double ThrustN { get; }

    /// <summary>Moles it drew last tick.</summary>
    public double PassedMol { get; }

    public double ExhaustVelocityMs { get; }

    public double ExhaustK { get; }

    /// <summary>RocketEngineBase.MaxThrust: the prefab's thrust on its own test fuel (gas engines 2:1 methane/oxygen at 215 K; Pumped Liquid alcohol + LOX; Pressure Fed Liquid liquid methane 70 % + LOX 30 %).</summary>
    public double PrefabMaxThrustN { get; }

    /// <summary>Device.UsedPower: what it draws a tick while on.</summary>
    public double UsedW { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? InputNetworkId { get; }

    /// <summary>Whether rocket_forecast can fly it (any of the game's six engines with its inputs connected).</summary>
    public bool Modelled { get; }
}

/// <summary>The thrust figures the landing check and the engines give.</summary>
/// <summary>
/// The combustion rate the thrust probe burned the fuel at: rate, source (game, or terraforming_reloaded when that mod
/// sets it) and a note when the mod answered something unusable.
/// </summary>
internal sealed class CombustionView
{
    internal CombustionView(CombustionRate rate)
    {
        Rate = rate.Rate;
        Source = rate.Source;
        Note = rate.Note;
    }

    public double Rate { get; }

    public string Source { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Note { get; }
}

internal sealed class RocketThrustView
{
    internal RocketThrustView(double currentN, double maxRecordedN, double prefabMaxN, double maxExpectedN,
        double? achievableFullN, string? howRecorded, CombustionView? combustion = null)
    {
        Combustion = combustion;
        CurrentN = RocketRound.Of(currentN, 0);
        MaxRecordedN = RocketRound.Of(maxRecordedN, 0);
        PrefabMaxN = RocketRound.Of(prefabMaxN, 0);
        MaxExpectedN = RocketRound.Of(maxExpectedN, 0);
        AchievableFullN = achievableFullN.HasValue ? RocketRound.Of(achievableFullN.Value, 0) : null;
        RecordedExceedsAchievable = achievableFullN.HasValue && maxExpectedN > achievableFullN.Value * 1.001;
        HowRecorded = howRecorded;
    }

    /// <summary>Rocket.GetThrust: every engine's Force now.</summary>
    public double CurrentN { get; }

    /// <summary>Rocket.MaxRecordedThrust.</summary>
    public double MaxRecordedN { get; }

    public double PrefabMaxN { get; }

    /// <summary>Rocket.GetMaxExpectedThrust: what the landing check assumes.</summary>
    public double MaxExpectedN { get; }

    /// <summary>Full-throttle thrust the fuel in the line gives now, by the game's combustion on a copy.</summary>
    public double? AchievableFullN { get; }

    /// <summary>The check assumes more thrust than the fuel now gives: it passes landings that will crash.</summary>
    public bool RecordedExceedsAchievable { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? HowRecorded { get; }

    /// <summary>The combustion rate achievable_full_n was measured at.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public CombustionView? Combustion { get; }
}

/// <summary>The auto-land confidence the avionics shows, computed now.</summary>
internal sealed class LandingCheckView
{
    internal LandingCheckView(double confidence, string band, double? minRequiredThrustN, double expectedThrustN,
        double massKg, double altitudeM, double deltaV, double gravity, string source)
    {
        Confidence = RocketRound.Of(confidence, 2);
        Band = band;
        MinRequiredThrustN = minRequiredThrustN.HasValue ? RocketRound.Of(minRequiredThrustN.Value, 0) : null;
        ExpectedThrustN = RocketRound.Of(expectedThrustN, 0);
        MassKg = RocketRound.Of(massKg, 1);
        AltitudeM = altitudeM;
        DeltaV = deltaV;
        Gravity = gravity;
        Source = source;
    }

    public double Confidence { get; }

    public string Band { get; }

    public double? MinRequiredThrustN { get; }

    public double ExpectedThrustN { get; }

    public double MassKg { get; }

    public double AltitudeM { get; }

    public double DeltaV { get; }

    public double Gravity { get; }

    /// <summary>The game method that computed it.</summary>
    public string Source { get; }
}

internal sealed class CargoHoldView
{
    internal CargoHoldView(ThingId referenceId, string name, int filled, int slots, double kg)
    {
        ReferenceId = referenceId;
        Name = name;
        Filled = filled;
        Slots = slots;
        Kg = RocketRound.Of(kg, 0);
    }

    public ThingId ReferenceId { get; }

    public string Name { get; }

    /// <summary>Filled slots by the hold's own counter (CurrentIndex - 2).</summary>
    public int Filled { get; }

    public int Slots { get; }

    /// <summary>RocketChuteStorage.MassContribution: its own mass plus 1 kg per filled slot.</summary>
    public double Kg { get; }
}

internal sealed class RocketPowerView
{
    internal RocketPowerView(double chargeJ, double capacityJ, double loadW, double engineW,
        List<PowerDeviceView>? devices)
    {
        ChargeJ = RocketRound.Of(chargeJ, 0);
        CapacityJ = RocketRound.Of(capacityJ, 0);
        Percent = capacityJ > 0.0 ? RocketRound.Of(chargeJ / capacityJ * 100.0, 1) : 0.0;
        LoadW = RocketRound.Of(loadW, 1);
        SecondsLeft = loadW > 0.0 ? RocketRound.Of(chargeJ / loadW * 0.5, 0) : null;
        EngineW = engineW;
        Devices = devices;
    }

    public double ChargeJ { get; }

    public double CapacityJ { get; }

    public double Percent { get; }

    /// <summary>What the devices on the batteries' outputs draw now, per 0.5 s tick.</summary>
    public double LoadW { get; }

    /// <summary>At this load: charge / load ticks of 0.5 s.</summary>
    public double? SecondsLeft { get; }

    /// <summary>The engines' draw while on.</summary>
    public double EngineW { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PowerDeviceView>? Devices { get; }
}

internal sealed class PowerDeviceView
{
    internal PowerDeviceView(ThingId referenceId, string name, bool on, double nowW, double onW, bool staysOnAtArrival)
    {
        ReferenceId = referenceId;
        Name = name;
        On = on;
        NowW = RocketRound.Of(nowW, 1);
        OnW = RocketRound.Of(onW, 1);
        StaysOnAtArrival = staysOnAtArrival;
    }

    public ThingId ReferenceId { get; }

    public string Name { get; }

    public bool On { get; }

    public double NowW { get; }

    public double OnW { get; }

    /// <summary>Whether AutoShutOff leaves it on at a destination (avionics, batteries, circuit housings, or off the avionics' data network).</summary>
    public bool StaysOnAtArrival { get; }
}

internal sealed class BurnTimeView
{
    internal BurnTimeView(double gameEstimateS, double? fullThrottleS, double? currentThrottleS)
    {
        GameEstimateS = gameEstimateS;
        FullThrottleS = fullThrottleS.HasValue ? RocketRound.Of(fullThrottleS.Value, 0) : null;
        CurrentThrottleS = currentThrottleS.HasValue ? RocketRound.Of(currentThrottleS.Value, 0) : null;
    }

    /// <summary>Rocket.EstimatedRemainingBurnTimeSeconds: 0 unless travelling and burning.</summary>
    public double GameEstimateS { get; }

    public double? FullThrottleS { get; }

    public double? CurrentThrottleS { get; }
}

internal sealed class RocketStatusView
{
    internal RocketStatusView(ThingId referenceId, ThingId networkId, string name, bool automatedLanding,
        bool autoShutOff, string reEntryProfile, double reEntryAltitudeM, RocketWhereView where, RocketMassView mass,
        List<FuelLineView> fuel, List<RocketEngineView> engines, RocketThrustView thrust, LandingCheckView landingCheck,
        List<CargoHoldView> cargo, RocketPowerView power, BurnTimeView burnTime, List<SelfCheckView>? checks,
        List<string> notes, bool manned = false, LandingAtPadView? landingAtPad = null, List<string>? collects = null,
        List<PartPoseView>? parts = null)
    {
        Manned = manned;
        LandingAtPad = landingAtPad;
        Collects = collects;
        Parts = parts;
        ReferenceId = referenceId;
        NetworkId = networkId;
        Name = name;
        AutomatedLanding = automatedLanding;
        AutoShutOff = autoShutOff;
        ReEntryProfile = reEntryProfile;
        ReEntryAltitudeM = reEntryAltitudeM;
        Where = where;
        Mass = mass;
        Fuel = fuel;
        Engines = engines;
        Thrust = thrust;
        LandingCheck = landingCheck;
        Cargo = cargo;
        Power = power;
        BurnTime = burnTime;
        Checks = checks;
        Notes = notes;
    }

    public ThingId ReferenceId { get; }

    public ThingId NetworkId { get; }

    public string Name { get; }

    public bool AutomatedLanding { get; }

    public bool AutoShutOff { get; }

    /// <summary>Someone sits in a Crew Module Chair: the rocket may only target launch mounts (Rocket.cs:1871-1885).</summary>
    public bool Manned { get; }

    public string ReEntryProfile { get; }

    public double ReEntryAltitudeM { get; }

    public RocketWhereView Where { get; }

    public RocketMassView Mass { get; }

    public List<FuelLineView> Fuel { get; }

    public List<RocketEngineView> Engines { get; }

    public RocketThrustView Thrust { get; }

    public LandingCheckView LandingCheck { get; }

    /// <summary>The check the game really makes at re-entry toward its pad (the target, else its home pad): that pad's hop and gravity.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public LandingAtPadView? LandingAtPad { get; }

    /// <summary>What its gear can collect or do: ore, ice, gas, chart/discover/survey, surface_scan (rocket_mining_options has the detail).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Collects { get; }

    public List<CargoHoldView> Cargo { get; }

    public RocketPowerView Power { get; }

    public BurnTimeView BurnTime { get; }

    /// <summary>The port checked against the game's own numbers now.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<SelfCheckView>? Checks { get; }

    public List<string> Notes { get; }

    /// <summary>Every part with its pose relative to the engine mount (parts: true), enough to build the rocket again.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PartPoseView>? Parts { get; }
}

/// <summary>rocket_forecast's reply.</summary>
internal sealed class RocketForecastView
{
    internal RocketForecastView(ThingId rocketId, string rocketName, string startState, SpaceNodeView? destination,
        List<RouteHopView> route, List<string> uncharted, List<string> assumptions, List<ForecastLegView> legs,
        bool succeeded, string verdict, double fuelNowMol, double? leastFuelMol, LandingLimitsView? limits,
        List<ProfileLandingView>? profiles, ColumnView? column, CombustionView? combustion = null,
        ForecastMiningView? mining = null)
    {
        Combustion = combustion;
        Mining = mining;
        RocketId = rocketId;
        RocketName = rocketName;
        StartState = startState;
        Destination = destination;
        Route = route;
        Uncharted = uncharted.Count == 0 ? null : uncharted;
        Assumptions = assumptions;
        Legs = legs;
        Succeeded = succeeded;
        Verdict = verdict;
        FuelNowMol = RocketRound.Of(fuelNowMol, 0);
        LeastFuelMol = leastFuelMol.HasValue ? RocketRound.Of(leastFuelMol.Value, 0) : null;
        LandingLimits = limits;
        Profiles = profiles;
        Column = column;
    }

    public ThingId RocketId { get; }

    public string RocketName { get; }

    public string StartState { get; }

    public SpaceNodeView? Destination { get; }

    public List<RouteHopView> Route { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Uncharted { get; }

    public List<string> Assumptions { get; }

    public List<ForecastLegView> Legs { get; }

    public bool Succeeded { get; }

    public string Verdict { get; }

    /// <summary>The fuel the forecast started with (after what-ifs).</summary>
    public double FuelNowMol { get; }

    /// <summary>The least starting fuel the whole plan succeeds with (null: none does, or not asked).</summary>
    public double? LeastFuelMol { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public LandingLimitsView? LandingLimits { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ProfileLandingView>? Profiles { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ColumnView? Column { get; }

    /// <summary>The combustion rate the engines were modelled at (the game's, or Terraforming Reloaded's).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public CombustionView? Combustion { get; }

    /// <summary>The destination's deposit: whether this loadout collects it, and what the stop yields.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ForecastMiningView? Mining { get; }
}

/// <summary>The auto-land check as the game makes it at re-entry toward one pad (Rocket.EvaluateLaunchOrLandState, Rocket.cs:2174-2188).</summary>
internal sealed class LandingAtPadView
{
    internal LandingAtPadView(string pad, bool orbital, double confidence, string band, double? minRequiredThrustN,
        double expectedThrustN, double massKg, double altitudeM, double deltaV, double gravity)
    {
        Pad = pad;
        Orbital = orbital;
        Confidence = RocketRound.Of(confidence, 2);
        Band = band;
        MinRequiredThrustN = minRequiredThrustN.HasValue ? RocketRound.Of(minRequiredThrustN.Value, 0) : null;
        ExpectedThrustN = RocketRound.Of(expectedThrustN, 0);
        MassKg = RocketRound.Of(massKg, 1);
        AltitudeM = altitudeM;
        DeltaV = RocketRound.Of(deltaV, 1);
        Gravity = gravity;
    }

    public string Pad { get; }

    /// <summary>An Orbital Launch Mount: gravity 1 m/s2 for the check and the descent.</summary>
    public bool Orbital { get; }

    public double Confidence { get; }

    public string Band { get; }

    public double? MinRequiredThrustN { get; }

    public double ExpectedThrustN { get; }

    public double MassKg { get; }

    public double AltitudeM { get; }

    /// <summary>The hop down to the pad (twice its map distance): the speed the landing starts at.</summary>
    public double DeltaV { get; }

    public double Gravity { get; }
}

/// <summary>One part of a rocket and where it sits relative to the engine mount (its RocketData anchor).</summary>
internal sealed class PartPoseView
{
    internal PartPoseView(ThingId referenceId, string prefab, string? name, string role, double x, double y, double z,
        OrientationView rotation, List<string>? contents)
    {
        ReferenceId = referenceId;
        Prefab = prefab;
        Name = name;
        Role = role;
        Offset = new Dictionary<string, double> { ["x"] = RocketRound.Of(x, 3), ["y"] = RocketRound.Of(y, 3), ["z"] = RocketRound.Of(z, 3) };
        Rotation = rotation;
        Contents = contents;
    }

    public ThingId ReferenceId { get; }

    public string Prefab { get; }

    /// <summary>Its label, when it has one.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Name { get; }

    /// <summary>hull (fuselage pieces, engine mount, fairing) or internal (devices, tanks, pipes, cables, chutes).</summary>
    public string Role { get; }

    /// <summary>Metres from the engine mount, in the mount's own frame (x right, y up, z forward).</summary>
    public Dictionary<string, double> Offset { get; }

    /// <summary>Its rotation relative to the engine mount, as place_structure takes it.</summary>
    public OrientationView Rotation { get; }

    /// <summary>Prefabs in its slots (drill head, scanner head, payload, canisters, batteries).</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Contents { get; }
}
