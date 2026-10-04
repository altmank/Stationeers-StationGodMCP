#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// Chutes. One grade: every piece Kit (Chute) builds that carries items one way or the other (a plain Chute: straight,
/// corner, window and the 3, 5 and 10 long straights; and ChuteJunction). The kit is a MultiConstructor, not a merging
/// coil: a player picks the piece. Valves, overflows and splitters are Chute members of the network but have no
/// grade here: they are never chosen and never replaced. Chute devices (bins, inlets, outlets, the digital valves and
/// splitters) are devices on the network. Items ride in each piece's TransportSlot; a network holds no contents of its
/// own. CODE: Chute, ChuteJunction, ChuteNetwork; kit contents ASSET (resources.assets, 2026-09-27).
/// </summary>
internal sealed class ChuteFamily : UpgradeFamily
{
    internal static readonly Grade Chute = new Grade(0, 0, "chute");

    internal override string NetworkKind => "chute";

    internal override bool BuildsWith(MultiConstructor kit) => true;

    internal override bool IsMember(Thing thing) => thing is Chute;

    internal override bool IsPiece(Thing thing) => thing is Chute;

    internal override List<SmallGrid> NetworkMembers(ThingId networkId)
    {
        ChuteNetwork network = Referencable.Find<ChuteNetwork>(networkId.Value) ??
                               throw NetworkNotFound(NetworkKind, networkId);
        return NonNull(network.StructureList);
    }

    internal override List<Device> NetworkDevices(ThingId networkId)
    {
        ChuteNetwork network = Referencable.Find<ChuteNetwork>(networkId.Value) ??
                               throw NetworkNotFound(NetworkKind, networkId);
        return Runs.RunNetworks.Copy(network.DeviceList);
    }

    internal override Grade? GradeOf(Structure structure) => IsRunPiece(structure) ? Chute : null;

    internal override Grade? RunGradeOf(SmallGrid piece) => IsRunPiece(piece) ? Chute : null;

    internal override IReferencable? NetworkOf(SmallGrid piece) => piece is Chute chute ? chute.ChuteNetwork : null;

    internal override PipeContent? ContentOf(SmallGrid piece) => null;

    internal override bool IsMountedOn(Device device) => false;

    internal override bool MountedNow(Device device, SmallGrid piece) => false;

    internal override void Join(SmallGrid replacement, IReferencable network)
    {
        if (replacement is Chute chute && network is ChuteNetwork chutes && chute.ChuteNetwork != chutes)
        {
            chutes.Add(chute);
        }
    }

    // ChuteNetwork.Remove leaves the chute in DeviceRegister, as for pipes: each device registered through it is
    // registered through the replacements first. Chute.OnDestroy then rebuilds its neighbours' network whatever this
    // did, so chute networks take new ids after any removal or change (CODE).
    internal override void Leave(SmallGrid old, List<SmallGrid> replacements, IReferencable network)
    {
        if (!(old is Chute chute) || !(network is ChuteNetwork chutes))
        {
            return;
        }

        chutes.Remove(chute);
        foreach (Device device in new List<Device>(chutes.DeviceList))
        {
            HashSet<INetworkedStructure> through = chutes.GetDeviceRegistration(device);
            if (through == null || !through.Contains(chute))
            {
                continue;
            }

            foreach (SmallGrid next in ThroughWhich(device, replacements))
            {
                if (next is Chute nextChute)
                {
                    chutes.AddDevice(nextChute, device);
                }
            }

            chutes.RemoveDevice(chute, device);
        }
    }

    // Chute.OnDestroy rebuilds every connected chute's network, with no null check on it, before base.OnDestroy.
    internal override NeighbourRebuild OnDestroyRebuilds => NeighbourRebuild.Always;

    internal override double RatingOf(Structure structure) => 0.0;

    internal override bool Holds(SmallCell cell, SmallGrid piece) => cell.Chute == piece;

    internal override List<long> DeviceNetworks(Device device) => Ids(device.ConnectedChuteNetworks);

    internal override NetworkRecord Record(IReferencable network, List<PlannedSwap> swaps) =>
        throw new NotSupportedException("Chutes have one grade; there is nothing to upgrade.");

    internal override object Mapping(Api.Views.UpgradeMappingCount count, Structure source, Structure target) =>
        throw new NotSupportedException("Chutes have one grade; there is nothing to upgrade.");

    internal override string CleanNote => "Chute networks are rebuilt by the game after any piece is removed.";

    internal override string DeviceNote =>
        "Devices read the chute in their port's cell whenever a chute network is added to or taken from them " +
        "(DeviceImport.UpdateChutes).";

    /// <summary>What rides in the piece's transport slot; null when it is empty.</summary>
    internal static DynamicThing? ItemIn(SmallGrid piece) =>
        piece is Chute chute && chute.Slots != null && chute.Slots.Count > 0 &&
        chute.TransportSlot.Get() is DynamicThing item && item != null
            ? item
            : null;

    // A plain chute moves items end to end; a junction merges its inputs into its output. Both come from Kit (Chute).
    private static bool IsRunPiece(Thing thing) =>
        thing is Chute chute && (chute.GetType() == typeof(Chute) || chute is ChuteJunction);
}
