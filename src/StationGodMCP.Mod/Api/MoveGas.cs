#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Networks;
using Newtonsoft.Json.Linq;
using Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// move_gas: move gas between two atmospheres, or delete it, through the game's own gas API. Writes.
///
/// The gas calls (CODE, Assets.Scripts.Atmospherics): Atmosphere.GasMixture is a public field holding a GasMixture
/// struct, so calls on it change the atmosphere in place. GasMixture.Remove(GasType, MoleQuantity) calls Mole.Remove,
/// which caps the amount at what is there and takes the same share of the gas's energy (Energy * removed / Quantity).
/// The removed Mole goes into the target with GasMixture.Add(Mole) (Mole.Add), so heat moves with the gas and nothing
/// is recomputed from temperature.
///
/// When (CODE): the atmospherics simulation runs on a worker thread (GameManager.GameTick) while this API runs on the
/// main thread, and the game keeps no lock on an atmosphere's gas. On the main thread, Mole getters return last tick's
/// cached values (Mole.Quantity, AtmosphereHelper.CanWriteAccess => ThreadedManager.IsThread). The game never changes
/// gas from the main thread directly: it queues an AtmosphericEventInstance, as the addgas console command does
/// (AtmosphericEventInstance.CreateAdd), applied by AtmosphericsController.HandleMainThreadEvents at the start of the
/// worker-thread tick, before any atmospherics job runs. The game's events cannot carry a removed Mole's own energy
/// into the add, so a move is queued here instead and applied by a Harmony postfix on that same method, on that same
/// thread (MoveGasQueue.cs). The first reply is therefore a prediction from last tick's cached values (status queued);
/// its transfer_id reads the outcome as applied, from live values. While the game is paused, nothing is applied.
///
/// Pressure (CODE): Atmosphere.PressureGassesAndLiquids is IdealGas.Pressure of the gas moles at the mixture's
/// temperature in GetGasVolume = max(Volume - liquid volume, GetMinimumGasVolume(Mode)); a Thing or Network
/// atmosphere mostly full of liquid reports Atmosphere.LiquidPressureOffset instead when that is higher. Predictions
/// use the same formula on the combined mixture.
///
/// Burst limits (CODE): each is a limit on |inside - outside|, where outside is
/// AtmosphericsController.SampleGlobalAtmosphere at the cell. A GasCanister is damaged at >= MaxPressure
/// (GasCanister.OnAtmosphericTick), a portable tank (DynamicGasCanister) at >= MaxSetting. A pipe network bursts a
/// member when the difference exceeds that member's MaxPressure at any of its cells that can hold air
/// (AtmosphericsNetwork.ScanStructuresAndEvaluate). Other things have no limit here and are not checked. Both ends are
/// checked, because pulling gas out can burst a thing from outside too.
///
/// Joined sets (MoveGasJoins.cs): atmospheres the game mixes every tick are one unit unless joined is false. From a
/// set, each gas is taken from every member that carries its matter state, in proportion to what each holds
/// (amount_mol caps the set's total), in the same tick. Into a set, the gas goes into the named member and the game
/// spreads it. The burst check uses each side's settled pressure: the members pooled as AtmosphereHelper.Mix pools
/// two, checked against every member's rating. Exact for sets joined for all matter; an approximation where a join
/// carries only gases or only liquids (PortablesConnector). The sets are found when the move is asked for.
///
/// Receiving side (CODE, AtmosphericsNetwork.EvaluateIncorrectMatterState, Mole.StateChangeLiquid): liquid over
/// GameConstants.MAX_ALLOWED_RATIO_VOLUME_LIQUIDS_GAS_PIPE of a gas pipe network's volume, and frozen moles over
/// MinFrozenMolesToDamage in any network, damage it every tick; a liquid arriving somewhere that changes state boils
/// over the next ticks, so the pressure once it has boiled is checked too. Each is refused only when the move makes it
/// worse (GasSide.RefuseIfReceivingBursts).
///
/// Landing pads (CODE, Networks.LandingPadNetwork): every pad piece is an INetworkedLandingPad on one
/// LandingPadNetwork, an AtmosphericsNetwork whose atmosphere all pieces share (volume: 500 L per
/// LandingPadGasStorage, 1 L per other piece; content type All, so no liquid rule; PreventStateChange, so liquids stay
/// liquid there). Its burst rating is Chemistry.Limits.MAXPressureGasPipe for every piece. No device joins it to
/// another atmosphere every tick: the pad's pumps and tank connectors move a volume per tick.
///
/// Sync (CODE): nothing is marked by hand; Atmosphere.PrepareForWrite sets the gas network flags from the mixture's
/// own dirty tracking. Host only. Not supported: world cells (refused) and rooms. The planet (from "planet", delete only): PlanetGasRemoval.cs.
/// </summary>
internal static class MoveGasApi
{
    internal static object Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves gas.");
        }

        if (args.Has("transfer_id"))
        {
            args.Reject("transfer_id", "from", "to", "delete", "gases", "amount_mol", "force", "joined");
            return GasMoves.Outcome(args.ThingId("transfer_id").Value);
        }

        if (args.IsWord("from", "planet"))
        {
            return PlanetGasRemoval.Handle(args);
        }

        GasMoveRequest request = GasMoveRequest.Parse(args);
        GasPlan plan = GasPlan.Predict(request);
        if (!request.Force)
        {
            plan.RefuseIfBursting();
        }

        return plan.ToView(GasMoves.Enqueue(plan));
    }
}

/// <summary>Where the moved gas goes: into another atmosphere, or nowhere. A closed set.</summary>
internal abstract class GasDestination
{
    private GasDestination()
    {
    }

    internal sealed class Into : GasDestination
    {
        internal Into(GasEnd target)
        {
            Target = target;
        }

        internal GasEnd Target { get; }
    }

    internal sealed class Deleted : GasDestination
    {
        internal static readonly Deleted Instance = new Deleted();

        private Deleted()
        {
        }
    }
}

/// <summary>A validated move_gas request.</summary>
internal sealed class GasMoveRequest
{
    private const int MaximumGasNames = 256;

    private GasMoveRequest(GasEnd source, GasDestination destination, Chemistry.GasType[] gases, double? amountMol,
        bool force, bool joined)
    {
        Source = source;
        Destination = destination;
        Gases = gases;
        AmountMol = amountMol;
        Force = force;
        Joined = joined;
    }

    internal GasEnd Source { get; }

    internal GasDestination Destination { get; }

    internal Chemistry.GasType[] Gases { get; }

    /// <summary>Moles per gas, or null for all of each.</summary>
    internal double? AmountMol { get; }

    internal bool Force { get; }

    /// <summary>
    /// Treat the atmospheres the game mixes every tick as one unit (the default), or only the named ones.
    /// </summary>
    internal bool Joined { get; }

    internal GasEnd? Target => (Destination as GasDestination.Into)?.Target;

    internal static GasMoveRequest Parse(Args args)
    {
        GasEnd source = GasEnd.Resolve(args.ThingId("from"), "from");
        GasDestination destination = ParseDestination(args);
        if (destination is GasDestination.Into into && ReferenceEquals(into.Target.Atmosphere, source.Atmosphere))
        {
            throw ApiErrors.InvalidArgument("'from' and 'to' are the same atmosphere.");
        }

        Chemistry.GasType[] gases = args.Has("gases") ? ParseGases(args.Array("gases", MaximumGasNames)) : GasTypes.All;
        return new GasMoveRequest(source, destination, gases, args.OptionalPositiveDouble("amount_mol"),
            args.OptionalBool("force") ?? false, args.OptionalBool("joined") ?? true);
    }

    private static GasDestination ParseDestination(Args args)
    {
        bool hasTarget = args.Has("to");
        bool delete = args.OptionalBool("delete") ?? false;
        if (hasTarget == delete)
        {
            throw ApiErrors.InvalidArgument("Pass 'to', or 'delete: true' to destroy the gas; not both, not neither.");
        }

        return delete
            ? GasDestination.Deleted.Instance
            : new GasDestination.Into(GasEnd.Resolve(args.ThingId("to"), "to"));
    }

    internal static Chemistry.GasType[] ParseGases(JArray array)
    {
        List<Chemistry.GasType> gases = new List<Chemistry.GasType>(array.Count);
        foreach (JToken item in array)
        {
            Chemistry.GasType gas = ParseGas(item);
            if (!gases.Contains(gas))
            {
                gases.Add(gas);
            }
        }

        return gases.ToArray();
    }

    private static Chemistry.GasType ParseGas(JToken item)
    {
        string? name = item.Type == JTokenType.String ? item.Value<string>() : null;
        if (name != null && Enum.TryParse(name.Trim(), true, out Chemistry.GasType gas) && GasTypes.IndexOf(gas) >= 0)
        {
            return gas;
        }

        throw ApiErrors.Refused("unknown_gas",
            $"'{item}' is not a gas name as atmosphere_contents reports them (Oxygen, Nitrogen, CarbonDioxide, " +
            "LiquidOxygen, Steam...).");
    }
}

/// <summary>One end of a move: an atmosphere, what owns it, and the pressure ratings that can burst it.</summary>
internal sealed class GasEnd
{
    private GasEnd(Atmosphere atmosphere, Thing? thing, AtmosphericsNetwork? network)
    {
        Atmosphere = atmosphere;
        Thing = thing;
        Network = network;
    }

    internal Atmosphere Atmosphere { get; }

    /// <summary>The owning thing, for a thing's internal atmosphere; else null.</summary>
    internal Thing? Thing { get; }

    /// <summary>The owning pipe network, for a network atmosphere; else null.</summary>
    internal AtmosphericsNetwork? Network { get; }

    /// <summary>
    /// Whether the game changes this atmosphere's matter state (Atmosphere.StateChange): not for a thing or network
    /// with PreventStateChange, such as a landing pad network.
    /// </summary>
    internal bool ChangesState =>
        Thing != null ? !Thing.PreventStateChange : Network != null && !Network.PreventStateChange;

    // Unity's == reports a destroyed Thing as null.
    internal bool IsGone =>
        Atmosphere.BeingDestroyed || (!ReferenceEquals(Thing, null) && (Thing == null || Thing.IsBeingDestroyed));

    internal static GasEnd? OfNetwork(AtmosphericsNetwork? network) =>
        network != null && network.Atmosphere != null ? new GasEnd(network.Atmosphere, null, network) : null;

    internal static GasEnd? OfThing(Thing? thing) =>
        thing != null && thing.InternalAtmosphere != null ? new GasEnd(thing.InternalAtmosphere, thing, null) : null;

    // Resolved as atmosphere_contents resolves an id, narrowed to exactly one atmosphere: a thing's internal
    // atmosphere, else a pipe's network, else a landing pad piece's pad network (INetworkedLandingPad: every piece
    // shares one atmosphere); a pipe or landing pad network id; an atmosphere id owned by a thing or a network. A
    // device with no internal atmosphere of its own is refused rather than guessed: pass the network's id.
    internal static GasEnd Resolve(ThingId id, string name)
    {
        Thing thing = Thing.Find(id.Value);
        if (thing != null)
        {
            return FromThing(thing, name);
        }

        if (Referencable.Find<AtmosphericsNetwork>(id.Value) is AtmosphericsNetwork network)
        {
            return FromNetwork(network, name);
        }

        if (Referencable.Find<Atmosphere>(id.Value) is Atmosphere atmosphere)
        {
            return FromAtmosphere(atmosphere, name);
        }

        throw ApiErrors.Refused("atmosphere_not_found", $"'{name}': no thing, pipe network or atmosphere has id {id}.");
    }

    private static GasEnd FromThing(Thing thing, string name)
    {
        if (thing.InternalAtmosphere != null)
        {
            return Checked(new GasEnd(thing.InternalAtmosphere, thing, null), name);
        }

        if (thing is INetworkedPipe pipe && pipe.PipeNetwork != null)
        {
            return FromNetwork(pipe.PipeNetwork, name);
        }

        if (thing is INetworkedLandingPad pad && pad.LandingPadNetwork != null)
        {
            return FromNetwork(pad.LandingPadNetwork, name);
        }

        throw ApiErrors.Refused("no_atmosphere",
            $"'{name}': {thing.DisplayName} ({thing.ReferenceId}) has no internal atmosphere and is not a pipe or " +
            "a landing pad piece. For a device's pipe network, pass the network's id.");
    }

    private static GasEnd FromNetwork(AtmosphericsNetwork network, string name)
    {
        if (network.Atmosphere == null)
        {
            throw ApiErrors.Refused("no_atmosphere",
                $"'{name}': {AtmosphereOwners.SourceOf(network).Replace('_', ' ')} {network.ReferenceId} has no " +
                "atmosphere yet.");
        }

        return Checked(new GasEnd(network.Atmosphere, null, network), name);
    }

    private static GasEnd FromAtmosphere(Atmosphere atmosphere, string name)
    {
        if (atmosphere.Mode == AtmosphereHelper.AtmosphereMode.Thing && atmosphere.Thing != null)
        {
            return Checked(new GasEnd(atmosphere, atmosphere.Thing, null), name);
        }

        if (atmosphere.Mode == AtmosphereHelper.AtmosphereMode.Network && atmosphere.AtmosphericsNetwork != null)
        {
            return Checked(new GasEnd(atmosphere, null, atmosphere.AtmosphericsNetwork), name);
        }

        throw ApiErrors.Refused("refused",
            $"'{name}': atmosphere {atmosphere.ReferenceId} is a {atmosphere.Mode} atmosphere. The planet, world " +
            "cells and rooms are not moved to or from.");
    }

    private static GasEnd Checked(GasEnd end, string name)
    {
        if (end.Atmosphere.IsGlobalAtmosphere || end.Atmosphere.Mode == AtmosphereHelper.AtmosphereMode.World)
        {
            throw ApiErrors.Refused("refused", $"'{name}' is the planet's or a world cell's atmosphere.");
        }

        if (end.IsGone)
        {
            throw ApiErrors.Refused("atmosphere_not_found", $"'{name}' is being destroyed.");
        }

        return end;
    }

    internal GasOwnerView OwnerView()
    {
        if (Thing != null)
        {
            return new GasOwnerView("thing", new ThingId(Thing.ReferenceId), Thing.PrefabName,
                Thing.DisplayName);
        }

        AtmosphericsNetwork network = Network!;
        return new GasOwnerView(AtmosphereOwners.SourceOf(network), new ThingId(network.ReferenceId), null,
            network.DisplayName);
    }

    /// <summary>The first rating this pressure breaks, or null. Ratings come from the game's burst rules.</summary>
    internal PressureRating? FirstBroken(double pressureKpa)
    {
        List<PressureRating> ratings = Ratings();
        for (int index = 0; index < ratings.Count; index++)
        {
            if (ratings[index].IsBrokenBy(pressureKpa))
            {
                return ratings[index];
            }
        }

        return null;
    }

    private List<PressureRating> Ratings()
    {
        List<PressureRating> ratings = new List<PressureRating>();
        if (Thing is GasCanister canister)
        {
            ratings.Add(new PressureRating(canister.MaxPressure.ToDouble(), OutsideKpa(canister.WorldGrid), true,
                canister.DisplayName));
        }
        else if (Thing is DynamicGasCanister tank)
        {
            ratings.Add(new PressureRating(tank.MaxSetting, OutsideKpa(tank.WorldGrid), true, tank.DisplayName));
        }
        else if (Network != null)
        {
            AddNetworkRatings(Network, ratings);
        }

        return ratings;
    }

    // AtmosphericsNetwork.ScanStructuresAndEvaluate: every member, at each of its cells that can hold air. A landing
    // pad's gas storage also damages itself at the same rating reached, not only passed
    // (LandingPadGasStorage.OnAtmosphericTick against LandingPadNetwork.MaxPressureKpa).
    private static void AddNetworkRatings(AtmosphericsNetwork network, List<PressureRating> ratings)
    {
        List<INetworkedStructure> members;
        lock (network.StructureList)
        {
            members = new List<INetworkedStructure>(network.StructureList);
        }

        for (int index = 0; index < members.Count; index++)
        {
            if (members[index] is INetworkedAtmospherics member && member.GetAsThing != null)
            {
                AddMemberRatings(member, ratings);
            }
        }
    }

    private static void AddMemberRatings(INetworkedAtmospherics member, List<PressureRating> ratings)
    {
        List<WorldGrid> grids;
        lock (member.CurrentGrids)
        {
            grids = new List<WorldGrid>(member.CurrentGrids);
        }

        GridController grid = member.GetAsThing.GridController;
        if (grid == null)
        {
            return;
        }

        for (int index = 0; index < grids.Count; index++)
        {
            if (grid.CanContainAtmos(grids[index]))
            {
                ratings.Add(new PressureRating(member.MaxPressure.ToDouble(), OutsideKpa(grids[index]),
                    member is LandingPadGasStorage, member.GetAsThing.DisplayName));
            }
        }
    }

    private static double OutsideKpa(WorldGrid cell)
    {
        Atmosphere? outside = AtmosphericsController.World?.SampleGlobalAtmosphere(cell);
        return outside != null ? outside.PressureGassesAndLiquids.ToDouble() : 0.0;
    }
}

/// <summary>
/// A burst rating: the thing is damaged when |inside - outside| passes MaxKpa (or reaches it, when inclusive).
/// </summary>
internal sealed class PressureRating
{
    internal PressureRating(double maxKpa, double outsideKpa, bool inclusive, string member)
    {
        MaxKpa = maxKpa;
        OutsideKpa = outsideKpa;
        Inclusive = inclusive;
        Member = member;
    }

    internal double MaxKpa { get; }

    internal double OutsideKpa { get; }

    internal bool Inclusive { get; }

    internal string Member { get; }

    internal bool IsBrokenBy(double pressureKpa)
    {
        double difference = Math.Abs(pressureKpa - OutsideKpa);
        return Inclusive ? difference >= MaxKpa : difference > MaxKpa;
    }

    /// <summary>The pressure bound on the side the given pressure is on.</summary>
    internal double LimitFor(double pressureKpa) =>
        pressureKpa >= OutsideKpa ? OutsideKpa + MaxKpa : OutsideKpa - MaxKpa;
}
