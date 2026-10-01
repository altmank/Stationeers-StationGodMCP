#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.Rockets;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Flight;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Rockets;

namespace StationGodMCP.Api;

/// <summary>
/// rocket_status: where each rocket is and goes, what it weighs, its fuel, engines, thrust, the auto-land confidence the
/// avionics shows, cargo, power and burn time, all read from the game; plus checks of the forecast's port against the
/// game's own numbers. Read only: every game method called here (GetAutoLandConfidenceRatio, GetApex,
/// GetMaxExpectedThrust, TotalMass, GetThrust, SpaceMapPathFinder) only computes.
/// </summary>
internal static class RocketStatusApi
{
    internal static RocketStatusListView Handle(Args args)
    {
        ThingId? id = args.OptionalThingId("rocket_id");
        bool selfTest = args.OptionalBool("self_test") ?? true;
        List<Rocket> rockets = id.HasValue ? new List<Rocket> { RocketLocator.Find(id.Value) } : RocketLocator.All();
        List<RocketStatusView> views = new List<RocketStatusView>(rockets.Count);
        for (int index = 0; index < rockets.Count; index++)
        {
            views.Add(Describe(rockets[index], selfTest));
        }

        return new RocketStatusListView(views);
    }

    internal static RocketStatusView Describe(Rocket rocket, bool selfTest)
    {
        RocketParts parts = RocketParts.Of(rocket);
        List<string> notes = new List<string>(4);
        float profileAltitude = RoutePlanner.AltitudeOf(rocket.ReEntryProfile);
        RocketStatusView view = new RocketStatusView(new ThingId(rocket.ReferenceId),
            new ThingId(rocket.RocketNetwork.ReferenceId), rocket.DisplayName, rocket.AutomatedLanding,
            rocket.AutomatedShutOff, rocket.ReEntryProfile.ToString(), profileAltitude, Where(rocket, notes),
            Mass(rocket, parts), Fuel(parts), Engines(parts), Thrust(rocket, parts, notes),
            LandingCheck(rocket, parts, profileAltitude), Cargo(parts), Power(parts), BurnTime(rocket, parts),
            selfTest ? Checks(rocket, parts, profileAltitude) : new List<SelfCheckView>(), notes);
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

    private static RocketMassView Mass(Rocket rocket, RocketParts parts) =>
        new RocketMassView(rocket.TotalMass(), parts.Network.DryMass, parts.Network.GasMass, parts.CargoSlotsFilled,
            parts.MassParts);

    private static List<FuelLineView> Fuel(RocketParts parts)
    {
        List<FuelLineView> lines = new List<FuelLineView>(parts.Lines.Count);
        for (int index = 0; index < parts.Lines.Count; index++)
        {
            FuelLineRead line = parts.Lines[index];
            Atmosphere pipe = line.Network.Atmosphere;
            double total = pipe.TotalMoles.ToDouble();
            List<FuelTankView> tanks = new List<FuelTankView>(line.Tanks.Count);
            for (int tank = 0; tank < line.Tanks.Count; tank++)
            {
                FuelTankRead read = line.Tanks[tank];
                double moles = read.Atmosphere.TotalMoles.ToDouble();
                total += moles;
                tanks.Add(new FuelTankView(new ThingId(read.Owner.ReferenceId), Names.Of(read.Owner),
                    read.Atmosphere.Volume.ToDouble(), moles, read.Atmosphere.Temperature.ToDouble(),
                    read.Atmosphere.PressureGassesAndLiquids.ToDouble(), read.Counted));
            }

            lines.Add(new FuelLineView(new ThingId(line.Network.ReferenceId), line.Engines.Count,
                pipe.Volume.ToDouble(), pipe.TotalMoles.ToDouble(), total, pipe.Temperature.ToDouble(), Mix(pipe),
                tanks));
        }

        return lines;
    }

    private static Dictionary<string, double> Mix(Atmosphere atmosphere)
    {
        FuelSample sample = FuelSample.Of(atmosphere);
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
        List<RocketEngineView> engines = new List<RocketEngineView>(parts.Engines.Count);
        for (int index = 0; index < parts.Engines.Count; index++)
        {
            RocketEngineBase engine = parts.Engines[index];
            object? input = GameMembers.EngineInputNetwork1.GetValue(engine);
            engines.Add(new RocketEngineView(new ThingId(engine.ReferenceId), Names.Of(engine),
                engine.GetType().Name, engine.OnOff, engine.Powered, engine.Throttle, engine.Force,
                engine.PassedMoles.ToDouble(), engine.ExhaustVelocity, engine.ExhaustTemperature.ToDouble(),
                engine.MaxThrust, engine.UsedPower,
                input is Assets.Scripts.Networks.PipeNetwork network ? new ThingId(network.ReferenceId) : null,
                engine is GovernedGasEngine));
        }

        return engines;
    }

    /// <summary>Full-throttle thrust of every fuel line's engines on the fuel as it is, by the game's combustion on a copy.</summary>
    internal static double? AchievableFullThrust(RocketParts parts)
    {
        if (parts.Lines.Count == 0)
        {
            return null;
        }

        double total = 0.0;
        for (int index = 0; index < parts.Lines.Count; index++)
        {
            FuelLineRead line = parts.Lines[index];
            FuelSample sample = FuelSample.Of(line.Network.Atmosphere);
            total += ThrustProbe.Burn(line.Engines[0], sample, FuelLine.MaxMolesPerTick).ForceN * line.Engines.Count;
        }

        return total;
    }

    private static RocketThrustView Thrust(Rocket rocket, RocketParts parts, List<string> notes)
    {
        float prefab = rocket.RocketNetwork.Engines.Count > 0 ? rocket.RocketNetwork.Engines[0].MaxThrust : 0f;
        double? achievable = AchievableFullThrust(parts);
        RocketThrustView view = new RocketThrustView(rocket.GetThrust(), rocket.MaxRecordedThrust, prefab,
            rocket.GetMaxExpectedThrust(), achievable,
            "MaxRecordedThrust = the highest Rocket.GetThrust (all engines' Force) seen on any physics step since the " +
            "rocket was built, saved with the game, reset to 0 only while the rocket has no engine (Rocket.cs:1982-1989, " +
            "844, 885). The landing check uses max(MaxRecordedThrust, first engine's prefab MaxThrust at 215 K) " +
            "(GetMaxExpectedThrust, Rocket.cs:2710-2721), never the thrust the fuel gives now.");
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

    private static List<CargoHoldView> Cargo(RocketParts parts)
    {
        List<CargoHoldView> holds = new List<CargoHoldView>(parts.Holds.Count);
        for (int index = 0; index < parts.Holds.Count; index++)
        {
            RocketChuteStorage hold = parts.Holds[index];
            holds.Add(new CargoHoldView(new ThingId(hold.ReferenceId), Names.Of(hold), RocketParts.FilledSlots(hold),
                RocketParts.Slots(hold), hold.MassContribution));
        }

        return holds;
    }

    private static RocketPowerView Power(RocketParts parts)
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

        return new RocketPowerView(charge, capacity, load, engines, devices);
    }

    private static BurnTimeView BurnTime(Rocket rocket, RocketParts parts)
    {
        double fuel = 0.0;
        double full = 0.0;
        for (int index = 0; index < parts.Lines.Count; index++)
        {
            FuelLineRead line = parts.Lines[index];
            fuel += LineMoles(line) * FuelShare(line.Network.Atmosphere);
            full += line.Engines.Count * FuelLine.MaxMolesPerTick;
        }

        float throttle = RocketModel.FirstThrottle(parts);
        double? fullSeconds = full > 0.0 ? fuel / full * Assets.Scripts.GameManager.GameTickSpeedSeconds : null;
        double? nowSeconds = full > 0.0 && throttle > 0f
            ? fuel / (full * throttle / 100.0) * Assets.Scripts.GameManager.GameTickSpeedSeconds
            : null;
        return new BurnTimeView(rocket.EstimatedRemainingBurnTimeSeconds, fullSeconds, nowSeconds);
    }

    // The kg RocketNetwork.CalculateGasMass counts for one fuel line: its pipe, the tanks it lists, the engines' chambers.
    private static double LineGasKg(FuelLineRead line, Networks.RocketNetwork network)
    {
        double grams = line.Network.Atmosphere.GasMixture.TotalMassGassesAndLiquidsGrams();
        for (int index = 0; index < line.Tanks.Count; index++)
        {
            if (line.Tanks[index].Counted)
            {
                grams += line.Tanks[index].Atmosphere.GasMixture.TotalMassGassesAndLiquidsGrams();
            }
        }

        for (int index = 0; index < line.Engines.Count; index++)
        {
            Atmosphere? chamber = line.Engines[index].InternalAtmosphere;
            if (chamber != null && network.RocketAtmospheres.Contains(chamber))
            {
                grams += chamber.GasMixture.TotalMassGassesAndLiquidsGrams();
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
    private static List<SelfCheckView> Checks(Rocket rocket, RocketParts parts, float profileAltitude)
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

        RocketCraft craft = RocketModel.Craft(parts, RocketWhatIfNone.Value);
        checks.Add(new SelfCheckView("max_expected_thrust_n", rocket.GetMaxExpectedThrust(), craft.MaxExpectedThrust,
            0.01));
        for (int index = 0; index < parts.Lines.Count && index < craft.Lines.Count; index++)
        {
            double measured = LineGasKg(parts.Lines[index], parts.Network);
            checks.Add(new SelfCheckView($"fuel_line_gas_kg (network {parts.Lines[index].Network.ReferenceId})",
                measured, craft.Lines[index].CountedMassKg, 0.5 + measured * 0.01));
        }

        for (int index = 0; index < parts.Lines.Count; index++)
        {
            FuelLineRead line = parts.Lines[index];
            RocketEngineBase engine = line.Engines[0];
            double passed = engine.PassedMoles.ToDouble();
            if (engine.Force > 0f && passed > 0.0)
            {
                double probe = ThrustProbe.Burn(engine, FuelSample.Of(line.Network.Atmosphere), passed).ForceN;
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
                for (int engine = 0; engine < parts.Lines[index].Engines.Count; engine++)
                {
                    drawn += parts.Lines[index].Engines[engine].PassedMoles.ToDouble();
                }
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

    private static void Notes(Rocket rocket, RocketParts parts, List<string> notes)
    {
        for (int index = 0; index < parts.Unmodelled.Count; index++)
        {
            notes.Add($"{Names.Of(parts.Unmodelled[index])} is a {parts.Unmodelled[index].GetType().Name}: " +
                      "rocket_forecast flies only the Pumped Gas Engine (GovernedGasEngine).");
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
    }
}

/// <summary>The forecast's what-ifs left empty: the rocket as it is.</summary>
internal static class RocketWhatIfNone
{
    internal static RocketWhatIf Value => RocketWhatIf.From(new Args(null));
}
