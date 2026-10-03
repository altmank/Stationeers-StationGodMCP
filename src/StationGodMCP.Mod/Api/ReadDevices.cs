#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.DeviceReads;

namespace StationGodMCP.Api;

/// <summary>
/// read_devices: many reads of many things in one request, all in the same frame (one Update; not one atmospherics
/// tick: the atmosphere thread may run between two items). Per item: logic values as read_logic reads them, slot logic
/// as inspect_slots reads it, one atmosphere compact (its own, its internal one, or the pipe network at a device port),
/// and reagents as the reagents tool reads them. Values come back keyed by the name the client sent. An id that names
/// nothing fails its item; a part that cannot be read (the device out of scope) fails in the item's errors; a logic
/// name that does not read fails in logic_errors. Read only.
/// </summary>
internal static class ReadDevicesApi
{
    private static readonly PipeFamily Pipes = new PipeFamily();

    internal static ReadDevicesView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        DeviceReadRequest request = DeviceReadParse.Of(args.Optional("items"), args.Optional("include")) switch
        {
            DeviceReadParse.Parsed parsed => parsed.Request,
            DeviceReadParse.Refused refused => throw ApiErrors.InvalidArgument(refused.Message),
            _ => throw ApiErrors.InvalidArgument("Unknown read_devices request."),
        };

        return Read(scope, request);
    }

    /// <summary>An already parsed request in a scope: a subscription's sample, without reading its items again.</summary>
    internal static ReadDevicesView Read(DeviceScope scope, DeviceReadRequest request)
    {
        BatchBuilder batch = new BatchBuilder(request.Items.Count);
        for (int index = 0; index < request.Items.Count; index++)
        {
            DeviceReadItem item = request.Items[index];
            ThingId id = new ThingId(item.ReferenceId);
            Subject? subject = Subject.Find(id);
            if (subject == null)
            {
                batch.Failed(new DeviceReadFailedView(index, id, ApiErrors.ThingNotFound(id)));
                continue;
            }

            batch.Succeeded(new DeviceReadItemView(index, id, Read(scope, item, subject)));
        }

        return new ReadDevicesView(scope.Id, request.Clock ? GameClockApi.Now() : null, batch.Build());
    }

    private static DeviceReadParts Read(DeviceScope scope, DeviceReadItem item, Subject subject)
    {
        Dictionary<string, ErrorView> errors = new Dictionary<string, ErrorView>();
        ScopedTarget? device = null;
        if (item.Logic != null || item.Slots != null)
        {
            try
            {
                device = Devices.Require(scope, subject.Id);
            }
            catch (ApiException refused)
            {
                // Logic and slots both need the device: each fails with the same error.
                ErrorView error = new ErrorView(refused.Code, refused.Message);
                if (item.Logic != null)
                {
                    errors["logic"] = error;
                }

                if (item.Slots != null)
                {
                    errors["slots"] = error;
                }
            }
        }

        LogicReadings? logic = device != null && item.Logic != null ? Logic(device, item.Logic) : null;
        List<SlotReadView>? slots = device != null && item.Slots != null ? Slots(device, item.Slots) : null;
        AtmosphereReadView? atmosphere = item.Atmosphere != null
            ? Part("atmosphere", errors, () => Atmosphere(subject, item.Atmosphere))
            : null;
        ReagentsReadView? reagents = item.Reagents
            ? Part("reagents", errors, () => ReagentsApi.ReadingOf(subject.RequireThing()))
            : null;
        return new DeviceReadParts(logic, slots, atmosphere, reagents, errors);
    }

    private static T? Part<T>(string name, Dictionary<string, ErrorView> errors, System.Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (ApiException refused)
        {
            errors[name] = new ErrorView(refused.Code, refused.Message);
            return null;
        }
    }

    private static LogicReadings Logic(ScopedTarget device, IReadOnlyList<SentName> names)
    {
        Dictionary<string, double> values = new Dictionary<string, double>(names.Count, System.StringComparer.Ordinal);
        Dictionary<string, ErrorView> errors = new Dictionary<string, ErrorView>(System.StringComparer.Ordinal);
        foreach (SentName name in names)
        {
            try
            {
                values[name.Key] = LogicOps.ReadValue(device, LogicTypes.Parse(name.Token));
            }
            catch (ApiException refused)
            {
                errors[name.Key] = new ErrorView(refused.Code, refused.Message);
            }
        }

        return new LogicReadings(values, errors);
    }

    private static List<SlotReadView> Slots(ScopedTarget device, IReadOnlyList<SlotReadRequest> slots)
    {
        List<SlotReadView> views = new List<SlotReadView>(slots.Count);
        foreach (SlotReadRequest slot in slots)
        {
            try
            {
                SlotLogicTypes.RequireSlot(device, slot.Index);
            }
            catch (ApiException refused)
            {
                views.Add(SlotReadView.Failed(slot.Index, refused));
                continue;
            }

            views.Add(SlotReadView.Read(slot.Index, slot.Logic == null
                ? new LogicReadings(SlotLogicTypes.ReadAll(device, slot.Index),
                    new Dictionary<string, ErrorView>())
                : SlotLogic(device, slot.Index, slot.Logic)));
        }

        return views;
    }

    private static LogicReadings SlotLogic(ScopedTarget device, int index, IReadOnlyList<SentName> names)
    {
        Dictionary<string, double> values = new Dictionary<string, double>(names.Count, System.StringComparer.Ordinal);
        Dictionary<string, ErrorView> errors = new Dictionary<string, ErrorView>(System.StringComparer.Ordinal);
        foreach (SentName name in names)
        {
            try
            {
                values[name.Key] = SlotLogicTypes.Read(device, SlotLogicTypes.Parse(name.Token), index);
            }
            catch (ApiException refused)
            {
                errors[name.Key] = new ErrorView(refused.Code, refused.Message);
            }
        }

        return new LogicReadings(values, errors);
    }

    private static AtmosphereReadView Atmosphere(Subject subject, AtmosphereTarget target) =>
        target switch
        {
            AtmosphereTarget.Internal => Internal(subject),
            AtmosphereTarget.AtPort port => AtPort(subject, port.Port),
            _ => Own(subject),
        };

    private static AtmosphereReadView Internal(Subject subject)
    {
        Atmosphere? atmosphere = subject.Thing != null ? subject.Thing.InternalAtmosphere : null;
        return atmosphere != null
            ? AtmosphereOwners.CompactOf(atmosphere, "internal", null)
            : throw NoAtmosphere(subject, "has no internal atmosphere");
    }

    // What atmosphere_contents lists first for the id when it is the id's own: a thing's internal atmosphere, a pipe's
    // or landing pad piece's network, a network id's, an atmosphere id's. A device's connected networks are not its
    // own: they are asked for by port.
    private static AtmosphereReadView Own(Subject subject)
    {
        Thing? thing = subject.Thing;
        if (thing != null)
        {
            if (thing.InternalAtmosphere != null)
            {
                return AtmosphereOwners.CompactOf(thing.InternalAtmosphere, "internal", null);
            }

            if (thing is INetworkedPipe pipe && pipe.PipeNetwork != null)
            {
                return OfNetwork(pipe.PipeNetwork);
            }

            if (thing is INetworkedLandingPad pad && pad.LandingPadNetwork != null)
            {
                return OfNetwork(pad.LandingPadNetwork);
            }

            throw NoAtmosphere(subject,
                "has no atmosphere of its own (no internal atmosphere, not a pipe or landing pad piece); a device's " +
                "pipe network is read with atmosphere {port: n}");
        }

        if (subject.Network != null)
        {
            return OfNetwork(subject.Network);
        }

        Atmosphere owned = subject.Atmosphere!;
        return owned.Thing != null
            ? AtmosphereOwners.CompactOf(owned, "internal", null)
            : OfNetwork(owned.AtmosphericsNetwork!);
    }

    private static AtmosphereReadView AtPort(Subject subject, int port)
    {
        if (subject.Thing == null)
        {
            throw ApiErrors.InvalidArgument(
                $"atmosphere {{port: {port}}} needs a device; {subject.Id} is a {subject.Name}.");
        }

        ThingId networkId = NetworkHandles.OfPort(subject.Thing, port, "atmosphere.port", Pipes);
        PipeNetwork network = Referencable.Find<PipeNetwork>(networkId.Value) ??
                              throw ApiErrors.Refused("network_not_found", $"No pipe network has id {networkId}.");
        return OfNetwork(network);
    }

    private static AtmosphereReadView OfNetwork(AtmosphericsNetwork network) =>
        network.Atmosphere != null
            ? AtmosphereOwners.CompactOf(network.Atmosphere, AtmosphereOwners.SourceOf(network),
                new ThingId(network.ReferenceId))
            : throw ApiErrors.Refused("no_atmosphere",
                $"Network {network.ReferenceId} has no atmosphere yet (atmosphere_contents reports it as null).");

    private static ApiException NoAtmosphere(Subject subject, string why) =>
        ApiErrors.Refused("no_atmosphere", $"{subject.Name} ({subject.Id}) {why}.");

    /// <summary>What an item's reference id names: a thing, an atmospherics network, or an owned atmosphere.</summary>
    private sealed class Subject
    {
        private Subject(ThingId id, Thing? thing, AtmosphericsNetwork? network, Atmosphere? atmosphere)
        {
            Id = id;
            Thing = thing;
            Network = network;
            Atmosphere = atmosphere;
        }

        internal ThingId Id { get; }

        internal Thing? Thing { get; }

        internal AtmosphericsNetwork? Network { get; }

        internal Atmosphere? Atmosphere { get; }

        /// <summary>The thing, for a part only a thing has (reagents); thing_not_found for a network or atmosphere.</summary>
        internal Thing RequireThing() => Thing != null ? Thing : throw ApiErrors.ThingNotFound(Id);

        internal string Name => Thing != null ? Names.Of(Thing) : Network != null ? "network" : "atmosphere";

        // The lookups atmosphere_contents makes, in its order.
        internal static Subject? Find(ThingId id)
        {
            Thing thing = Thing.Find(id.Value);
            if (thing != null)
            {
                return new Subject(id, thing, null, null);
            }

            if (Referencable.Find<AtmosphericsNetwork>(id.Value) is AtmosphericsNetwork network)
            {
                return new Subject(id, null, network, null);
            }

            if (Referencable.Find<Atmosphere>(id.Value) is Atmosphere atmosphere &&
                (atmosphere.Thing != null || atmosphere.AtmosphericsNetwork != null))
            {
                return new Subject(id, null, null, atmosphere);
            }

            return null;
        }
    }
}
