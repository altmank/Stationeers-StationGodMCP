#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Objects.Rockets;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Rockets;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// rocket_status: where each rocket is and goes, what it weighs, its fuel lines, engines and their feed laws, thrust, the
/// auto-land confidence the avionics shows and the one the game will really check at its pad, cargo, power, burn time
/// and gear, all read from the game; plus checks of the forecast's port against the game's own numbers, and (parts)
/// every part's pose relative to the engine mount. Read only: every game method called here only computes.
/// </summary>
internal static class RocketStatusApi
{
    private const string HowRecorded =
        "thrust.max_recorded = the highest Rocket.GetThrust (all engines' Force) seen on any physics step since the " +
        "rocket was built, saved with the game, reset to 0 only while the rocket has no engine (Rocket.cs:1982-1989, " +
        "844, 885). The landing check uses max(max_recorded, first engine's prefab MaxThrust) (GetMaxExpectedThrust, " +
        "Rocket.cs:2710-2721), never the thrust the fuel gives now: thrust.achievable_full is that, by the game's " +
        "combustion on a copy of a full-throttle draw.";

    private const string ScreenCheck =
        "landing_check is what the Rocket Control screen shows (RocketAvionicsDevice.GetAutoLandConfidenceRatio: the " +
        "ground pad's hop and the world's gravity, RocketAvionicsDevice.cs:1294-1300); landing_at_pad is the check the " +
        "game makes as the rocket re-enters toward its pad, with that pad's hop and gravity (1 m/s2 at an orbital launch " +
        "mount; Rocket.cs:2174-2188). A landing whose check is 0 is aborted back to orbit.";

    internal static RocketStatusListView Handle(Args args)
    {
        ThingId? id = args.OptionalThingId("rocket_id");
        bool compact = args.OptionalBool("compact") ?? false;
        bool selfTest = args.OptionalBool("self_test") ?? !compact;
        bool withParts = args.OptionalBool("parts") ?? false;
        List<Rocket> rockets = id.HasValue ? new List<Rocket> { RocketLocator.Find(id.Value) } : RocketLocator.All();
        List<RocketStatusView> views = new List<RocketStatusView>(rockets.Count);
        for (int index = 0; index < rockets.Count; index++)
        {
            views.Add(Describe(rockets[index], selfTest, compact, withParts));
        }

        List<string> explanations = new List<string>(3) { HowRecorded, ScreenCheck };
        if (!compact)
        {
            explanations.Add("power: the game takes each device's watts off the batteries once per 0.5 s tick " +
                             "(PowerTick.cs:88-150), so seconds_left = charge / load x 0.5.");
        }

        return new RocketStatusListView(views, explanations);
    }

    internal static RocketStatusView Describe(Rocket rocket, bool selfTest, bool compact = false, bool withParts = false)
    {
        RocketParts parts = RocketParts.Of(rocket);
        List<string> notes = new List<string>(4);
        float profileAltitude = RoutePlanner.AltitudeOf(rocket.ReEntryProfile);
        RocketCraft craft = RocketModel.Craft(parts, RocketWhatIfNone.Value);
        RocketStatusView view = new RocketStatusView(new ThingId(rocket.ReferenceId),
            new ThingId(rocket.RocketNetwork.ReferenceId), rocket.DisplayName, rocket.AutomatedLanding,
            rocket.AutomatedShutOff, rocket.ReEntryProfile.ToString(), profileAltitude, Where(rocket, notes),
            Mass(rocket, parts, compact), Fuel(parts, compact), Engines(parts), Thrust(rocket, craft, notes),
            LandingCheck(rocket, parts, profileAltitude), Cargo(parts), Power(parts, compact),
            BurnTime(rocket, parts, craft), selfTest ? Checks(rocket, parts, craft, profileAltitude) : null, notes,
            rocket.IsManned, LandingAtPad(rocket, profileAltitude), MiningPlans.Loadout(parts).Collects,
            withParts ? Poses(parts) : null);
        Notes(rocket, parts, notes);
        return view;
    }

    private static RocketWhereView Where(Rocket rocket, List<string> notes)
    {
        List<RouteHopView> route = new List<RouteHopView>(4);
        List<string> uncharted = new List<string>(2);
        if (rocket.TargetNode != null)
        {
            RoutePlan plan = RoutePlanner.FromRocket(rocket, rocket.TargetNode, rocket.ReEntryProfile);
            for (int index = 0; index < plan.Hops.Count; index++)
            {
                RouteHop hop = plan.Hops[index];
                route.Add(new RouteHopView(SpaceRoutes.ViewOf(hop.From), SpaceRoutes.ViewOf(hop.To), hop.Distance,
                    hop.Done));
                if (!hop.To.IsCharted)
                {
                    uncharted.Add(SpaceRoutes.NameOf(hop.To));
                }
            }

            if (plan.Problem != null)
            {
                notes.Add(plan.Problem);
            }
        }

        RocketState state = rocket.RocketState;
        bool flying = state == RocketState.Launching || state == RocketState.Landing;
        return new RocketWhereView(state.ToString(), rocket.RocketMode.ToString(),
            rocket.CurrentNode != null ? SpaceRoutes.ViewOf(rocket.CurrentNode) : null,
            rocket.TargetNode != null ? SpaceRoutes.ViewOf(rocket.TargetNode) : null, rocket.Progress,
            flying ? rocket.GetAltitude() : null, rocket.Velocity, rocket.Acceleration,
            rocket.FlightControlRule.ToString(), route, uncharted);
    }

    private static RocketMassView Mass(Rocket rocket, RocketParts parts, bool compact) =>
        new RocketMassView(rocket.TotalMass(), parts.Network.DryMass, parts.Network.GasMass, parts.CargoSlotsFilled,
            compact ? null : parts.MassParts, parts.PayloadKg);

    private static List<FuelLineView> Fuel(RocketParts parts, bool compact)
    {
        List<FuelLineView> lines = new List<FuelLineView>(parts.Lines.Count);
        for (int index = 0; index < parts.Lines.Count; index++)
        {
            FuelLineRead line = parts.Lines[index];
            Atmosphere pipe = line.Network.Atmosphere;
            double total = pipe.TotalMoles.ToDouble();
            double liquid = pipe.GasMixture.GetTotalMolesLiquids.ToDouble();
            double litres = pipe.TotalVolumeLiquids.ToDouble();
            List<FuelTankView> tanks = new List<FuelTankView>(line.Tanks.Count);
            for (int tank = 0; tank < line.Tanks.Count; tank++)
            {
                FuelTankRead read = line.Tanks[tank];
                double moles = read.Atmosphere.TotalMoles.ToDouble();
                total += moles;
                liquid += read.Atmosphere.GasMixture.GetTotalMolesLiquids.ToDouble();
                litres += read.Atmosphere.TotalVolumeLiquids.ToDouble();
                tanks.Add(new FuelTankView(new ThingId(read.Owner.ReferenceId), Names.Of(read.Owner),
                    read.Atmosphere.Volume.ToDouble(), moles, read.Atmosphere.Temperature.ToDouble(),
                    read.Atmosphere.PressureGassesAndLiquids.ToDouble(), read.Counted));
            }

            lines.Add(new FuelLineView(new ThingId(line.Network.ReferenceId), line.Engines.Count,
                pipe.Volume.ToDouble(), pipe.TotalMoles.ToDouble(), total, pipe.Temperature.ToDouble(), Mix(line),
                compact ? null : tanks, liquid, litres, pipe.PressureGasses.ToDouble(), Feeds(parts, index)));
        }

        return lines;
    }

    private static List<string> Feeds(RocketParts parts, int line)
    {
        List<string> feeds = new List<string>(2);
        for (int index = 0; index < parts.EngineReads.Count; index++)
        {
            EngineRead read = parts.EngineReads[index];
            string name = EngineSpecs.NameOf(read.ClassName);
            if (read.Input1 == line)
            {
                feeds.Add($"{name} input 1");
            }

            if (read.Input2 == line)
            {
                feeds.Add($"{name} input 2");
            }
        }

        return feeds;
    }

    private static Dictionary<string, double> Mix(FuelLineRead line)
    {
        FuelSample sample = line.Sample();
        double total = sample.Total;
        Dictionary<string, double> mix = new Dictionary<string, double>(4);
        for (int index = 0; index < sample.Moles.Length; index++)
        {
            if (total > 0.0 && sample.Moles[index] / total >= 0.0005)
            {
                mix[GasTypes.All[index].ToString()] = RocketRound.Of(sample.Moles[index] / total, 4);
            }
        }

        return mix;
    }

    private static List<RocketEngineView> Engines(RocketParts parts)
    {
        List<RocketEngineView> engines = new List<RocketEngineView>(parts.EngineReads.Count);
        for (int index = 0; index < parts.EngineReads.Count; index++)
        {
            EngineRead read = parts.EngineReads[index];
            RocketEngineBase engine = read.Engine;
            engines.Add(new RocketEngineView(new ThingId(engine.ReferenceId), Names.Of(engine),
                engine.GetType().Name, engine.OnOff, engine.Powered, engine.Throttle, engine.Force,
                engine.PassedMoles.ToDouble(), engine.ExhaustVelocity, engine.ExhaustTemperature.ToDouble(),
                engine.MaxThrust, engine.UsedPower,
                read.Input1.HasValue ? new ThingId(parts.Lines[read.Input1.Value].Network.ReferenceId) : null,
                read.Feed != null && read.MissingInput == null, EngineSpecs.NameOf(read.ClassName), read.Feed?.Law,
                read.Input2.HasValue ? new ThingId(parts.Lines[read.Input2.Value].Network.ReferenceId) : null,
                read.HeatExchange != null ? new ThingId(read.HeatExchange.ReferenceId) : null, read.MissingInput));
        }

        return engines;
    }

    /// <summary>Full-throttle thrust of every engine on its lines as they are: the first tick's draw, by the game's combustion.</summary>
    internal static double? AchievableFullThrust(RocketCraft craft)
    {
        if (craft.Engines.Count == 0)
        {
            return null;
        }

        RocketCraft copy = craft.Copy();
        double total = 0.0;
        for (int index = 0; index < copy.Engines.Count; index++)
        {
            total += copy.Engines[index].Burn(100f, copy.Lines);
        }

        return total;
    }

    private static RocketThrustView Thrust(Rocket rocket, RocketCraft craft, List<string> notes)
    {
        float prefab = rocket.RocketNetwork.Engines.Count > 0 ? rocket.RocketNetwork.Engines[0].MaxThrust : 0f;
        double? achievable = AchievableFullThrust(craft);
        RocketThrustView view = new RocketThrustView(rocket.GetThrust(), rocket.MaxRecordedThrust, prefab,
            rocket.GetMaxExpectedThrust(), achievable, null, new CombustionView(CombustionRates.Current()));
        if (view.RecordedExceedsAchievable)
        {
            notes.Add($"The landing check assumes {view.MaxExpectedN:0} N but the fuel now gives {view.AchievableFullN:0} N " +
                      "at full throttle: a landing it passes can still crash. See rocket_forecast.");
        }

        return view;
    }

    private static LandingCheckView LandingCheck(Rocket rocket, RocketParts parts, float profileAltitude)
    {
        float deltaV = SpaceMap.Current.DistanceToOrbit * 2f;
        float gravity = WorldSetting.Current.Gravity;
        float confidence;
        float minimum;
        float expected;
        string source;
        if (parts.Avionics != null)
        {
            confidence = parts.Avionics.GetAutoLandConfidenceRatio(out minimum, out expected);
            source = "RocketAvionicsDevice.GetAutoLandConfidenceRatio (what the Rocket Control screen shows)";
        }
        else
        {
            confidence = rocket.GetAutoLandConfidenceRatio(deltaV, gravity, profileAltitude, out minimum);
            expected = rocket.GetMaxExpectedThrust();
            source = "Rocket.GetAutoLandConfidenceRatio";
        }

        return new LandingCheckView(confidence, FlightMath.ConfidenceBand(confidence),
            float.IsInfinity(minimum) ? null : minimum, expected, rocket.TotalMass(), profileAltitude, deltaV,
            FlightMath.ClampGravity(gravity), source);
    }

    // The re-entry check toward the target pad, else the rocket's home pad (Rocket.cs:2174-2181): the hop down is
    // twice the pad's connection distance (NodeTransit.GetDistance), gravity -1 at an orbital mount.
    private static LandingAtPadView? LandingAtPad(Rocket rocket, float profileAltitude)
    {
        SpaceMapNode? pad = SpaceRoutes.IsPad(rocket.TargetNode) ? rocket.TargetNode : null;
        if (pad == null)
        {
            try
            {
                pad = SpaceRoutes.ResolveFor("pad", rocket);
            }
            catch (ApiException)
            {
                return null;
            }
        }

        if (pad?.ParentConnection == null)
        {
            return null;
        }

        bool orbital = pad.Owner != null && pad.Owner.IsOrbital;
        float deltaV = pad.ParentConnection.Distance() * 2f;
        float gravity = FlightMath.ClampGravity(orbital ? -1f : WorldSetting.Current.Gravity);
        ConfidenceReading reading = FlightMath.Confidence(rocket.AutomatedLanding, deltaV, gravity, profileAltitude,
            rocket.GetMaxExpectedThrust(), rocket.TotalMass());
        return new LandingAtPadView(SpaceRoutes.NameOf(pad), orbital, reading.Ratio,
            FlightMath.ConfidenceBand(reading.Ratio),
            float.IsInfinity(reading.MinRequiredThrust) ? null : reading.MinRequiredThrust, rocket.GetMaxExpectedThrust(),
            rocket.TotalMass(), profileAltitude, deltaV, gravity);
    }

    private static List<CargoHoldView> Cargo(RocketParts parts)
    {
        List<CargoHoldView> holds = new List<CargoHoldView>(parts.Holds.Count);
        for (int index = 0; index < parts.Holds.Count; index++)
        {
            Assets.Scripts.Objects.Electrical.RocketChuteStorage hold = parts.Holds[index];
            holds.Add(new CargoHoldView(new ThingId(hold.ReferenceId), Names.Of(hold), RocketParts.FilledSlots(hold),
                RocketParts.Slots(hold), hold.MassContribution));
        }

        return holds;
    }

    private static RocketPowerView Power(RocketParts parts, bool compact)
    {
        double charge = 0.0;
        double capacity = 0.0;
        for (int index = 0; index < parts.Batteries.Count; index++)
        {
            charge += parts.Batteries[index].PowerStored;
            capacity += parts.Batteries[index].PowerMaximum;
        }

        double load = 0.0;
        double engines = 0.0;
        List<PowerDeviceView> devices = new List<PowerDeviceView>(parts.Loads.Count);
        for (int index = 0; index < parts.Loads.Count; index++)
        {
            PowerLoadRead device = parts.Loads[index];
            load += device.NowW;
            if (device.IsEngine)
            {
                engines += device.OnW;
            }

            devices.Add(new PowerDeviceView(new ThingId(device.Device.ReferenceId), Names.Of(device.Device),
                device.Device.OnOff, device.NowW, device.OnW, device.StaysOnAtArrival));
        }

        return new RocketPowerView(charge, capacity, load, engines, compact ? null : devices);
    }

    // The game's own estimate, and ours: the engines' feed laws run tick by tick on a copy until nothing is drawn.
    private static BurnTimeView BurnTime(Rocket rocket, RocketParts parts, RocketCraft craft)
    {
        double tick = Assets.Scripts.GameManager.GameTickSpeedSeconds;
        float throttle = RocketModel.FirstThrottle(parts);
        return new BurnTimeView(rocket.EstimatedRemainingBurnTimeSeconds, Pure.Rockets.BurnTime.Seconds(craft, 100f, tick),
            throttle > 0f ? Pure.Rockets.BurnTime.Seconds(craft, throttle, tick) : null);
    }

    // The kg RocketNetwork.CalculateGasMass counts for one fuel line: its pipe and the tanks it lists.
    private static double LineGasKg(FuelLineRead line)
    {
        double grams = line.Network.Atmosphere.GasMixture.TotalMassGassesAndLiquidsGrams();
        for (int index = 0; index < line.Tanks.Count; index++)
        {
            if (line.Tanks[index].Counted)
            {
                grams += line.Tanks[index].Atmosphere.GasMixture.TotalMassGassesAndLiquidsGrams();
            }
        }

        return grams / 1000.0;
    }

    private static double LineMoles(FuelLineRead line)
    {
        double total = line.Network.Atmosphere.TotalMoles.ToDouble();
        for (int index = 0; index < line.Tanks.Count; index++)
        {
            total += line.Tanks[index].Atmosphere.TotalMoles.ToDouble();
        }

        return total;
    }

    // Rocket.TotalMolesVolatiles + TotalMolesOxidizer's share of an atmosphere (Rocket.cs:2924-2966).
    private static double FuelShare(Atmosphere atmosphere)
    {
        GasMixture mixture = atmosphere.GasMixture;
        double total = mixture.GetTotalMolesGassesAndLiquids.ToDouble();
        return total > 0.0
            ? (mixture.TotalFuel.ToDouble() + mixture.TotalOxidiser.ToDouble() + mixture.TotalHypergolics.ToDouble()) /
              total
            : 0.0;
    }

    /// <summary>The port against the game's own methods, on the rocket as it is now.</summary>
    private static List<SelfCheckView> Checks(Rocket rocket, RocketParts parts, RocketCraft craft, float profileAltitude)
    {
        List<SelfCheckView> checks = new List<SelfCheckView>(6);
        float deltaV = SpaceMap.Current.DistanceToOrbit * 2f;
        float gravity = WorldSetting.Current.Gravity;
        float game = rocket.GetAutoLandConfidenceRatio(deltaV, gravity, profileAltitude, out float gameMinimum);
        ConfidenceReading ours = FlightMath.Confidence(rocket.AutomatedLanding, deltaV, gravity, profileAltitude,
            rocket.GetMaxExpectedThrust(), rocket.TotalMass());
        checks.Add(new SelfCheckView("confidence_ratio", game, ours.Ratio, 1e-6));
        checks.Add(new SelfCheckView("confidence_min_required_thrust_n", gameMinimum, ours.MinRequiredThrust, 0.01));

        float altitude = rocket.RocketState == RocketState.Landing ? rocket.GetAltitude() : profileAltitude;
        float velocity = rocket.RocketState == RocketState.Landing ? rocket.Velocity : -deltaV;
        float acceleration = rocket.RocketState == RocketState.Landing ? rocket.Acceleration : 1.5f;
        checks.Add(new SelfCheckView("apex_m", Rocket.GetApex(altitude, velocity, acceleration),
            FlightMath.Apex(altitude, velocity, acceleration), 1e-3));
        checks.Add(new SelfCheckView("max_expected_thrust_n", rocket.GetMaxExpectedThrust(), craft.MaxExpectedThrust,
            0.01));
        for (int index = 0; index < parts.Lines.Count && index < craft.Lines.Count; index++)
        {
            double measured = LineGasKg(parts.Lines[index]);
            checks.Add(new SelfCheckView($"fuel_line_gas_kg (network {parts.Lines[index].Network.ReferenceId})",
                measured, craft.Lines[index].CountedMassKg, 0.5 + measured * 0.01));
        }

        // A Pumped Gas Engine's last draw is one make-up from one input: the probe can replay it exactly.
        for (int index = 0; index < parts.EngineReads.Count; index++)
        {
            EngineRead read = parts.EngineReads[index];
            RocketEngineBase engine = read.Engine;
            double passed = engine.PassedMoles.ToDouble();
            if (read.Feed is PumpedGasFeed && read.Input1.HasValue && engine.Force > 0f && passed > 0.0)
            {
                double probe = ThrustProbe.Burn(engine, FuelSample.Of(parts.Lines[read.Input1.Value].Network.Atmosphere),
                    passed).ForceN;
                checks.Add(new SelfCheckView($"thrust_n_at_{passed:0.##}_mol (engine {engine.ReferenceId})",
                    engine.Force, probe, engine.Force * 0.01));
            }
        }

        if (rocket.EstimatedRemainingBurnTimeSeconds > 0f)
        {
            double fuel = 0.0;
            double drawn = 0.0;
            for (int index = 0; index < parts.Lines.Count; index++)
            {
                fuel += LineMoles(parts.Lines[index]) * FuelShare(parts.Lines[index].Network.Atmosphere);
            }

            for (int index = 0; index < parts.Engines.Count; index++)
            {
                drawn += parts.Engines[index].PassedMoles.ToDouble();
            }

            if (drawn > 0.0)
            {
                double estimate = Math.Floor(fuel / drawn / 2.0);
                checks.Add(new SelfCheckView("burn_time_s", rocket.EstimatedRemainingBurnTimeSeconds, estimate,
                    Math.Max(2.0, estimate * 0.05)));
            }
        }

        return checks;
    }

    // Every hull piece and internal part with its offset and turn in the engine mount's frame (the anchor RocketNetwork
    // measures RocketData offsets from, RocketNetwork.cs:350-363), and what sits in its slots.
    private static List<PartPoseView> Poses(RocketParts parts)
    {
        List<PartPoseView> poses = new List<PartPoseView>(parts.Network.StructureList.Count + parts.Network.Internals.Count);
        Thing? anchor = parts.Network.Anchor;
        Vector3 origin = anchor != null ? anchor.ThingTransformPosition : Vector3.zero;
        Quaternion turn = anchor != null ? Quaternion.Inverse(anchor.ThingTransformRotation) : Quaternion.identity;
        for (int index = 0; index < parts.Network.StructureList.Count; index++)
        {
            if (parts.Network.StructureList[index] is Thing piece && !piece.IsBeingDestroyed)
            {
                poses.Add(Pose(piece, "hull", origin, turn));
            }
        }

        for (int index = 0; index < parts.Network.Internals.Count; index++)
        {
            if (parts.Network.Internals[index] is Thing part && !part.IsBeingDestroyed)
            {
                poses.Add(Pose(part, "internal", origin, turn));
            }
        }

        return poses;
    }

    private static PartPoseView Pose(Thing thing, string role, Vector3 origin, Quaternion turn)
    {
        Vector3 offset = turn * (thing.ThingTransformPosition - origin);
        List<string>? contents = null;
        if (thing.Slots != null)
        {
            for (int index = 0; index < thing.Slots.Count; index++)
            {
                DynamicThing? item = thing.Slots[index]?.Get();
                if (item != null)
                {
                    contents ??= new List<string>(2);
                    contents.Add(item.PrefabName);
                }
            }
        }

        return new PartPoseView(new ThingId(thing.ReferenceId), thing.PrefabName, Labels.CustomNameOf(thing), role,
            offset.x, offset.y, offset.z, Orientations.Of(turn * thing.ThingTransformRotation), contents);
    }

    private static void Notes(Rocket rocket, RocketParts parts, List<string> notes)
    {
        for (int index = 0; index < parts.Unmodelled.Count; index++)
        {
            notes.Add($"{Names.Of(parts.Unmodelled[index])} is a {parts.Unmodelled[index].GetType().Name}, none of the " +
                      "game's six engine classes: rocket_forecast refuses it.");
        }

        for (int index = 0; index < parts.EngineReads.Count; index++)
        {
            if (parts.EngineReads[index].MissingInput != null)
            {
                notes.Add($"{Names.Of(parts.EngineReads[index].Engine)}: {parts.EngineReads[index].MissingInput}; the " +
                          "game keeps it inoperable (no thrust).");
            }
        }

        bool parked = rocket.RocketState == RocketState.InSpace && rocket.Progress == 0f;
        for (int index = 0; index < parts.Engines.Count; index++)
        {
            if (parked && parts.Engines[index].OnOff && parts.Engines[index].Powered)
            {
                notes.Add("An engine is on while parked: it burns fuel all the same (RocketEngineBase.cs:450-454).");
                break;
            }
        }

        if (!rocket.AutomatedLanding)
        {
            notes.Add("Automated landing is off: no confidence check, no autopilot; the engines are switched off and " +
                      "the rocket falls (Rocket.cs:579-595).");
        }

        if (rocket.TargetNode != null && !rocket.TargetNode.IsCharted)
        {
            notes.Add("The target is uncharted.");
        }

        if (rocket.IsManned)
        {
            notes.Add("Manned: only launch mounts (ground or orbital) can be targeted (Rocket.cs:1871-1885).");
        }
    }
}

/// <summary>The forecast's what-ifs left empty: the rocket as it is.</summary>
internal static class RocketWhatIfNone
{
    internal static RocketWhatIf Value => RocketWhatIf.From(new Args(null));
}
