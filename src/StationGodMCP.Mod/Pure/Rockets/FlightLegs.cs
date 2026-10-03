#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.Rockets;

/// <summary>Where a landing begins: from a hop out of space (re-entry), or part way down already.</summary>
internal abstract class LandingStart
{
    private LandingStart()
    {
    }

    /// <summary>
    /// The re-entry hop from space: the confidence check first (Rocket.EvaluateLaunchOrLandState, Rocket.cs:2174-2188),
    /// then the start Rocket.OnRocketStateUpdated sets (Rocket.cs:1658-1688): Velocity = -distance, altitude = the
    /// re-entry profile's (cut down for an orbital pad), and InitAutomatedLanding.
    /// </summary>
    internal sealed class ReEntry : LandingStart
    {
        internal ReEntry(float profileAltitude, bool orbital, float distanceToOrbit, float worldGravity)
        {
            ProfileAltitude = profileAltitude;
            Orbital = orbital;
            DistanceToOrbit = distanceToOrbit;
            WorldGravity = worldGravity;
        }

        /// <summary>Rocket.ReEntryProfiles[profile] (Rocket.cs:77-99): Low 25 km, Medium 40 km, High 70 km, Max 120 km.</summary>
        internal float ProfileAltitude { get; }

        internal bool Orbital { get; }

        /// <summary>SpaceMap.Current.DistanceToOrbit, for the orbital pad's altitude search.</summary>
        internal float DistanceToOrbit { get; }

        internal float WorldGravity { get; }
    }

    /// <summary>A landing already under way (or a launch the apex check turned round), from the rocket's live state.</summary>
    internal sealed class Midway : LandingStart
    {
        internal Midway(float altitude, float velocity, float acceleration, float visualAltitude, bool initialise)
        {
            Initialise = initialise;
            Altitude = altitude;
            Velocity = velocity;
            Acceleration = acceleration;
            VisualAltitude = visualAltitude;
        }

        internal float Altitude { get; }

        internal float Velocity { get; }

        internal float Acceleration { get; }

        /// <summary>The rocket parent transform's height above the mount (it lags the target position).</summary>
        internal float VisualAltitude { get; }

        /// <summary>Whether the game starts its autopilot here (InitAutomatedLanding after a launch abort); false for a landing under way.</summary>
        internal bool Initialise { get; }
    }
}

/// <summary>One leg of a flight plan. Each flies itself on the simulator.</summary>
internal abstract class FlightLeg
{
    protected FlightLeg(string from, string to, float distance)
    {
        From = from;
        To = to;
        Distance = distance;
    }

    internal string From { get; }

    internal string To { get; }

    /// <summary>NodeTransit.GetDistance: twice the connection's distance (NodeTransit.cs:15-18). Also the Δv a hop costs.</summary>
    internal float Distance { get; }

    internal abstract string Kind { get; }

    internal abstract LegResult Fly(FlightSimulator simulator, RocketCraft craft);
}

/// <summary>Pad to orbit (or the rest of a launch under way): gravity until Progress reaches 1.</summary>
internal sealed class LaunchLeg : FlightLeg
{
    internal LaunchLeg(string from, string to, float distance, float gravity, LaunchStart start)
        : base(from, to, distance)
    {
        Gravity = gravity;
        Start = start;
    }

    /// <summary>Rocket.GetGravity while launching: the pad's world gravity clamped, -1 from an orbital pad.</summary>
    internal float Gravity { get; }

    internal LaunchStart Start { get; }

    internal override string Kind => "launch";

    internal override LegResult Fly(FlightSimulator simulator, RocketCraft craft) => simulator.Launch(this, craft);
}

/// <summary>A launch's starting point: on the mount, or climbing already.</summary>
internal readonly struct LaunchStart
{
    internal LaunchStart(bool onPad, float altitude, float velocity, float progress)
    {
        OnPad = onPad;
        Altitude = altitude;
        Velocity = velocity;
        Progress = progress;
    }

    internal static LaunchStart Pad => new LaunchStart(true, 0f, 0f, 0f);

    internal bool OnPad { get; }

    internal float Altitude { get; }

    internal float Velocity { get; }

    internal float Progress { get; }
}

/// <summary>A hop between space nodes: no gravity; Progress grows by the engine's Δv over the distance.</summary>
internal sealed class SpaceHop : FlightLeg
{
    internal SpaceHop(string from, string to, float distance, float startProgress) : base(from, to, distance)
    {
        StartProgress = startProgress;
    }

    internal float StartProgress { get; }

    internal override string Kind => "hop";

    internal override LegResult Fly(FlightSimulator simulator, RocketCraft craft) => simulator.Hop(this, craft);
}

/// <summary>The hop down to a launch pad and the autopilot's landing.</summary>
internal sealed class LandingLeg : FlightLeg
{
    internal LandingLeg(string from, string to, float distance, float gravity, LandingStart start)
        : base(from, to, distance)
    {
        Gravity = gravity;
        Start = start;
    }

    /// <summary>Rocket.GetGravity while landing: -1 at an orbital pad, else the world's clamped (Rocket.cs:3089).</summary>
    internal float Gravity { get; }

    internal LandingStart Start { get; }

    internal override string Kind => "landing";

    internal override LegResult Fly(FlightSimulator simulator, RocketCraft craft) => simulator.Land(this, craft);
}

/// <summary>
/// A stop at a node: the time spent there (mining, scanning, deploying, transferring) with its power draw, the cargo
/// slots filled by the end, and what leaves or arrives: a deployed payload's mass, fuel and charge another rocket gives
/// or takes through the Transfer action. The engine burns nothing parked once AutoShutOff (or the player) has switched
/// it off; a forecast assumes it is switched on again for the next leg.
/// </summary>
internal sealed class ParkLeg : FlightLeg
{
    internal ParkLeg(string at, float seconds, double loadW, int addCargoSlots, double addCargoKg)
        : this(at, seconds, loadW, addCargoSlots, addCargoKg, new List<LineTransfer>(), 0.0)
    {
    }

    internal ParkLeg(string at, float seconds, double loadW, int addCargoSlots, double addCargoKg,
        List<LineTransfer> fuel, double batteryJ)
        : base(at, at, 0f)
    {
        Seconds = seconds;
        LoadW = loadW;
        AddCargoSlots = addCargoSlots;
        AddCargoKg = addCargoKg;
        Fuel = fuel;
        BatteryJ = batteryJ;
    }

    internal float Seconds { get; }

    /// <summary>The power draw while parked, per tick.</summary>
    internal double LoadW { get; }

    /// <summary>Cargo slots filled during the stop: 1 kg each (RocketChuteStorage.MassContribution, RocketChuteStorage.cs:30).</summary>
    internal int AddCargoSlots { get; }

    /// <summary>Mass added (positive) or gone (negative, a deployed payload) by the end of the stop.</summary>
    internal double AddCargoKg { get; }

    /// <summary>Fuel received (positive) or given (negative) on each fuel line by the end of the stop.</summary>
    internal List<LineTransfer> Fuel { get; }

    /// <summary>Battery charge received (positive) or given (negative) by the end of the stop.</summary>
    internal double BatteryJ { get; }

    internal override string Kind => "park";

    internal override LegResult Fly(FlightSimulator simulator, RocketCraft craft) => simulator.Park(this, craft);
}

/// <summary>Moles a fuel line receives (positive) or gives (negative).</summary>
internal readonly struct LineTransfer
{
    internal LineTransfer(int line, double moles)
    {
        Line = line;
        Moles = moles;
    }

    internal int Line { get; }

    internal double Moles { get; }
}
