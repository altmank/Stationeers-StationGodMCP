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
/// flight rules (ported from Rocket.cs with citations in Pure/Rockets), every engine's own feed law (Pure/Rockets/
/// EngineFeeds) and its thrust measured by the game's own combustion on a copy of what it draws. Nothing in the game
/// changes.
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
        RefuseManned(args, rocket, target);
        List<string> assumptions = new List<string>(16);
        RoutePlan plan = RoutePlanner.FromRocket(rocket, target, profile);
        FlightSimulator simulator = RocketModel.Simulator();
        RocketCraft craft = RocketModel.Craft(parts, what);
        ForecastMiningView? mining = AddStop(args, rocket, parts, craft, target, profile, plan, assumptions);

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

        if (pad?.Owner != null && pad.Owner.IsOrbital)
        {
            assumptions.Add($"{SpaceRoutes.NameOf(pad)} is an orbital launch mount: its landing check and descent use " +
                            "gravity 1 m/s² (-1 clamped, Rocket.cs:2174-2181, 3089-3100) and its re-entry altitude is cut " +
                            "down (Rocket.cs:1665-1681).");
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
            Verdict(run, plan, column, floor, mining), craft.FuelMoles, leastFuel, limitsView, profiles, column,
            new CombustionView(CombustionRates.Current()), mining);
    }

    private static void Refuse(RocketParts parts)
    {
        if (parts.Unmodelled.Count > 0)
        {
            throw ApiErrors.Refused("engine_not_modelled",
                $"{Names.Of(parts.Unmodelled[0])} is a {parts.Unmodelled[0].GetType().Name}, none of the game's six " +
                "engine classes (or a class built on one): its feed law is unknown to the forecast.");
        }

        for (int index = 0; index < parts.EngineReads.Count; index++)
        {
            EngineRead read = parts.EngineReads[index];
            if (read.MissingInput != null)
            {
                throw ApiErrors.Refused("no_fuel_line",
                    $"{Names.Of(read.Engine)} ({EngineSpecs.NameOf(read.ClassName)}): {read.MissingInput}. The game " +
                    "keeps it inoperable and it makes no thrust (its IsOperable).");
            }
        }

        if (parts.EngineReads.Count == 0)
        {
            throw ApiErrors.Refused("no_fuel_line", "The rocket has no engine: there is nothing to burn.");
        }
    }

    // A rocket with someone in a Crew Module Chair may only target launch mounts (Rocket.IsValidMannedTarget and
    // EnforceMannedTargetRestriction, Rocket.cs:1871-1885): the game clears any other target.
    private static void RefuseManned(Args args, Rocket rocket, SpaceMapNode target)
    {
        if (!rocket.IsManned)
        {
            return;
        }

        bool stops = (args.OptionalDouble("park_s") ?? 0.0) > 0.0 || (args.OptionalBool("then_return") ?? false) ||
                     (args.OptionalBool("mine") ?? false);
        if (!Rocket.IsValidMannedTarget(target) || (stops && !SpaceRoutes.IsPad(target)))
        {
            throw ApiErrors.Refused("manned_target",
                $"Someone sits in a Crew Module Chair: the rocket may only target a launch mount (ground or orbital); the " +
                $"game clears {SpaceRoutes.NameOf(target)} as a target. Give a pad as 'to'.");
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
            return SpaceRoutes.ResolveFor(to, rocket);
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

        return SpaceRoutes.ResolveFor("pad", rocket);
    }

    // A stop at the target (park_s, mining, cargo, payload deploy, transfers) and the way home (then_return).
    private static ForecastMiningView? AddStop(Args args, Rocket rocket, RocketParts parts, RocketCraft craft,
        SpaceMapNode target, ReEntryProfile profile, RoutePlan plan, List<string> assumptions)
    {
        float park = (float)(args.OptionalDouble("park_s") ?? 0.0);
        bool mine = args.OptionalBool("mine") ?? false;
        bool fill = args.OptionalBool("fill_holds") ?? false;
        int? askedSlots = args.OptionalInt("add_cargo_slots", 0, 100000);
        bool back = args.OptionalBool("then_return") ?? false;
        bool deploy = args.OptionalBool("deploy_payload") ?? false;
        double? transferMol = args.OptionalDouble("transfer_mol");
        ThingId? partnerId = args.OptionalThingId("transfer_with");
        double transferJ = args.OptionalDouble("transfer_battery_j") ?? 0.0;
        if (park < 0f)
        {
            throw ApiErrors.InvalidArgument("Argument 'park_s' must be 0 or more.");
        }

        if (transferMol.HasValue && partnerId.HasValue)
        {
            throw ApiErrors.InvalidArgument("Give transfer_mol or transfer_with, not both.");
        }

        SiteRead? site = MiningReads.SiteOf(target);
        ForecastMiningView? mining = site != null ? MiningAt(site, parts, park, mine, assumptions) : null;
        if (mining != null && !mining.Collectable)
        {
            assumptions.Add($"Warning: this loadout collects nothing at {mining.Site} ({mining.Kind} site): " +
                            string.Join(" ", mining.Problems ?? new List<string>()));
        }

        // A hold stops counting at its last slot (RocketChuteStorage.TryProcessImport, RocketChuteStorage.cs:115-131).
        int free = Math.Max(0, parts.CargoSlotsTotal - parts.CargoSlotsFilled);
        int addSlots = fill ? free : Math.Min(askedSlots ?? 0, free);
        if (!fill && !askedSlots.HasValue && mine && mining != null && park > 0f)
        {
            addSlots = Math.Min(mining.SlotsInStop, free);
        }

        List<LineTransfer> fuel = Transfers(rocket, parts, craft, transferMol, partnerId, assumptions);
        double payloadKg = deploy ? DeployedKg(parts, target, assumptions) : 0.0;
        if (deploy && park < 20f)
        {
            park = 20f;
        }

        if (park > 0f || mine || addSlots > 0 || back || deploy || fuel.Count > 0 || Math.Abs(transferJ) > 0.0)
        {
            if (SpaceRoutes.IsPad(target) && (mine || addSlots > 0 || back || deploy))
            {
                throw ApiErrors.InvalidArgument(
                    "mine, fill_holds, add_cargo_slots, deploy_payload and then_return are for a stop in space; 'to' is a pad.");
            }

            double load = args.OptionalDouble("park_load_w") ??
                          RocketModel.ParkLoad(parts, mine, args.OptionalDouble("extra_load_w") ?? 0.0);
            plan.Legs.Add(new ParkLeg(SpaceRoutes.NameOf(target), park, load, addSlots, -payloadKg, fuel, transferJ));
            assumptions.Add($"At {SpaceRoutes.NameOf(target)}: {park:0} s parked drawing {load:0.#} W a tick" +
                            (addSlots > 0 ? $", {addSlots} cargo slots filled (1 kg each; the ice or ore itself weighs nothing to the game, RocketNetwork.CargoMass = 0, RocketNetwork.cs:48)" : string.Empty) +
                            (payloadKg > 0.0 ? $", payloads deployed (-{payloadKg:0} kg; the 20 s Deploy action, RocketPayload.cs:22)" : string.Empty) +
                            (Math.Abs(transferJ) > 0.0 ? $", {transferJ:0} J battery charge by Transfer" : string.Empty) +
                            (rocket.AutomatedShutOff ? "; AutoShutOff switches the engine off on arrival" : string.Empty) + ".");
        }

        if (back && plan.Problem == null)
        {
            string? home = args.OptionalString("return_to");
            SpaceMapNode pad = SpaceRoutes.ResolveFor(home ?? "pad", rocket);
            if (rocket.IsManned && !Rocket.IsValidMannedTarget(pad))
            {
                throw ApiErrors.Refused("manned_target",
                    $"Someone sits in a Crew Module Chair: return_to must be a launch mount, not {SpaceRoutes.NameOf(pad)}.");
            }

            string? problem = RoutePlanner.AddRoute(target, pad, profile, plan.Legs, plan.Hops);
            if (problem != null)
            {
                assumptions.Add(problem);
            }
        }

        return mining;
    }

    // mine: each machine switched on at the site, as the player switches it on; one that is off now is named.
    private static ForecastMiningView MiningAt(SiteRead site, RocketParts parts, float park, bool mine,
        List<string> assumptions)
    {
        List<string> off = new List<string>();
        List<MachineYieldView> machines = MiningPlans.Machines(site, parts, out List<string> problems, mine, off);
        if (off.Count > 0)
        {
            assumptions.Add($"{string.Join(", ", off)} {(off.Count == 1 ? "is" : "are")} switched off; mine models " +
                            $"{(off.Count == 1 ? "it" : "them")} switched on at {SpaceRoutes.NameOf(site.Node)}, as " +
                            "you switch them on to mine.");
        }

        bool collectable = false;
        double perHour = 0.0;
        for (int index = 0; index < machines.Count; index++)
        {
            if (machines[index].Collected)
            {
                collectable = true;
                perHour += machines[index].UnitsPerHour;
            }
            else if (machines[index].Problems != null)
            {
                problems.AddRange(machines[index].Problems!);
            }
        }

        double units = mine ? perHour * park / 3600.0 : 0.0;
        int slots = site.Kind == "gas" ? 0 : (int)Math.Ceiling(units / Math.Max(1, site.StackSize));
        return new ForecastMiningView(SpaceRoutes.NameOf(site.Node), site.Kind, collectable, perHour, units, slots,
            problems);
    }

    // Payloads leave at a node that offers Deploy (the planet's orbit); each weighs its MassContribution (RocketPayload
    // .cs:20; the bay itself stays at 100, RocketPayloadBay.cs:55).
    private static double DeployedKg(RocketParts parts, SpaceMapNode target, List<string> assumptions)
    {
        if (!target.HasAction(RocketMode.Deploy))
        {
            throw ApiErrors.InvalidArgument(
                $"deploy_payload: {SpaceRoutes.NameOf(target)} offers no Deploy action (payloads deploy only at the planet's orbit node).");
        }

        double kg = parts.PayloadKg;
        if (kg <= 0.0)
        {
            assumptions.Add("deploy_payload: no payload is attached to a bay, so nothing leaves (give payload_kg to try one).");
        }

        return kg;
    }

    // Fuel received or given at the stop: transfer_mol spread over the lines in proportion, or transfer_with: the
    // Transfer action Mixes each pair of compatible sockets' pipe networks by volume every tick (RocketGasUmbilicalFemale
    // .cs:142-148, AtmosphereHelper.Mix All), so without a pump both sides end at the same moles per litre.
    private static List<LineTransfer> Transfers(Rocket rocket, RocketParts parts, RocketCraft craft, double? moles,
        ThingId? partnerId, List<string> assumptions)
    {
        List<LineTransfer> transfers = new List<LineTransfer>(2);
        if (moles.HasValue && Math.Abs(moles.Value) > 0.0)
        {
            double total = craft.FuelMoles;
            for (int index = 0; index < craft.Lines.Count; index++)
            {
                double share = total > 0.0 ? craft.Lines[index].Moles / total : 1.0 / craft.Lines.Count;
                transfers.Add(new LineTransfer(index, moles.Value * share));
            }

            assumptions.Add($"Transfer at the stop: {moles.Value:0} mol {(moles.Value > 0 ? "received" : "given")}, " +
                            "spread over the fuel lines in proportion and taken to have the lines' own make-up.");
            return transfers;
        }

        if (!partnerId.HasValue)
        {
            return transfers;
        }

        Rocket partner = RocketLocator.Find(partnerId.Value);
        if (partner == rocket)
        {
            throw ApiErrors.InvalidArgument("transfer_with: give another rocket.");
        }

        RocketParts other = RocketParts.Of(partner);
        int paired = 0;
        for (int index = 0; index < parts.FluidSockets.Count; index++)
        {
            RocketGasUmbilicalFemale socket = parts.FluidSockets[index];
            int line = LineOf(parts, socket);
            RocketGasUmbilicalFemale? match = Compatible(socket, other);
            if (match == null || match.ConnectedPipeNetwork?.Atmosphere == null)
            {
                continue;
            }

            paired++;
            FuelLineRead theirs = other.ContentsOf(match.ConnectedPipeNetwork);
            (double theirMoles, double theirLitres) = Totals(theirs);
            if (line < 0)
            {
                assumptions.Add($"Transfer with {partner.DisplayName}: {Names.Of(socket)} is on no engine fuel line, so " +
                                "what it receives does not reach the engines unless a pump moves it.");
                continue;
            }

            FuelLine ours = craft.Lines[line];
            double delta = SocketTransfer.Equalised(ours.Moles, ours.VolumeL, theirMoles, theirLitres);
            transfers.Add(new LineTransfer(line, delta));
            assumptions.Add($"Transfer with {partner.DisplayName} through {Names.Of(socket)} and {Names.Of(match)}: the two " +
                            $"networks equalise by volume ({ours.Moles:0} mol in {ours.VolumeL:0} L here, {theirMoles:0} mol " +
                            $"in {theirLitres:0} L there), {delta:+0;-0} mol on this line; a pump on the receiving side moves " +
                            "more, which this does not model. The received fuel is taken to have this line's make-up. Both " +
                            "rockets must be at the stop's node (Transfer needs two rockets at one node, SpaceMapNode.cs:354-355).");
        }

        if (paired == 0)
        {
            throw ApiErrors.InvalidArgument(
                $"transfer_with: no gas or liquid umbilical socket on {partner.DisplayName} matches one on this rocket.");
        }

        return transfers;
    }

    private static int LineOf(RocketParts parts, RocketGasUmbilicalFemale socket)
    {
        for (int index = 0; index < parts.Lines.Count; index++)
        {
            if (ReferenceEquals(parts.Lines[index].Network, socket.ConnectedPipeNetwork))
            {
                return index;
            }
        }

        return -1;
    }

    private static RocketGasUmbilicalFemale? Compatible(RocketGasUmbilicalFemale socket, RocketParts other)
    {
        for (int index = 0; index < other.FluidSockets.Count; index++)
        {
            if (socket.IsCompatibleWith(other.FluidSockets[index]))
            {
                return other.FluidSockets[index];
            }
        }

        return null;
    }

    private static (double Moles, double Litres) Totals(FuelLineRead line)
    {
        double moles = line.Network.Atmosphere.TotalMoles.ToDouble();
        double litres = line.Network.Atmosphere.Volume.ToDouble();
        for (int index = 0; index < line.Tanks.Count; index++)
        {
            moles += line.Tanks[index].Atmosphere.TotalMoles.ToDouble();
            litres += line.Tanks[index].Atmosphere.Volume.ToDouble();
        }

        return (moles, litres);
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
        if ((what.BatteryJ.HasValue || what.BatteryPercent.HasValue) && !craft.Power.HasBattery)
        {
            assumptions.Add("Warning: battery_j and battery_percent change nothing: the rocket has no battery, so its " +
                            "engines are unpowered off the tower. Add a battery to it.");
        }

        assumptions.Add($"Engines on for every launch and hop at throttle {craft.Throttle:0.#} (the player switches " +
                        "them on to leave; AutoShutOff turns them off on arrival); the landing autopilot sets its own.");
        for (int index = 0; index < parts.EngineReads.Count; index++)
        {
            EngineRead read = parts.EngineReads[index];
            if (read.Feed == null)
            {
                continue;
            }

            assumptions.Add($"{Names.Of(read.Engine)} ({EngineSpecs.NameOf(read.ClassName)}): {read.Feed.Law}" +
                            (read.HeatExchange != null
                                ? " Its heat-exchange input heats that line after each burn; that heat is not simulated."
                                : string.Empty));
        }

        if (craft.Engines.Count > 0)
        {
            EngineRead first = parts.EngineReads[0];
            double now = FullThrottleForce(craft, parts, first, what, 0.0);
            double colder = FullThrottleForce(craft, parts, first, what, -100.0);
            double warmer = FullThrottleForce(craft, parts, first, what, 100.0);
            assumptions.Add("Each line keeps its present make-up and temperature all flight: draining takes every gas and " +
                            "liquid in proportion (GasMixture.Remove), and boiling, condensing and the heat tanks trade " +
                            "with the air around them (Tank.cs:25-27) are not simulated. The first engine's full-throttle " +
                            $"thrust now is {now:0} N, {colder:0} N with its fuel 100 K colder, {warmer:0} N 100 K warmer " +
                            "(the game's combustion).");
        }

        assumptions.Add("Power: every device's draw per 0.5 s tick comes off the batteries as now (PowerTick.cs:88-150); " +
                        "flat batteries leave the engines unpowered and they stop (RocketEngineBase.cs:450-458).");
        assumptions.Add("Cargo weighs 1 kg per filled slot (RocketChuteStorage.cs:30); what is in the slots weighs nothing. " +
                        "Gas and liquid in tanks count (RocketNetwork.CalculateGasMass); canisters in tank storage do not.");
        what.Describe(assumptions);
    }

    // The first tick of a full-throttle burn of the first engine on its lines as they are, the fuel shifted in temperature.
    private static double FullThrottleForce(RocketCraft craft, RocketParts parts, EngineRead read, RocketWhatIf what,
        double shiftK)
    {
        RocketCraft copy = craft.Copy();
        EngineUnit unit = copy.Engines[0];
        EngineDraw draw = unit.Feed.Draw(100f, copy.Lines[unit.Input1],
            unit.Input2.HasValue ? copy.Lines[unit.Input2.Value] : null);
        if (Math.Abs(shiftK) < 1e-9)
        {
            return unit.Thrust.Burn(draw).ForceN;
        }

        FuelSample one = RocketModel.SampleOf(parts.Lines[read.Input1!.Value], what);
        FuelSample? two = read.Input2.HasValue ? RocketModel.SampleOf(parts.Lines[read.Input2!.Value], what) : null;
        ProbeThrust shifted = new ProbeThrust(read.Engine, one.WithTemperature(Math.Max(1.0, one.TemperatureK + shiftK)),
            two?.WithTemperature(Math.Max(1.0, two.TemperatureK + shiftK)));
        return shifted.Burn(draw).ForceN;
    }

    private static string Verdict(FlightRun run, RoutePlan plan, ColumnView? column, float floor,
        ForecastMiningView? mining)
    {
        if (plan.Problem != null)
        {
            return plan.Problem;
        }

        if (run.Legs.Count == 0)
        {
            return "Nothing to fly: the rocket is at the target.";
        }

        string columnNote = column != null && !column.Clear
            ? " Something stands in the landing column: the rocket would hit it."
            : string.Empty;
        string miningNote = mining != null && !mining.Collectable
            ? $" This loadout collects nothing at {mining.Site} ({mining.Kind} site): see mining.problems."
            : string.Empty;
        for (int index = 0; index < run.Legs.Count; index++)
        {
            LegResult leg = run.Legs[index];
            if (!leg.Outcome.Continues)
            {
                return $"{leg.Leg.Kind} {leg.Leg.From} -> {leg.Leg.To}: {leg.Outcome.Detail}{columnNote}{miningNote}";
            }
        }

        LegResult last = run.Legs[run.Legs.Count - 1];
        string landing = last.Outcome is LegOutcome.Landed landed && !float.IsNaN(landed.Landing.Confidence) &&
                         landed.Landing.Confidence < floor
            ? $" Landing confidence {landed.Landing.Confidence:0.00} is below the floor {floor:0.00}: little thrust margin."
            : string.Empty;
        return $"Arrives with {last.Tally.FuelEndMol:0} mol left.{landing}{columnNote}{miningNote}";
    }
}
