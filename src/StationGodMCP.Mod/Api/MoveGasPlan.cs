#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>An atmosphere's gas: quantity and energy per single gas type, read with the game's Mole getters.</summary>
internal sealed class GasSnapshot
{
    // Atmosphere.LiquidPressureOffset: the pressure reported for an atmosphere completely full of liquid, and the
    // kPa scale of the offset below that.
    private const double FullOfLiquidKpa = 1013249.9694824219;
    private const double LiquidOffsetScaleKpa = 10.0;

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

    /// <summary>Several atmospheres as one: moles, energies and volumes added, as AtmosphereHelper.Mix pools.</summary>
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
        foreach (GasSnapshot part in parts)
        {
            for (int index = 0; index < count; index++)
            {
                moles[index] += part._moles[index];
                energies[index] += part._energies[index];
            }

            volume += part.VolumeL;
        }

        return new GasSnapshot(moles, energies, volume, AtmosphereHelper.AtmosphereMode.Network);
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
        double gasVolume = Math.Max(VolumeL - liquidVolume, minimumGas);
        double pressure = IdealGas.Pressure(mixture.GetTotalMolesGasses, mixture.Temperature,
            new VolumeLitres(gasVolume)).ToDouble();
        bool liquidMode = Mode == AtmosphereHelper.AtmosphereMode.Thing ||
                          Mode == AtmosphereHelper.AtmosphereMode.Network ||
                          Mode == AtmosphereHelper.AtmosphereMode.None;
        if (liquidMode && liquidVolume > VolumeL - minimumGas)
        {
            pressure = Math.Max(pressure, LiquidOffsetKpa(mixture, liquidVolume));
        }

        return pressure;
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
    private GasSide(JoinedSet gasSet, JoinedSet liquidSet)
    {
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

    internal JoinedSet GasSet { get; }

    internal JoinedSet LiquidSet { get; }

    internal JoinedSet All { get; }

    internal GasSnapshot[] Before { get; }

    internal GasSnapshot[] After { get; }

    internal static GasSide Of(GasEnd end, bool joined)
    {
        if (!joined)
        {
            JoinedSet alone = JoinedSet.Alone(end);
            return new GasSide(alone, alone);
        }

        return new GasSide(
            JoinedSet.Collect(end, AtmosphereHelper.MatterState.Gas),
            JoinedSet.Collect(end, AtmosphereHelper.MatterState.Liquid));
    }

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
        double predicted = SettledPressureKpa();
        foreach (GasEnd member in All.Members)
        {
            PressureRating? broken = member.FirstBroken(predicted);
            if (broken != null)
            {
                throw ApiErrors.Refused("would_burst",
                    $"'{name}' would settle at {predicted:0.#} kPa against a limit of " +
                    $"{broken.LimitFor(predicted):0.#} kPa ({broken.Member}: {broken.MaxKpa:0.#} kPa across, " +
                    $"{broken.OutsideKpa:0.#} kPa outside). Pass force to move it anyway.");
            }
        }
    }

    internal GasSideView ToView(GasSnapshot[] after)
    {
        List<GasMemberView> members = new List<GasMemberView>(All.Members.Count);
        for (int index = 0; index < All.Members.Count; index++)
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

        GasTotalView total = new GasTotalView(GasSnapshot.Pool(Before).ToView(), GasSnapshot.Pool(after).ToView());
        return new GasSideView(members, total, joiners);
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

    internal bool AnyGone()
    {
        foreach (GasEnd member in All.Members)
        {
            if (member.IsGone)
            {
                return true;
            }
        }

        return false;
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
        GasSide source = GasSide.Of(request.Source, request.Joined);
        GasEnd? targetEnd = request.Target;
        GasSide? target = targetEnd != null ? GasSide.Of(targetEnd, request.Joined) : null;
        if (targetEnd != null && source.All.Contains(targetEnd.Atmosphere))
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
        JoinedSet set = source.For(gas);
        double total = 0.0;
        foreach (GasEnd member in set.Members)
        {
            total += source.Before[source.IndexOf(member.Atmosphere)].MolesOf(gasIndex);
        }

        double amount = request.AmountMol.HasValue ? Math.Min(total, request.AmountMol.Value) : total;
        if (!(amount > 0.0))
        {
            return null;
        }

        double movedMoles = 0.0;
        double movedEnergy = 0.0;
        foreach (GasEnd member in set.Members)
        {
            GasSnapshot before = source.Before[source.IndexOf(member.Atmosphere)];
            (double moles, double energy) = before.Take(gasIndex, amount * before.MolesOf(gasIndex) / total);
            source.Change(member.Atmosphere, gasIndex, -moles, -energy);
            movedMoles += moles;
            movedEnergy += energy;
        }

        if (target != null)
        {
            target.Change(request.Target!.Atmosphere, gasIndex, movedMoles, movedEnergy);
        }

        return new MovedGasView(gas.ToString(), movedMoles, movedEnergy);
    }

    internal void RefuseIfBursting()
    {
        Source.RefuseIfBursting("from");
        Target?.RefuseIfBursting("to");
    }

    internal MoveGasView ToView(long transferId) =>
        new MoveGasView(transferId.ToString(CultureInfo.InvariantCulture), "queued", true, Request.Joined,
            Source.ToView(Source.After), Target?.ToView(Target.After), Moved, null);
}
