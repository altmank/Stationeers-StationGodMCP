#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Serialization;
using Newtonsoft.Json.Linq;
using Objects.Rockets;
using StationGodMCP.Pure.Rockets;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Flight;

/// <summary>rocket_forecast's what-ifs, each optional: the rocket is read as it stands and these replace parts of it.</summary>
internal sealed class RocketWhatIf
{
    private RocketWhatIf()
    {
    }

    internal double? FuelMol { get; private set; }

    internal double? FuelTemperatureK { get; private set; }

    /// <summary>Fractions by GasTypes.All index; null keeps the fuel's own mix.</summary>
    internal double[]? FuelMix { get; private set; }

    internal float ThrustScale { get; private set; } = 1f;

    internal float? Throttle { get; private set; }

    /// <summary>Filled cargo slots in all (replaces the holds' count).</summary>
    internal int? CargoSlots { get; private set; }

    internal double AddCargoKg { get; private set; }

    internal double? BatteryJ { get; private set; }

    internal double? BatteryPercent { get; private set; }

    internal double ExtraLoadW { get; private set; }

    internal static RocketWhatIf From(Args args)
    {
        RocketWhatIf what = new RocketWhatIf
        {
            FuelMol = NonNegative(args, "fuel_mol"),
            FuelTemperatureK = Positive(args, "fuel_temperature_k"),
            Throttle = (float?)Ranged(args, "throttle", 0.0, 100.0),
            CargoSlots = args.OptionalInt("cargo_slots", 0, 100000),
            AddCargoKg = NonNegative(args, "add_cargo_kg") ?? 0.0,
            BatteryJ = NonNegative(args, "battery_j"),
            BatteryPercent = Ranged(args, "battery_percent", 0.0, 100.0),
            ExtraLoadW = NonNegative(args, "extra_load_w") ?? 0.0,
            ThrustScale = (float)(Ranged(args, "thrust_scale", 0.0, 10.0) ?? 1.0)
        };
        JObject? mix = args.OptionalObject("fuel_mix");
        if (mix != null)
        {
            double[] fractions = new double[GasTypes.All.Length];
            foreach (JProperty property in mix.Properties())
            {
                int index = IndexOfGas(property.Name);
                if (index < 0 || (property.Value.Type != JTokenType.Float && property.Value.Type != JTokenType.Integer) ||
                    property.Value.Value<double>() < 0.0)
                {
                    throw ApiErrors.InvalidArgument(
                        $"fuel_mix.{property.Name}: give a gas name (e.g. Methane, Oxygen) and a fraction of 0 or more.");
                }

                fractions[index] = property.Value.Value<double>();
            }

            what.FuelMix = fractions;
        }

        return what;
    }

    private static int IndexOfGas(string name)
    {
        for (int index = 0; index < GasTypes.All.Length; index++)
        {
            if (string.Equals(GasTypes.All[index].ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static double? NonNegative(Args args, string name) => Ranged(args, name, 0.0, double.MaxValue);

    private static double? Positive(Args args, string name)
    {
        double? value = args.OptionalDouble(name);
        return value.HasValue && !(value.Value > 0.0)
            ? throw ApiErrors.InvalidArgument($"Argument '{name}' must be above 0.")
            : value;
    }

    private static double? Ranged(Args args, string name, double minimum, double maximum)
    {
        double? value = args.OptionalDouble(name);
        return value.HasValue && (value.Value < minimum || value.Value > maximum || double.IsNaN(value.Value))
            ? throw ApiErrors.InvalidArgument($"Argument '{name}' must be from {minimum} to {maximum}.")
            : value;
    }

    /// <summary>What was changed, in words, for the reply's assumptions.</summary>
    internal void Describe(List<string> into)
    {
        if (FuelMol.HasValue)
        {
            into.Add($"what-if: fuel set to {FuelMol.Value:0} mol in the same proportions across the tanks and pipe");
        }

        if (FuelTemperatureK.HasValue)
        {
            into.Add($"what-if: fuel at {FuelTemperatureK.Value:0} K (thrust per mole re-measured by the game's combustion)");
        }

        if (FuelMix != null)
        {
            into.Add("what-if: fuel mix replaced (thrust per mole re-measured by the game's combustion)");
        }

        if (Math.Abs(ThrustScale - 1f) > 1e-6f)
        {
            into.Add($"what-if: the engines give {ThrustScale * 100f:0.#} % of their thrust; the recorded peak the landing check uses is unchanged");
        }

        if (Throttle.HasValue)
        {
            into.Add($"what-if: throttle {Throttle.Value:0.#} for launch and hops");
        }

        if (CargoSlots.HasValue || AddCargoKg > 0.0)
        {
            into.Add($"what-if: cargo {(CargoSlots.HasValue ? CargoSlots.Value + " filled slots" : "as loaded")} plus {AddCargoKg:0} kg");
        }

        if (BatteryJ.HasValue || BatteryPercent.HasValue)
        {
            into.Add("what-if: battery charge replaced");
        }

        if (ExtraLoadW > 0.0)
        {
            into.Add($"what-if: {ExtraLoadW:0} W more load");
        }
    }
}

/// <summary>Builds the simulator's rocket from the live one: read only.</summary>
internal static class RocketModel
{
    /// <summary>Rocket.GetGravity's clamp of the world's gravity (Rocket.cs:3097); -1 for an orbital pad.</summary>
    internal static float GravityAt(ISpaceMapNodeOwner? owner) =>
        FlightMath.ClampGravity(owner != null && owner.IsOrbital ? -1f : WorldSetting.Current.Gravity);

    /// <summary>The simulator's clocks: the game's fixed physics step and its 0.5 s tick (GameManager.GameTickSpeedSeconds).</summary>
    internal static FlightSimulator Simulator() =>
        new FlightSimulator(Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f,
            Assets.Scripts.GameManager.GameTickSpeedSeconds);

    internal static RocketCraft Craft(RocketParts parts, RocketWhatIf what)
    {
        Rocket rocket = parts.Rocket;
        List<FuelLine> lines = new List<FuelLine>(parts.Lines.Count);
        double countedKg = 0.0;
        for (int index = 0; index < parts.Lines.Count; index++)
        {
            FuelLine line = LineOf(parts.Lines[index], what);
            countedKg += line.CountedMassKg;
            lines.Add(line);
        }

        // Gas the rocket counts outside its fuel lines (other tanks and pipes) stays as it is all flight.
        double otherGasKg = Math.Max(0.0, parts.Network.GasMass - countedKg);
        float structure = parts.Network.DryMass + (float)otherGasKg;
        if (what.CargoSlots.HasValue)
        {
            structure += what.CargoSlots.Value - parts.CargoSlotsFilled;
        }

        structure += (float)what.AddCargoKg;
        float throttle = what.Throttle ?? FirstThrottle(parts);
        bool enginesOn = false;
        for (int index = 0; index < parts.Engines.Count; index++)
        {
            enginesOn |= parts.Engines[index].OnOff;
        }

        RocketCraft craft = new RocketCraft(structure, lines,
            rocket.RocketNetwork.Engines.Count > 0 ? rocket.RocketNetwork.Engines[0].MaxThrust : 0f,
            rocket.MaxRecordedThrust, rocket.AutomatedLanding, throttle, enginesOn, rocket.GetThrust(), Power(parts, what))
        {
            HighestRecordedThrust = (float)GameMembers.RocketHighestRecordedThrust.GetValue(rocket),
            ThrustScale = what.ThrustScale
        };
        if (what.FuelMol.HasValue)
        {
            craft.SetFuelMoles(what.FuelMol.Value);
        }

        return craft;
    }

    /// <summary>The engines' throttle as Rocket.GetThrottle reads it: the first engine's.</summary>
    internal static float FirstThrottle(RocketParts parts) =>
        parts.Engines.Count > 0 ? parts.Engines[0].Throttle : 0f;

    internal static FuelSample SampleOf(FuelLineRead line, RocketWhatIf what)
    {
        FuelSample sample = FuelSample.Of(line.Network.Atmosphere);
        if (what.FuelMix != null)
        {
            sample = sample.WithMix(what.FuelMix);
        }

        return what.FuelTemperatureK.HasValue ? sample.WithTemperature(what.FuelTemperatureK.Value) : sample;
    }

    private static FuelLine LineOf(FuelLineRead read, RocketWhatIf what)
    {
        Atmosphere pipe = read.Network.Atmosphere;
        List<FuelTank> tanks = new List<FuelTank>(read.Tanks.Count);
        for (int index = 0; index < read.Tanks.Count; index++)
        {
            Atmosphere atmosphere = read.Tanks[index].Atmosphere;
            tanks.Add(new FuelTank(atmosphere.Volume.ToDouble(), atmosphere.TotalMoles.ToDouble(),
                read.Tanks[index].Counted));
        }

        FuelSample sample = SampleOf(read, what);
        double kpaLitres = KpaLitresPerMol(read, sample);
        FuelBurn burn = ThrustProbe.BurnFor(read.Engines[0], sample, kpaLitres);
        double exhaust = 0.0;
        for (int index = 0; index < read.Engines.Count; index++)
        {
            RocketEngineBase engine = read.Engines[index];
            exhaust += engine.OnOff && engine.Powered ? engine.PassedMoles.ToDouble() : 0.0;
        }

        return new FuelLine(pipe.Volume.ToDouble(), pipe.TotalMoles.ToDouble(), tanks, read.Engines.Count, burn)
        {
            ExhaustMoles = exhaust
        };
    }

    // P V / n of the first tank (else the pipe) as the game reads it, scaled to a what-if temperature.
    private static double KpaLitresPerMol(FuelLineRead read, FuelSample sample)
    {
        Atmosphere atmosphere = read.Tanks.Count > 0 ? read.Tanks[0].Atmosphere : read.Network.Atmosphere;
        double moles = atmosphere.TotalMoles.ToDouble();
        double kelvin = atmosphere.Temperature.ToDouble();
        double measured = moles > 0.0
            ? atmosphere.PressureGassesAndLiquids.ToDouble() * atmosphere.Volume.ToDouble() / moles
            : 8.3144 * sample.TemperatureK;
        return kelvin > 0.0 ? measured * sample.TemperatureK / kelvin : measured;
    }

    private static PowerBank Power(RocketParts parts, RocketWhatIf what)
    {
        double charge = 0.0;
        double capacity = 0.0;
        for (int index = 0; index < parts.Batteries.Count; index++)
        {
            charge += parts.Batteries[index].PowerStored;
            capacity += parts.Batteries[index].PowerMaximum;
        }

        if (what.BatteryPercent.HasValue)
        {
            charge = capacity * what.BatteryPercent.Value / 100.0;
        }

        if (what.BatteryJ.HasValue)
        {
            charge = Math.Min(capacity, what.BatteryJ.Value);
        }

        double other = what.ExtraLoadW;
        double engines = 0.0;
        for (int index = 0; index < parts.Loads.Count; index++)
        {
            PowerLoadRead load = parts.Loads[index];
            if (load.IsEngine)
            {
                engines += load.OnW;
            }
            else
            {
                other += load.NowW;
            }
        }

        return new PowerBank(charge, capacity, other, engines);
    }

    /// <summary>
    /// The draw at a stop: what AutoShutOff leaves on (RocketAvionicsDevice.RunAutoShutOff switches off every device on
    /// the avionics' data network but avionics, batteries and circuit housings, RocketAvionicsDevice.cs:930-943), plus
    /// the miners and cargo holds while mining, plus any extra load.
    /// </summary>
    internal static double ParkLoad(RocketParts parts, bool mining, double extraW)
    {
        double load = extraW;
        bool shutOff = parts.Rocket.AutomatedShutOff;
        for (int index = 0; index < parts.Loads.Count; index++)
        {
            PowerLoadRead device = parts.Loads[index];
            if (device.IsEngine)
            {
                continue;
            }

            if (mining && device.IsMiningGear)
            {
                load += device.OnW;
            }
            else if (!shutOff || device.StaysOnAtArrival)
            {
                load += device.NowW;
            }
        }

        return load;
    }
}

/// <summary>The legs from the rocket's state to a target, and how the route was laid.</summary>
internal sealed class RoutePlan
{
    internal RoutePlan(List<FlightLeg> legs, List<RouteHop> hops, string? problem)
    {
        Legs = legs;
        Hops = hops;
        Problem = problem;
    }

    internal List<FlightLeg> Legs { get; }

    internal List<RouteHop> Hops { get; }

    /// <summary>Why the route stops short; null when it reaches the target.</summary>
    internal string? Problem { get; }
}

/// <summary>One hop of a route: the nodes, the distance, and the share already flown.</summary>
internal sealed class RouteHop
{
    internal RouteHop(SpaceMapNode from, SpaceMapNode to, float distance, float done)
    {
        From = from;
        To = to;
        Distance = distance;
        Done = done;
    }

    internal SpaceMapNode From { get; }

    internal SpaceMapNode To { get; }

    internal float Distance { get; }

    internal float Done { get; }
}

/// <summary>Lays the route from the rocket's state as the game would fly it, and turns it into legs.</summary>
internal static class RoutePlanner
{
    /// <summary>
    /// From where the rocket is now. A rocket in transit to another target than the one asked for is turned round as
    /// Rocket.ChangeTarget does (Rocket.cs:1889-1953): it keeps its hop when the new route starts with it; otherwise
    /// a hop in space reverses with Progress = 1 - Progress, and a launch becomes a landing back on its pad. A landing
    /// rocket given another target would turn into a launch from its height; that is not forecast.
    /// </summary>
    internal static RoutePlan FromRocket(Rocket rocket, SpaceMapNode target, ReEntryProfile profile)
    {
        List<FlightLeg> legs = new List<FlightLeg>(6);
        List<RouteHop> hops = new List<RouteHop>(6);
        SpaceMapNode? current = rocket.CurrentNode;
        if (current == null)
        {
            return new RoutePlan(legs, hops, "The rocket is at no space-map node.");
        }

        NodeTransit? transit = rocket.CurrentTransit;
        RocketState state = rocket.RocketState;
        SpaceMapNode start = current;
        bool moving = transit != null && rocket.Progress != 0f;
        if (state == RocketState.Landing && transit != null)
        {
            if (target != transit.Destination)
            {
                return new RoutePlan(legs, hops,
                    "The rocket is landing; another target would turn it into a launch from its present height " +
                    "(Rocket.ChangeTarget, Rocket.cs:1936-1940), which is not forecast. Forecast the landing (omit to).");
            }

            hops.Add(new RouteHop(transit.From, transit.Destination, transit.GetDistance(), rocket.GetMapProgress()));
            legs.Add(MidwayLanding(rocket, transit, false));
            return new RoutePlan(legs, hops, null);
        }

        if (moving)
        {
            bool keeps = target == rocket.TargetNode ||
                         (SpaceMapPathFinder.GetNextConnection(current, target, out NodeTransit next, out _) &&
                          next.Destination == transit!.Destination && next.From == transit.From);
            if (keeps)
            {
                hops.Add(new RouteHop(transit!.From, transit.Destination, transit.GetDistance(), rocket.Progress));
                legs.Add(FirstLeg(rocket, transit, profile));
                start = transit.Destination;
            }
            else if (state == RocketState.Launching)
            {
                hops.Add(new RouteHop(transit!.Destination, transit.From, transit.GetDistance(), 0f));
                legs.Add(MidwayLanding(rocket, transit, false));
                start = transit.From;
            }
            else
            {
                float back = 1f - rocket.Progress;
                hops.Add(new RouteHop(transit!.Destination, current, transit.GetDistance(), back));
                legs.Add(LegFor(transit.Destination, current, transit.GetDistance(), back, profile));
                start = current;
            }
        }

        string? problem = AddRoute(start, target, profile, legs, hops);
        return new RoutePlan(legs, hops, problem);
    }

    /// <summary>The route between two nodes as legs (all from rest).</summary>
    internal static string? AddRoute(SpaceMapNode from, SpaceMapNode target, ReEntryProfile profile,
        List<FlightLeg> legs, List<RouteHop> hops)
    {
        if (from == target)
        {
            return null;
        }

        List<NodeTransit>? route = SpaceRoutes.Plan(from, target);
        if (route == null)
        {
            return $"No route from {SpaceRoutes.NameOf(from)} to {SpaceRoutes.NameOf(target)} " +
                   "(SpaceMapPathFinder found none).";
        }

        for (int index = 0; index < route.Count; index++)
        {
            NodeTransit hop = route[index];
            hops.Add(new RouteHop(hop.From, hop.Destination, hop.GetDistance(), 0f));
            legs.Add(LegFor(hop.From, hop.Destination, hop.GetDistance(), 0f, profile));
        }

        return null;
    }

    /// <summary>The re-entry altitude of a profile (Rocket.ReEntryProfiles, Rocket.cs:77-99).</summary>
    internal static float AltitudeOf(ReEntryProfile profile) =>
        Rocket.ReEntryProfiles.TryGetValue(profile, out float altitude) ? altitude : Rocket.OPTIMAL_LANDING_ALTITUDE;

    internal static LandingLeg ReEntryLeg(SpaceMapNode from, SpaceMapNode pad, float distance, ReEntryProfile profile) =>
        new LandingLeg(SpaceRoutes.NameOf(from), SpaceRoutes.NameOf(pad), distance, RocketModel.GravityAt(pad.Owner),
            new LandingStart.ReEntry(AltitudeOf(profile), pad.Owner != null && pad.Owner.IsOrbital,
                SpaceMap.Current.DistanceToOrbit, WorldSetting.Current.Gravity));

    private static FlightLeg LegFor(SpaceMapNode from, SpaceMapNode to, float distance, float done,
        ReEntryProfile profile)
    {
        string fromName = SpaceRoutes.NameOf(from);
        string toName = SpaceRoutes.NameOf(to);
        if (SpaceRoutes.IsPad(from))
        {
            return new LaunchLeg(fromName, toName, distance, RocketModel.GravityAt(from.Owner), LaunchStart.Pad);
        }

        return SpaceRoutes.IsPad(to)
            ? ReEntryLeg(from, to, distance, profile)
            : new SpaceHop(fromName, toName, distance, done);
    }

    private static FlightLeg FirstLeg(Rocket rocket, NodeTransit transit, ReEntryProfile profile) =>
        rocket.RocketState switch
        {
            RocketState.Launching => new LaunchLeg(SpaceRoutes.NameOf(transit.From),
                SpaceRoutes.NameOf(transit.Destination), transit.GetDistance(),
                RocketModel.GravityAt(transit.From.Owner),
                new LaunchStart(false, rocket.GetAltitude(), rocket.Velocity, rocket.Progress)),
            _ => LegFor(transit.From, transit.Destination, transit.GetDistance(), rocket.Progress, profile)
        };

    // A landing under way (or a launch turned round), from the rocket's live numbers.
    private static LandingLeg MidwayLanding(Rocket rocket, NodeTransit transit, bool initialise)
    {
        SpaceMapNode pad = rocket.RocketState == RocketState.Landing ? transit.Destination : transit.From;
        float mount = pad.Owner != null ? pad.Owner.RocketTransformPosition.y : 0f;
        float visual = rocket.RocketParentTransform != null
            ? rocket.RocketParentTransform.position.y - mount
            : rocket.GetAltitude();
        return new LandingLeg(SpaceRoutes.NameOf(transit.From), SpaceRoutes.NameOf(pad), transit.GetDistance(),
            RocketModel.GravityAt(pad.Owner),
            new LandingStart.Midway(rocket.GetAltitude(), rocket.Velocity, rocket.Acceleration, visual, initialise));
    }
}
