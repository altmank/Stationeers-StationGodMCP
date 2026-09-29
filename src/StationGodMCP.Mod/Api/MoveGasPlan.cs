#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>An atmosphere's gas: quantity and energy per single gas type, read with the game's Mole getters.</summary>
internal sealed class GasSnapshot
{
    // Atmosphere.LiquidPressureOffset: the pressure reported for an atmosphere completely full of liquid, and the
    // kPa scale of the offset below that.
    private const double FullOfLiquidKpa = 1013249.9694824219;
    private const double LiquidOffsetScaleKpa = 10.0;

    // AtmosphericsNetwork.MinFrozenMolesToDamage (moles per litre of network volume) and the liquid share above which
    // Atmosphere.IsAboveArmstrong counts a network atmosphere as pressurised.
    private const double FrozenMolesPerLitre = 0.05000000074505806;
    private const double NetworkLiquidRatioFloor = 0.0001;

    private readonly double[] _moles;
    private readonly double[] _energies;

    private GasSnapshot(double[] moles, double[] energies, double volumeL, AtmosphereHelper.AtmosphereMode mode)
    {
        _moles = moles;
        _energies = energies;
        VolumeL = volumeL;
        Mode = mode;
    }

    internal double VolumeL { get; }

    internal AtmosphereHelper.AtmosphereMode Mode { get; }

    // On the main thread the Mole getters return last tick's cached values; on the atmospherics thread, live ones.
    internal static GasSnapshot Of(Atmosphere atmosphere)
    {
        int count = GasTypes.All.Length;
        double[] moles = new double[count];
        double[] energies = new double[count];
        for (int index = 0; index < count; index++)
        {
            Mole mole = atmosphere.GasMixture.GetMoleValue(GasTypes.All[index]);
            moles[index] = mole.Quantity.ToDouble();
            energies[index] = mole.Energy.ToDouble();
        }

        return new GasSnapshot(moles, energies, atmosphere.Volume.ToDouble(), atmosphere.Mode);
    }

    internal double MolesOf(int index) => _moles[index];

    /// <summary>
    /// Several atmospheres as one: moles, energies and volumes added, as AtmosphereHelper.Mix pools (and
    /// Room.CacheRoomData, for a room's cells). The pool keeps its parts' mode when they share one (a room's World
    /// cells), else counts as a network.
    /// </summary>
    internal static GasSnapshot Pool(GasSnapshot[] parts)
    {
        if (parts.Length == 1)
        {
            return parts[0];
        }

        int count = GasTypes.All.Length;
        double[] moles = new double[count];
        double[] energies = new double[count];
        double volume = 0.0;
        AtmosphereHelper.AtmosphereMode mode =
            parts.Length > 0 ? parts[0].Mode : AtmosphereHelper.AtmosphereMode.Network;
        foreach (GasSnapshot part in parts)
        {
            if (part.Mode != mode)
            {
                mode = AtmosphereHelper.AtmosphereMode.Network;
            }

            for (int index = 0; index < count; index++)
            {
                moles[index] += part._moles[index];
                energies[index] += part._energies[index];
            }

            volume += part.VolumeL;
        }

        return new GasSnapshot(moles, energies, volume, mode);
    }

    /// <summary>What Mole.Remove would take: the amount capped at what is there, and its share of energy.</summary>
    internal (double moles, double energy) Take(int index, double? amount)
    {
        double have = _moles[index];
        double moles = amount.HasValue ? Math.Min(have, amount.Value) : have;
        double energy = have > 0.0 ? _energies[index] * (moles / have) : 0.0;
        return (moles, energy);
    }

    internal GasSnapshot With(int index, double moles, double energy)
    {
        double[] newMoles = (double[])_moles.Clone();
        double[] newEnergies = (double[])_energies.Clone();
        newMoles[index] = Math.Max(0.0, newMoles[index] + moles);
        newEnergies[index] = Math.Max(0.0, newEnergies[index] + energy);
        return new GasSnapshot(newMoles, newEnergies, VolumeL, Mode);
    }

    // Atmosphere.PressureGassesAndLiquids and Atmosphere.LiquidPressureOffset on this snapshot, with the temperature
    // from GasMixture.Temperature of the same moles.
    internal double PressureKpa()
    {
        GasMixture mixture = ToMixture();
        double liquidVolume = mixture.VolumeLiquids.ToDouble();
        double minimumGas = Atmosphere.GetMinimumGasVolume(Mode).ToDouble();
        double pressure = GasPressureKpa(mixture, liquidVolume);
        bool liquidMode = Mode == AtmosphereHelper.AtmosphereMode.Thing ||
                          Mode == AtmosphereHelper.AtmosphereMode.Network ||
                          Mode == AtmosphereHelper.AtmosphereMode.None;
        if (liquidMode && liquidVolume > VolumeL - minimumGas)
        {
            pressure = Math.Max(pressure, LiquidOffsetKpa(mixture, liquidVolume));
        }

        return pressure;
    }

    // Atmosphere.PressureGasses: the gas moles alone at the mixture's temperature in Atmosphere.GetGasVolume.
    private double GasPressureKpa(GasMixture mixture, double liquidVolume)
    {
        double minimumGas = Atmosphere.GetMinimumGasVolume(Mode).ToDouble();
        double gasVolume = Math.Max(VolumeL - liquidVolume, minimumGas);
        return IdealGas.Pressure(mixture.GetTotalMolesGasses, mixture.Temperature, new VolumeLitres(gasVolume))
            .ToDouble();
    }

    /// <summary>Atmosphere.LiquidVolumeRatio: the share of the volume the liquids fill.</summary>
    internal double LiquidVolumeRatio() => VolumeL > 0.0 ? ToMixture().VolumeLiquids.ToDouble() / VolumeL : 0.0;

    /// <summary>AtmosphericsNetwork.MinFrozenMolesToDamage: the frozen moles a network tolerates.</summary>
    internal double FrozenLimitMol() => FrozenMolesPerLitre * VolumeL;

    /// <summary>
    /// The moles AtmosphericsNetwork.EvaluateIncorrectMatterState would find frozen, with the mixture settled to one
    /// temperature (GasMixture.EqualiseInternalEnergy): GasMixture.CheckForFreezing at the gas pressure, counted only
    /// while the network's gate is open (Atmosphere.IsAboveArmstrong, or liquids over FrozenLimitMol).
    /// </summary>
    internal double FrozenMol()
    {
        GasMixture mixture = ToMixture();
        mixture.EqualiseInternalEnergy();
        double liquidVolume = mixture.VolumeLiquids.ToDouble();
        double liquidRatio = VolumeL > 0.0 ? liquidVolume / VolumeL : 0.0;
        bool network = Mode == AtmosphereHelper.AtmosphereMode.Network;
        bool aboveArmstrong = PressureKpa() > Chemistry.ArmstrongLimit.ToDouble() ||
                              (network && liquidRatio > NetworkLiquidRatioFloor);
        if (!aboveArmstrong && !(mixture.GetTotalMolesLiquids.ToDouble() > FrozenLimitMol()))
        {
            return 0.0;
        }

        return mixture.CheckForFreezing(new PressurekPa(GasPressureKpa(mixture, liquidVolume)))
            .GetTotalMolesGassesAndLiquids.ToDouble();
    }

    /// <summary>
    /// The moles a room's cells would freeze out of their air, settled to one temperature: GasMixture.CheckForFreezing
    /// at the gas pressure, with no tolerance (Atmosphere.StateChange in World mode takes any amount).
    /// </summary>
    internal double FrozenInAirMol()
    {
        GasMixture mixture = ToMixture();
        mixture.EqualiseInternalEnergy();
        return mixture.CheckForFreezing(new PressurekPa(GasPressureKpa(mixture, mixture.VolumeLiquids.ToDouble())))
            .GetTotalMolesGassesAndLiquids.ToDouble();
    }

    /// <summary>Atmosphere.PressureGasses: the gas moles alone, the pressure a liquid's state change uses.</summary>
    internal double GasOnlyPressureKpa()
    {
        GasMixture mixture = ToMixture();
        return GasPressureKpa(mixture, mixture.VolumeLiquids.ToDouble());
    }

    /// <summary>
    /// These contents once every liquid that can evaporate has boiled into its gas, as Mole.StateChangeLiquid turns
    /// it (MoleHelper.EvaporationType; the energy scaled by MoleHelper.EvaporationRatio), each mole paying its latent
    /// heat of vaporisation out of the pooled energy, settled to one temperature. Null when nothing would boil, or
    /// when a boiled liquid would end below its evaporation temperature at the resulting gas pressure: then it does
    /// not all boil, and its vapour stays at the game's evaporation pressure instead.
    /// </summary>
    internal GasSnapshot? Boiled()
    {
        double[] moles = (double[])_moles.Clone();
        double energy = EnergyJ();
        List<Chemistry.GasType> boiled = new List<Chemistry.GasType>();
        double minimum = Chemistry.MINIMUM_QUANTITY_MOLES.ToDouble();
        for (int index = 0; index < _moles.Length; index++)
        {
            Chemistry.GasType liquid = GasTypes.All[index];
            int gas = BoilsInto(liquid);
            if (_moles[index] <= minimum || gas < 0)
            {
                continue;
            }

            Mole mole = new Mole(liquid, new MoleQuantity(_moles[index]), new MoleEnergy(_energies[index]));
            energy -= _moles[index] * mole.LatentHeatOfVaporization();
            energy -= _energies[index] * (1.0 - MoleHelper.EvaporationRatio(liquid));
            moles[gas] += _moles[index];
            moles[index] = 0.0;
            boiled.Add(liquid);
        }

        if (boiled.Count == 0 || !(energy > 0.0))
        {
            return null;
        }

        GasSnapshot result = Settled(moles, energy);
        GasMixture mixture = result.ToMixture();
        double temperature = mixture.Temperature.ToDouble();
        PressurekPa gasPressure = new PressurekPa(result.GasPressureKpa(mixture, mixture.VolumeLiquids.ToDouble()));
        foreach (Chemistry.GasType liquid in boiled)
        {
            if (temperature < MoleHelper.EvaporationTemperature(liquid, gasPressure).ToDouble())
            {
                return null;
            }
        }

        return result;
    }

    private static int BoilsInto(Chemistry.GasType type) =>
        Mole.MatterState(type) == AtmosphereHelper.MatterState.Liquid && MoleHelper.CanEvaporate(type)
            ? GasTypes.IndexOf(MoleHelper.EvaporationType(type))
            : -1;

    // GasMixture.TotalEnergy's setter hands each gas its heat capacity's share: one temperature.
    private GasSnapshot Settled(double[] moles, double energy)
    {
        GasMixture mixture = GasMixtureHelper.Create();
        for (int index = 0; index < moles.Length; index++)
        {
            if (moles[index] > 0.0)
            {
                mixture.Add(new Mole(GasTypes.All[index], new MoleQuantity(moles[index]), new MoleEnergy(1.0)));
            }
        }

        mixture.TotalEnergy = new MoleEnergy(energy);
        double[] energies = new double[moles.Length];
        for (int index = 0; index < moles.Length; index++)
        {
            energies[index] = moles[index] > 0.0
                ? mixture.GetMoleValue(GasTypes.All[index]).Energy.ToDouble()
                : 0.0;
        }

        return new GasSnapshot(moles, energies, VolumeL, Mode);
    }

    private double LiquidOffsetKpa(GasMixture mixture, double liquidVolume)
    {
        if (mixture.GetTotalMolesLiquids < Chemistry.MINIMUM_QUANTITY_MOLES || !(VolumeL > 0.0))
        {
            return 0.0;
        }

        double ratio = Math.Min(1.0, Math.Max(0.0, liquidVolume / VolumeL));
        return ratio >= 1.0 ? FullOfLiquidKpa : LiquidOffsetScaleKpa / (1.0 - ratio) - LiquidOffsetScaleKpa;
    }

    internal double TemperatureK() => ToMixture().Temperature.ToDouble();

    internal double TotalMol()
    {
        double total = 0.0;
        for (int index = 0; index < _moles.Length; index++)
        {
            total += _moles[index];
        }

        return total;
    }

    internal double EnergyJ()
    {
        double total = 0.0;
        for (int index = 0; index < _energies.Length; index++)
        {
            total += _energies[index];
        }

        return total;
    }

    /// <summary>The same contents in another volume (a pipe network after its pieces change).</summary>
    internal GasSnapshot WithVolume(double volumeL) => new GasSnapshot(_moles, _energies, volumeL, Mode);

    // A fresh GasMixture is not cachable, so its getters compute from these moles on any thread.
    private GasMixture ToMixture()
    {
        GasMixture mixture = GasMixtureHelper.Create();
        for (int index = 0; index < _moles.Length; index++)
        {
            if (_moles[index] > 0.0)
            {
                mixture.Add(new Mole(GasTypes.All[index], new MoleQuantity(_moles[index]),
                    new MoleEnergy(_energies[index])));
            }
        }

        return mixture;
    }

    internal AtmosphereView ToView()
    {
        double minimum = Chemistry.MINIMUM_QUANTITY_MOLES.ToDouble();
        List<GasAmountView> gases = new List<GasAmountView>();
        for (int index = 0; index < _moles.Length; index++)
        {
            if (_moles[index] > minimum)
            {
                gases.Add(new GasAmountView(GasTypes.All[index].ToString(), _moles[index]));
            }
        }

        return new AtmosphereView(PressureKpa(), TemperatureK(), TotalMol(), gases);
    }
}

/// <summary>
/// One side of a move: the atmospheres treated as one unit, with a snapshot of each before and after. For the source
/// the unit differs by matter state (a PortablesConnector joins gases to one network and liquids to another), so a
/// side keeps a set per state and reports their union.
/// </summary>
internal sealed class GasSide
{
    private GasSide(GasPlace place, JoinedSet gasSet, JoinedSet liquidSet)
    {
        Place = place;
        GasSet = gasSet;
        LiquidSet = liquidSet;
        All = gasSet.Union(liquidSet);
        Before = new GasSnapshot[All.Members.Count];
        After = new GasSnapshot[All.Members.Count];
        for (int index = 0; index < All.Members.Count; index++)
        {
            Before[index] = GasSnapshot.Of(All.Members[index].Atmosphere);
            After[index] = Before[index];
        }
    }

    /// <summary>What was named: one atmosphere, or a room.</summary>
    internal GasPlace Place { get; }

    internal JoinedSet GasSet { get; }

    internal JoinedSet LiquidSet { get; }

    internal JoinedSet All { get; }

    internal GasSnapshot[] Before { get; }

    internal GasSnapshot[] After { get; }

    internal static GasSide Of(GasPlace place, GasEnd end, bool joined)
    {
        if (!joined)
        {
            JoinedSet alone = JoinedSet.Alone(end);
            return new GasSide(place, alone, alone);
        }

        return new GasSide(place,
            JoinedSet.Collect(end, AtmosphereHelper.MatterState.Gas),
            JoinedSet.Collect(end, AtmosphereHelper.MatterState.Liquid));
    }

    /// <summary>A room's cells: every gas and liquid is shared between all of them.</summary>
    internal static GasSide OfCells(GasPlace place, List<GasEnd> cells)
    {
        JoinedSet all = JoinedSet.OfCells(cells);
        return new GasSide(place, all, all);
    }

    /// <summary>The predicted arrival of a gas, where the named place puts it.</summary>
    internal void Receive(int gasIndex, double moles, double energy) => Place.Receive(this, gasIndex, moles, energy);

    /// <summary>The arrival itself, on the atmospherics thread.</summary>
    internal void Deliver(Chemistry.GasType gas, double moles, double energy) =>
        Place.Deliver(this, gas, moles, energy);

    /// <summary>Whether the move can no longer be applied here: an atmosphere was destroyed.</summary>
    internal bool AnyGone() => Place.IsGone(this);

    /// <summary>The members a gas of this type is shared between.</summary>
    internal JoinedSet For(Chemistry.GasType gas) =>
        Mole.MatterState(gas) == AtmosphereHelper.MatterState.Liquid ? LiquidSet : GasSet;

    internal int IndexOf(Atmosphere atmosphere) => All.IndexOf(atmosphere);

    internal void Change(Atmosphere atmosphere, int gasIndex, double moles, double energy)
    {
        int member = IndexOf(atmosphere);
        After[member] = After[member].With(gasIndex, moles, energy);
    }

    /// <summary>
    /// The pressure every member would share once the game has mixed them: the pooled moles and energy in the
    /// pooled volume. A lone member is just its own pressure.
    /// </summary>
    internal double SettledPressureKpa() => GasSnapshot.Pool(After).PressureKpa();

    internal void RefuseIfBursting(string name)
    {
        RefuseIfOverPressure(name, SettledPressureKpa(), "would settle at");
    }

    /// <summary>
    /// The receiving side's matter rules, each refused only when the move makes it worse: the pressure once its
    /// liquids have boiled, liquid in a gas pipe network, and gas or liquid freezing in a network.
    /// </summary>
    internal void RefuseIfReceivingBursts(string name)
    {
        GasSnapshot? boiled = AfterBoiling(After);
        if (boiled != null)
        {
            double pressure = boiled.PressureKpa();
            GasSnapshot? boiledBefore = AfterBoiling(Before);
            double pressureBefore = (boiledBefore ?? GasSnapshot.Pool(Before)).PressureKpa();
            if (pressure > pressureBefore)
            {
                RefuseIfOverPressure(name, pressure,
                    $"once its liquids boil (at {boiled.TemperatureK():0.#} K) would reach");
            }
        }

        RefuseIfLiquidInGasNetwork(name);
        RefuseIfFreezing(name);
        RefuseIfLostInRoom(name);
    }

    private void RefuseIfOverPressure(string name, double predicted, string verb)
    {
        foreach (GasEnd member in All.Members)
        {
            PressureRating? broken = member.FirstBroken(predicted);
            if (broken != null)
            {
                throw ApiErrors.Refused("would_burst",
                    $"'{name}' {verb} {predicted:0.#} kPa against a limit of " +
                    $"{broken.LimitFor(predicted):0.#} kPa ({broken.Member}: {broken.MaxKpa:0.#} kPa across, " +
                    $"{broken.OutsideKpa:0.#} kPa outside). Pass force to move it anyway.");
            }
        }
    }

    // AtmosphericsNetwork.EvaluateIncorrectMatterState: a gas pipe network whose liquids fill more than
    // GameConstants.MAX_ALLOWED_RATIO_VOLUME_LIQUIDS_GAS_PIPE of its volume damages its weakest member every tick.
    // The liquid spreads over the members joined for liquids, each taking its volume's share.
    private void RefuseIfLiquidInGasNetwork(string name)
    {
        GasEnd? gasNetwork = LiquidSet.Members.Find(member =>
            member.Network != null && member.Network.NetworkContentType == Pipe.ContentType.Gas);
        if (gasNetwork == null)
        {
            return;
        }

        double ratio = Pooled(LiquidSet, After).LiquidVolumeRatio();
        double limit = GameConstants.MAX_ALLOWED_RATIO_VOLUME_LIQUIDS_GAS_PIPE;
        if (ratio > limit && ratio > Pooled(LiquidSet, Before).LiquidVolumeRatio())
        {
            throw ApiErrors.Refused("would_burst",
                $"'{name}': liquid would fill {ratio * 100.0:0.##}% of gas pipe network " +
                $"{gasNetwork.Network!.ReferenceId}, over the {limit * 100.0:0.#}% a gas pipe holds without " +
                "bursting. " +
                "Move it into a liquid network or tank, or pass force to move it anyway.");
        }
    }

    // AtmosphericsNetwork.EvaluateIncorrectMatterState: frozen moles over MinFrozenMolesToDamage damage a network.
    private void RefuseIfFreezing(string name)
    {
        if (!All.Members.Exists(member => member.Network != null))
        {
            return;
        }

        GasSnapshot after = GasSnapshot.Pool(After);
        double frozen = after.FrozenMol();
        double limit = after.FrozenLimitMol();
        if (frozen > limit && frozen > GasSnapshot.Pool(Before).FrozenMol())
        {
            throw ApiErrors.Refused("would_burst",
                $"'{name}': {frozen:0.#} mol would freeze at {after.TemperatureK():0.#} K, over the " +
                $"{limit:0.#} mol a network of {after.VolumeL:0} L tolerates. Pass force to move it anyway.");
        }
    }

    // Atmosphere.StateChange in World mode: a room's cells freeze out any amount, and a liquid under its minimum
    // liquid pressure evaporates until it freezes unless the room can boil it all (Pure/RoomLiquids).
    private void RefuseIfLostInRoom(string name)
    {
        if (All.Members.Count == 0 ||
            !All.Members.TrueForAll(member => member.Atmosphere.Mode == AtmosphereHelper.AtmosphereMode.World))
        {
            return;
        }

        GasSnapshot before = GasSnapshot.Pool(Before);
        GasSnapshot after = GasSnapshot.Pool(After);
        double minimum = Chemistry.MINIMUM_QUANTITY_MOLES.ToDouble();
        List<LiquidArrival> arrivals = new List<LiquidArrival>();
        for (int index = 0; index < GasTypes.All.Length; index++)
        {
            Chemistry.GasType type = GasTypes.All[index];
            double added = after.MolesOf(index) - before.MolesOf(index);
            if (added > minimum && Mole.MatterState(type) == AtmosphereHelper.MatterState.Liquid &&
                MoleHelper.CanEvaporate(type))
            {
                arrivals.Add(new LiquidArrival(type.ToString(), added, Mole.MinLiquidPressure(type).ToDouble()));
            }
        }

        RoomAirAfter air = new RoomAirAfter(after.GasOnlyPressureKpa(), after.TemperatureK(),
            before.FrozenInAirMol(), after.FrozenInAirMol(), after.Boiled() != null);
        string? loss = RoomLiquids.Loss(air, arrivals);
        if (loss != null)
        {
            throw ApiErrors.Refused("would_burst",
                $"'{name}' is a room: {loss} (the game spawns ice only per cell of 50 mol and holds smaller amounts " +
                "out of the air). Move liquids into a tank or liquid network, or pass force to move it anyway.");
        }
    }

    private GasSnapshot Pooled(JoinedSet set, GasSnapshot[] snapshots)
    {
        GasSnapshot[] parts = new GasSnapshot[set.Members.Count];
        for (int index = 0; index < parts.Length; index++)
        {
            parts[index] = snapshots[IndexOf(set.Members[index].Atmosphere)];
        }

        return GasSnapshot.Pool(parts);
    }

    /// <summary>Whether any member's atmosphere changes matter state (a landing pad network's does not).</summary>
    private bool ChangesState => All.Members.Exists(member => member.ChangesState);

    private GasSnapshot? AfterBoiling(GasSnapshot[] snapshots) =>
        ChangesState ? GasSnapshot.Pool(snapshots).Boiled() : null;

    // A room lists no members (up to 1200 cells): its room and total say it all.
    internal GasSideView ToView(GasSnapshot[] after)
    {
        GasRoomView? room = Place.RoomView(after);
        List<GasMemberView> members = new List<GasMemberView>(room == null ? All.Members.Count : 0);
        for (int index = 0; room == null && index < All.Members.Count; index++)
        {
            GasEnd member = All.Members[index];
            members.Add(new GasMemberView(member.OwnerView(), new ThingId(member.Atmosphere.ReferenceId),
                Before[index].ToView(), after[index].ToView()));
        }

        List<ThingView> joiners = new List<ThingView>(All.Joiners.Count);
        foreach (Thing joiner in All.Joiners)
        {
            joiners.Add(GameLookup.ViewOf(joiner));
        }

        GasTotalView total = new GasTotalView(GasSnapshot.Pool(Before).ToView(), GasSnapshot.Pool(after).ToView(),
            AfterBoiling(after)?.ToView());
        return new GasSideView(members, total, joiners, room);
    }

    internal GasSnapshot[] Live()
    {
        GasSnapshot[] live = new GasSnapshot[All.Members.Count];
        for (int index = 0; index < live.Length; index++)
        {
            live[index] = GasSnapshot.Of(All.Members[index].Atmosphere);
        }

        return live;
    }
}

/// <summary>The predicted move: both sides before and after, and each gas moved.</summary>
internal sealed class GasPlan
{
    private GasPlan(GasMoveRequest request, GasSide source, GasSide? target, List<MovedGasView> moved)
    {
        Request = request;
        Source = source;
        Target = target;
        Moved = moved;
    }

    internal GasMoveRequest Request { get; }

    internal GasSide Source { get; }

    /// <summary>The target's joined set, or null when the gas is deleted.</summary>
    internal GasSide? Target { get; }

    internal List<MovedGasView> Moved { get; }

    internal static GasPlan Predict(GasMoveRequest request)
    {
        GasSide source = request.Source.SideOf(request.Joined);
        GasSide? target = request.Target?.SideOf(request.Joined);
        if (target != null && target.All.Members.Exists(member => source.All.Contains(member.Atmosphere)))
        {
            throw ApiErrors.Refused("same_joined_set",
                "'to' is joined to 'from' (the game mixes them every tick), so the gas would flow straight back. " +
                "Pass joined: false to move between them anyway.");
        }

        List<MovedGasView> moved = new List<MovedGasView>();
        foreach (Chemistry.GasType gas in request.Gases)
        {
            MovedGasView? one = PredictOne(request, source, target, gas);
            if (one != null)
            {
                moved.Add(one);
            }
        }

        if (moved.Count == 0)
        {
            throw ApiErrors.Refused("nothing_to_move", "The source holds none of the requested gases.");
        }

        return new GasPlan(request, source, target, moved);
    }

    // Each member gives its share of the amount (its moles over the set's), with its share of its own energy, exactly
    // as the applied move does with Mole.Remove. All of it goes into the named target.
    private static MovedGasView? PredictOne(GasMoveRequest request, GasSide source, GasSide? target,
        Chemistry.GasType gas)
    {
        int gasIndex = GasTypes.IndexOf(gas);
        List<GasEnd> members = source.For(gas).Members;
        double[] held = new double[members.Count];
        for (int index = 0; index < held.Length; index++)
        {
            held[index] = source.Before[source.IndexOf(members[index].Atmosphere)].MolesOf(gasIndex);
        }

        double[] taken = GasShares.Proportional(held, request.AmountMol);
        double movedMoles = 0.0;
        double movedEnergy = 0.0;
        for (int index = 0; index < taken.Length; index++)
        {
            if (!(taken[index] > 0.0))
            {
                continue;
            }

            GasSnapshot before = source.Before[source.IndexOf(members[index].Atmosphere)];
            (double moles, double energy) = before.Take(gasIndex, taken[index]);
            source.Change(members[index].Atmosphere, gasIndex, -moles, -energy);
            movedMoles += moles;
            movedEnergy += energy;
        }

        if (!(movedMoles > 0.0))
        {
            return null;
        }

        target?.Receive(gasIndex, movedMoles, movedEnergy);
        return new MovedGasView(gas.ToString(), movedMoles, movedEnergy);
    }

    internal void RefuseIfBursting()
    {
        Source.RefuseIfBursting("from");
        Target?.RefuseIfBursting("to");
        Target?.RefuseIfReceivingBursts("to");
    }

    internal MoveGasView ToView(long transferId) =>
        new MoveGasView(transferId.ToString(CultureInfo.InvariantCulture), MoveGasView.Queued, true, Request.Joined,
            Source.ToView(Source.After), Target?.ToView(Target.After), Moved, null);

    /// <summary>The prediction alone: nothing was queued, so there is no transfer id.</summary>
    internal MoveGasView ToDryRunView() =>
        new MoveGasView(null, MoveGasView.DryRun, true, Request.Joined, Source.ToView(Source.After),
            Target?.ToView(Target.After), Moved, null);
}
