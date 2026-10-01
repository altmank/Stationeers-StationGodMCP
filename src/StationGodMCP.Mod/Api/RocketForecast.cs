#nullable enable

using System;
using System.Collections.Generic;
using Objects.Rockets;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Rockets;

namespace StationGodMCP.Api;

/// <summary>
/// rocket_forecast: flies a copy of the rocket along the route the game's pathfinder lays, tick by tick, with the game's
/// flight rules (ported from Rocket.cs with citations in Pure/Rockets) and the engine's thrust measured by the game's
/// own combustion on a copy of the fuel. Nothing in the game changes.
/// </summary>
internal static class RocketForecastApi
{
    private static readonly ReEntryProfile[] Profiles =
        { ReEntryProfile.Low, ReEntryProfile.Medium, ReEntryProfile.High, ReEntryProfile.Max };

    internal static RocketForecastView Handle(Args args)
    {
        Rocket rocket = RocketLocator.One(args);
        RocketParts parts = RocketParts.Of(rocket);
        Refuse(parts);
        RocketWhatIf what = RocketWhatIf.From(args);
        float floor = (float)(args.OptionalDouble("min_confidence") ?? 0.25);
        if (floor < 0f || floor > 1f)
        {
            throw ApiErrors.InvalidArgument("Argument 'min_confidence' must be from 0 to 1.");
        }

        bool limits = args.OptionalBool("limits") ?? true;
        ReEntryProfile profile = ProfileOf(args.OptionalString("profile"), rocket.ReEntryProfile);
        SpaceMapNode target = TargetOf(args, rocket);
        List<string> assumptions = new List<string>(12);
        RoutePlan plan = RoutePlanner.FromRocket(rocket, target, profile);
        AddStop(args, rocket, parts, target, profile, plan, assumptions);

        FlightSimulator simulator = RocketModel.Simulator();
        RocketCraft craft = RocketModel.Craft(parts, what);
        FlightRun run = FlightForecast.Run(simulator, craft, plan.Legs);
        Assume(simulator, parts, what, craft, profile, assumptions);

        List<ForecastLegView> legs = new List<ForecastLegView>(run.Legs.Count);
        for (int index = 0; index < run.Legs.Count; index++)
        {
            legs.Add(new ForecastLegView(run.Legs[index]));
        }

        LandingLimitsView? limitsView = null;
        List<ProfileLandingView>? profiles = null;
        double? leastFuel = null;
        if (limits && run.BeforeLanding != null && run.Landing != null)
        {
            LandingLeg landing = run.Landing;
            float altitude = landing.Start is LandingStart.ReEntry entry ? entry.ProfileAltitude : RoutePlanner.AltitudeOf(profile);
            LandingLimits landingLimits = LandingLimits.Of(simulator, run.BeforeLanding, landing, floor, landing.Distance,
                landing.Gravity, altitude);
            limitsView = new LandingLimitsView(landingLimits, parts.Network.DryMass - parts.CargoSlotsFilled, floor);
            profiles = ProfileTable(simulator, run.BeforeLanding, landing);
        }

        if (limits && plan.Legs.Count > 0)
        {
            leastFuel = FlightForecast.LeastFuel(simulator, craft, plan.Legs, Math.Max(craft.FuelMoles * 4.0, 60000.0));
        }

        List<RouteHopView> route = new List<RouteHopView>(plan.Hops.Count);
        List<string> uncharted = new List<string>(2);
        SpaceMapNode? pad = null;
        for (int index = 0; index < plan.Hops.Count; index++)
        {
            RouteHop hop = plan.Hops[index];
            route.Add(new RouteHopView(SpaceRoutes.ViewOf(hop.From), SpaceRoutes.ViewOf(hop.To), hop.Distance, hop.Done));
            if (!hop.To.IsCharted)
            {
                uncharted.Add(SpaceRoutes.NameOf(hop.To));
            }

            if (SpaceRoutes.IsPad(hop.To))
            {
                pad = hop.To;
            }
        }

        if (!target.IsCharted)
        {
            assumptions.Add("The target is uncharted: the avionics' DestinationCode only takes a charted node " +
                            "(IsAccessible = IsCharted; RocketAvionicsDevice.cs:1235-1241, SpaceMapNode.cs:115). Chart its " +
                            "parent first.");
        }

        ColumnView? column = (args.OptionalBool("column_check") ?? true) && pad != null
            ? RocketColumn.Scan(rocket, pad)
            : null;
        return new RocketForecastView(new ThingId(rocket.ReferenceId), rocket.DisplayName, rocket.RocketState.ToString(),
            SpaceRoutes.ViewOf(target), route, uncharted, assumptions, legs, run.Succeeded && plan.Problem == null,
            Verdict(run, plan, column, floor), craft.FuelMoles, leastFuel, limitsView, profiles, column);
    }

    private static void Refuse(RocketParts parts)
    {
        if (parts.Unmodelled.Count > 0)
        {
            throw ApiErrors.Refused("engine_not_modelled",
                $"{Names.Of(parts.Unmodelled[0])} is a {parts.Unmodelled[0].GetType().Name}; the forecast flies only " +
                "the Pumped Gas Engine (GovernedGasEngine), whose draw does not depend on pressure.");
        }

        if (parts.Lines.Count == 0)
        {
            throw ApiErrors.Refused("no_fuel_line",
                "No engine on this rocket has a pipe network on its input: there is nothing to burn.");
        }
    }

    private static ReEntryProfile ProfileOf(string? text, ReEntryProfile current)
    {
        if (text == null)
        {
            return current == ReEntryProfile.None ? ReEntryProfile.Low : current;
        }

        for (int index = 0; index < Profiles.Length; index++)
        {
            if (string.Equals(Profiles[index].ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                return Profiles[index];
            }
        }

        throw ApiErrors.InvalidArgument("Argument 'profile' must be Low, Medium, High or Max.");
    }

    // The destination: 'to', else the rocket's target, else the launch pad (return and land).
    private static SpaceMapNode TargetOf(Args args, Rocket rocket)
    {
        string? to = args.OptionalString("to");
        if (to != null)
        {
            return SpaceRoutes.Resolve(to);
        }

        if (rocket.TargetNode != null)
        {
            return rocket.TargetNode;
        }

        if (rocket.RocketState == RocketState.OnLaunchMount)
        {
            throw ApiErrors.Refused("no_target",
                "The rocket stands on its pad with no target: give 'to' (a node's name or code).");
        }

        return SpaceRoutes.Resolve("pad");
    }

    // A stop at the target (park_s, mining, cargo) and the way home (then_return).
    private static void AddStop(Args args, Rocket rocket, RocketParts parts, SpaceMapNode target,
        ReEntryProfile profile, RoutePlan plan, List<string> assumptions)
    {
        float park = (float)(args.OptionalDouble("park_s") ?? 0.0);
        bool mine = args.OptionalBool("mine") ?? false;
        bool fill = args.OptionalBool("fill_holds") ?? false;
        int addSlots = args.OptionalInt("add_cargo_slots", 0, 100000) ?? 0;
        bool back = args.OptionalBool("then_return") ?? false;
        if (park < 0f)
        {
            throw ApiErrors.InvalidArgument("Argument 'park_s' must be 0 or more.");
        }

        // A hold stops counting at its last slot (RocketChuteStorage.TryProcessImport, RocketChuteStorage.cs:115-131).
        int free = Math.Max(0, parts.CargoSlotsTotal - parts.CargoSlotsFilled);
        addSlots = fill ? free : Math.Min(addSlots, free);

        if (park > 0f || mine || addSlots > 0 || back)
        {
            if (SpaceRoutes.IsPad(target))
            {
                throw ApiErrors.InvalidArgument(
                    "park_s, mine, fill_holds, add_cargo_slots and then_return are for a stop in space; 'to' is a pad.");
            }

            double load = args.OptionalDouble("park_load_w") ??
                          RocketModel.ParkLoad(parts, mine, args.OptionalDouble("extra_load_w") ?? 0.0);
            plan.Legs.Add(new ParkLeg(SpaceRoutes.NameOf(target), park, load, addSlots, 0.0));
            assumptions.Add($"At {SpaceRoutes.NameOf(target)}: {park:0} s parked drawing {load:0.#} W a tick" +
                            (addSlots > 0 ? $", {addSlots} cargo slots filled (1 kg each; the ice itself weighs nothing to the game, RocketNetwork.CargoMass = 0, RocketNetwork.cs:48)" : string.Empty) +
                            (rocket.AutomatedShutOff ? "; AutoShutOff switches the engine off on arrival" : string.Empty) + ".");
        }

        if (back && plan.Problem == null)
        {
            string? home = args.OptionalString("return_to");
            SpaceMapNode pad = SpaceRoutes.Resolve(home ?? "pad");
            string? problem = RoutePlanner.AddRoute(target, pad, profile, plan.Legs, plan.Hops);
            if (problem != null)
            {
                assumptions.Add(problem);
            }
        }
    }

    private static List<ProfileLandingView>? ProfileTable(FlightSimulator simulator, RocketCraft before,
        LandingLeg landing)
    {
        if (!(landing.Start is LandingStart.ReEntry entry))
        {
            return null;
        }

        List<ProfileLandingView> table = new List<ProfileLandingView>(Profiles.Length);
        for (int index = 0; index < Profiles.Length; index++)
        {
            float altitude = RoutePlanner.AltitudeOf(Profiles[index]);
            LandingLeg leg = new LandingLeg(landing.From, landing.To, landing.Distance, landing.Gravity,
                new LandingStart.ReEntry(altitude, entry.Orbital, entry.DistanceToOrbit, entry.WorldGravity));
            LegResult result = leg.Fly(simulator, before.Copy());
            double? lowest = Bisect.LowestPassing(0.0, before.ThrustScale, scale =>
            {
                RocketCraft trial = before.Copy();
                trial.ThrustScale = (float)scale;
                return LandingLimits.Clean(leg.Fly(simulator, trial));
            }, 0.001);
            table.Add(new ProfileLandingView(Profiles[index].ToString(), altitude, result,
                lowest.HasValue ? before.ThrustScale - lowest.Value : null));
        }

        return table;
    }

    private static void Assume(FlightSimulator simulator, RocketParts parts, RocketWhatIf what, RocketCraft craft,
        ReEntryProfile profile, List<string> assumptions)
    {
        assumptions.Add($"Physics step {simulator.PhysicsStep:0.###} s (Time.fixedDeltaTime), one 0.5 s game tick " +
                        $"every {simulator.StepsPerTick} steps; re-entry profile {profile} " +
                        $"({RoutePlanner.AltitudeOf(profile):0} m).");
        assumptions.Add($"Engines on for every launch and hop at throttle {craft.Throttle:0.#} (the player switches " +
                        "them on to leave; AutoShutOff turns them off on arrival); the landing autopilot sets its own.");
        FuelLineRead line = parts.Lines[0];
        FuelSample sample = RocketModel.SampleOf(line, what);
        double now = ThrustProbe.Burn(line.Engines[0], sample, FuelLine.MaxMolesPerTick).ForceN;
        double colder = ThrustProbe.Burn(line.Engines[0], sample.WithTemperature(Math.Max(1.0, sample.TemperatureK - 100.0)),
            FuelLine.MaxMolesPerTick).ForceN;
        double warmer = ThrustProbe.Burn(line.Engines[0], sample.WithTemperature(sample.TemperatureK + 100.0),
            FuelLine.MaxMolesPerTick).ForceN;
        assumptions.Add($"Fuel held at {sample.TemperatureK:0} K and its present mix all flight: draining keeps both " +
                        "(GasMixture.Remove takes every gas in proportion). Heat the tanks trade with the air around them " +
                        "(Tank convection and radiation, Tank.cs:25-27) is not simulated: full-throttle thrust per engine is " +
                        $"{now:0} N now, {colder:0} N 100 K colder, {warmer:0} N 100 K warmer (the game's combustion).");
        assumptions.Add("Power: every device's draw per 0.5 s tick comes off the batteries as now (PowerTick.cs:88-150); " +
                        "flat batteries leave the engines unpowered and they stop (RocketEngineBase.cs:450-458).");
        assumptions.Add("Cargo weighs 1 kg per filled slot (RocketChuteStorage.cs:30); what is in the slots weighs nothing.");
        what.Describe(assumptions);
    }

    private static string Verdict(FlightRun run, RoutePlan plan, ColumnView? column, float floor)
    {
        if (plan.Problem != null)
        {
            return plan.Problem;
        }

        if (run.Legs.Count == 0)
        {
            return "Nothing to fly: the rocket is at the target.";
        }

        string column_note = column != null && !column.Clear
            ? " Something stands in the landing column: the rocket would hit it."
            : string.Empty;
        for (int index = 0; index < run.Legs.Count; index++)
        {
            LegResult leg = run.Legs[index];
            if (!leg.Outcome.Continues)
            {
                return $"{leg.Leg.Kind} {leg.Leg.From} -> {leg.Leg.To}: {leg.Outcome.Detail}{column_note}";
            }
        }

        LegResult last = run.Legs[run.Legs.Count - 1];
        string landing = last.Outcome is LegOutcome.Landed landed && !float.IsNaN(landed.Landing.Confidence) &&
                         landed.Landing.Confidence < floor
            ? $" Landing confidence {landed.Landing.Confidence:0.00} is below the floor {floor:0.00}: little thrust margin."
            : string.Empty;
        return $"Arrives with {last.Tally.FuelEndMol:0} mol left.{landing}{column_note}";
    }
}
