#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// atmosphere_contents: what gas or liquid one thing holds. Read only.
///
/// A thing reports its own internal atmosphere (Thing.InternalAtmosphere: canister, portable tank, tank, suit), the
/// pipe network a pipe belongs to (INetworkedPipe.PipeNetwork), the landing pad network a pad piece belongs to
/// (INetworkedLandingPad.LandingPadNetwork: every piece of one pad shares its atmosphere), every pipe network a device
/// is connected to
/// (Device.ConnectedPipeNetworks), and the internal atmosphere of each item in its slots (the canister in a tank
/// storage or an air conditioner); organ slots are left out. A pipe or landing pad network's reference id works too
/// (Referencable.Find), and so does an atmosphere id owned by a thing or a network, as water_sources reports it.
/// Room and world air cannot be looked up this way: no tool reports those ids.
/// </summary>
internal static class AtmosphereContentsApi
{
    internal static AtmosphereContentsView Handle(Args args)
    {
        ThingId id = args.ThingId("reference_id");
        PlayerOrigin origin = PlayerOrigin.Current();
        List<HeldAtmosphereEntryView> atmospheres = new List<HeldAtmosphereEntryView>();
        object subject;
        Thing thing = Thing.Find(id.Value);
        if (thing != null)
        {
            subject = AtmosphereOwners.OwnerOf(thing, origin);
            AddThing(thing, origin, atmospheres);
        }
        else if (Referencable.Find<AtmosphericsNetwork>(id.Value) is AtmosphericsNetwork network)
        {
            subject = AtmosphereOwners.OwnerOf(network, origin);
            atmospheres.Add(Entry(AtmosphereOwners.SourceOf(network), network.Atmosphere, subject, null));
        }
        else if (Referencable.Find<Atmosphere>(id.Value) is Atmosphere atmosphere &&
                 (atmosphere.Thing != null || atmosphere.AtmosphericsNetwork != null))
        {
            bool owned = atmosphere.Thing != null;
            subject = owned
                ? AtmosphereOwners.OwnerOf(atmosphere.Thing!, origin)
                : AtmosphereOwners.OwnerOf(atmosphere.AtmosphericsNetwork!, origin);
            atmospheres.Add(Entry(owned ? "internal" : AtmosphereOwners.SourceOf(atmosphere.AtmosphericsNetwork!),
                atmosphere, subject, null));
        }
        else
        {
            throw ApiErrors.Refused(ApiErrors.ThingNotFoundCode,
                $"No thing, pipe or landing pad network or thing's atmosphere has reference id {id}. Room and world " +
                "air cannot be " +
                "looked up this way.");
        }

        if (atmospheres.Count == 0)
        {
            string? name = thing != null ? thing.DisplayName : null;
            throw ApiErrors.Refused("no_atmosphere",
                $"{name} ({id}) holds no gas or liquid: it has no internal atmosphere, is not a pipe or a landing " +
                "pad piece, " +
                "is connected to no pipe network and has nothing with an atmosphere in its slots.");
        }

        return new AtmosphereContentsView(id, subject, atmospheres);
    }

    /// <summary>Whether this tool has anything to report for the thing: the same checks it runs for a reply.</summary>
    /// <remarks>Allocation free, so a world scan can filter on it: AddThing's tests without building entries.</remarks>
    internal static bool HoldsAtmosphere(Thing thing)
    {
        if (thing.InternalAtmosphere != null || (thing is INetworkedPipe pipe && pipe.PipeNetwork != null) ||
            (thing is INetworkedLandingPad pad && pad.LandingPadNetwork != null))
        {
            return true;
        }

        if (thing is Device device && device.ConnectedPipeNetworks != null)
        {
            List<PipeNetwork> networks = device.ConnectedPipeNetworks;
            for (int index = 0; index < networks.Count; index++)
            {
                if (networks[index] != null)
                {
                    return true;
                }
            }
        }

        List<Slot>? slots = thing.Slots;
        if (slots == null)
        {
            return false;
        }

        for (int index = 0; index < slots.Count; index++)
        {
            Slot slot = slots[index];
            if (slot != null && slot.Type != Slot.Class.Organ)
            {
                DynamicThing occupant = slot.Get();
                if (occupant != null && occupant.InternalAtmosphere != null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AddThing(Thing thing, PlayerOrigin origin, List<HeldAtmosphereEntryView> atmospheres)
    {
        if (thing.InternalAtmosphere != null)
        {
            atmospheres.Add(Entry("internal", thing.InternalAtmosphere, AtmosphereOwners.OwnerOf(thing, origin),
                null));
        }

        HashSet<long> seen = new HashSet<long>();
        if (thing is INetworkedPipe pipe && pipe.PipeNetwork != null && seen.Add(pipe.PipeNetwork.ReferenceId))
        {
            atmospheres.Add(Entry("pipe_network", pipe.PipeNetwork.Atmosphere,
                AtmosphereOwners.OwnerOf(pipe.PipeNetwork, origin), null));
        }

        if (thing is INetworkedLandingPad pad && pad.LandingPadNetwork != null &&
            seen.Add(pad.LandingPadNetwork.ReferenceId))
        {
            atmospheres.Add(Entry("landing_pad_network", pad.LandingPadNetwork.Atmosphere,
                AtmosphereOwners.OwnerOf(pad.LandingPadNetwork, origin), null));
        }

        if (thing is Device device && device.ConnectedPipeNetworks != null)
        {
            List<PipeNetwork> networks = new List<PipeNetwork>(device.ConnectedPipeNetworks);
            foreach (PipeNetwork network in networks)
            {
                if (network != null && seen.Add(network.ReferenceId))
                {
                    atmospheres.Add(Entry("connected_network", network.Atmosphere,
                        AtmosphereOwners.OwnerOf(network, origin), null));
                }
            }
        }

        AddSlots(thing, origin, atmospheres);
    }

    private static void AddSlots(Thing thing, PlayerOrigin origin, List<HeldAtmosphereEntryView> atmospheres)
    {
        if (thing.Slots == null)
        {
            return;
        }

        foreach (Slot slot in thing.Slots)
        {
            if (slot == null || slot.Type == Slot.Class.Organ)
            {
                continue;
            }

            DynamicThing occupant = slot.Get();
            if (occupant != null && occupant.InternalAtmosphere != null)
            {
                atmospheres.Add(Entry("slot", occupant.InternalAtmosphere, AtmosphereOwners.OwnerOf(occupant, origin),
                    new SlotRef(slot.SlotIndex, slot.DisplayName)));
            }
        }
    }

    private static HeldAtmosphereEntryView Entry(string source, Atmosphere? atmosphere, object owner, SlotRef? slot) =>
        new HeldAtmosphereEntryView(source, owner, slot, AtmosphereOwners.ContentsOf(atmosphere));
}
