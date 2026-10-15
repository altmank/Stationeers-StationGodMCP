#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.DeviceReads;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Subscriptions;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Many reads of many things in one request, as read logic's items form and a devices subscription read them, all in
/// the same frame (one Update; not one atmospherics
/// tick: the atmosphere thread may run between two items). Per item: logic values as read logic reads them, slot logic
/// as inspect slots reads it, one atmosphere compact (its own, its internal one, or the pipe network at a device port),
/// and reagents as inspect reagents reads them. Values come back keyed by the name the client sent. An id that names
/// nothing fails its item; a part that cannot be read (the device out of scope) fails in the item's errors; a logic
/// name that does not read fails in logic_errors. A subscription's sample passes the frame's shared reads
/// (SampleReads): what another sample of the frame read already is taken from there. Read only.
/// </summary>
internal static class DeviceReads
{
    private static readonly PipeFamily Pipes = new PipeFamily();

    /// <summary>
    /// A parsed request in a scope (read logic's items, a subscription's sample): each item by its id ("looking_at"
    /// the thing under the crosshair; label names the call in that refusal), failing alone.
    /// </summary>
    [Profiled]
    internal static ReadDevicesView Read(DeviceScope scope, DeviceReadRequest request, string label,
        SampleReads? shared = null)
    {
        BatchBuilder batch = new BatchBuilder(request.Items.Count);
        for (int index = 0; index < request.Items.Count; index++)
        {
            DeviceReadItem item = request.Items[index];
            ThingId id;
            try
            {
                id = TargetIds.Of(item.Id, label);
            }
            catch (ApiException refused)
            {
                batch.Failed(new DeviceReadFailedView(index, item.Id, refused));
                continue;
            }

            Subject? subject = Subject.Find(id);
            if (subject == null)
            {
                batch.Failed(new DeviceReadFailedView(index, id.ToString(), ApiErrors.ThingNotFound(id)));
                continue;
            }

            batch.Succeeded(new DeviceReadItemView(index, id, Read(scope, item, subject, shared)));
        }

        return new ReadDevicesView(scope.Id, request.Clock ? WorldClock.Now() : null, batch.Build());
    }

    private static DeviceReadParts Read(DeviceScope scope, DeviceReadItem item, Subject subject, SampleReads? shared)
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

        LogicReadings? logic = device != null && item.Logic != null ? Logic(device, item.Logic, shared) : null;
        List<SlotReadView>? slots = device != null && item.Slots != null ? Slots(device, item.Slots, shared) : null;
        AtmosphereReadView? atmosphere = item.Atmosphere != null
            ? Part<AtmosphereReadView>("atmosphere", errors, shared, new SampleReadKey(subject.Id.Value,
                SampleReadPart.Atmosphere, PortOf(item.Atmosphere), KindOf(item.Atmosphere)), subject, item.Atmosphere)
            : null;
        ReagentsReadView? reagents = item.Reagents
            ? Part<ReagentsReadView>("reagents", errors, shared,
                new SampleReadKey(subject.Id.Value, SampleReadPart.Reagents, 0, 0), subject, null)
            : null;
        return new DeviceReadParts(logic, slots, atmosphere, reagents, errors);
    }

    // An atmosphere part (target given) or the reagents (none), from the frame's shared reads when a sample of the
    // frame read it already; a refusal goes in errors under name.
    private static T? Part<T>(string name, Dictionary<string, ErrorView> errors, SampleReads? shared,
        SampleReadKey key, Subject subject, AtmosphereTarget? target) where T : class
    {
        SampleRead read;
        if (shared == null || !shared.TryTake(key, out read))
        {
            read = ReadPart(subject, target);
            shared?.Keep(key, read);
        }

        if (read.Other is ErrorView error)
        {
            errors[name] = error;
            return null;
        }

        return (T?)read.Other;
    }

    private static SampleRead ReadPart(Subject subject, AtmosphereTarget? target)
    {
        try
        {
            return new SampleRead(0.0, target != null
                ? Atmosphere(subject, target)
                : ThingReagents.ReadingOf(subject.RequireThing()));
        }
        catch (ApiException refused)
        {
            return new SampleRead(0.0, new ErrorView(refused.Code, refused.Message));
        }
    }

    private static int KindOf(AtmosphereTarget target) =>
        target switch
        {
            AtmosphereTarget.Internal => 1,
            AtmosphereTarget.AtPort => 2,
            _ => 0,
        };

    private static int PortOf(AtmosphereTarget target) => target is AtmosphereTarget.AtPort port ? port.Port : 0;

    private static LogicReadings Logic(ScopedTarget device, IReadOnlyList<SentName> names, SampleReads? shared)
    {
        Dictionary<string, double> values = new Dictionary<string, double>(names.Count, System.StringComparer.Ordinal);
        Dictionary<string, ErrorView> errors = new Dictionary<string, ErrorView>(System.StringComparer.Ordinal);
        foreach (SentName name in names)
        {
            LogicType type;
            try
            {
                type = LogicTypes.Parse(name.Token);
            }
            catch (ApiException refused)
            {
                errors[name.Key] = new ErrorView(refused.Code, refused.Message);
                continue;
            }

            SampleReadKey key = new SampleReadKey(device.ReferenceId, SampleReadPart.Logic, 0, (int)type);
            SampleRead read;
            if (shared == null || !shared.TryTake(key, out read))
            {
                read = ReadLogic(device, type);
                shared?.Keep(key, read);
            }

            Put(values, errors, name.Key, read);
        }

        return new LogicReadings(values, errors);
    }

    private static SampleRead ReadLogic(ScopedTarget device, LogicType type)
    {
        try
        {
            return new SampleRead(LogicOps.ReadValue(device, type), null);
        }
        catch (ApiException refused)
        {
            return new SampleRead(0.0, new ErrorView(refused.Code, refused.Message));
        }
    }

    private static void Put(Dictionary<string, double> values, Dictionary<string, ErrorView> errors, string key,
        SampleRead read)
    {
        if (read.Other is ErrorView error)
        {
            errors[key] = error;
        }
        else
        {
            values[key] = read.Number;
        }
    }

    private static List<SlotReadView> Slots(ScopedTarget device, IReadOnlyList<SlotReadRequest> slots,
        SampleReads? shared)
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
                ? new LogicReadings(SlotValues(device, slot.Index, shared), new Dictionary<string, ErrorView>())
                : SlotLogic(device, slot.Index, slot.Logic, shared)));
        }

        return views;
    }

    // Every value at the slot; a dictionary the frame's shared reads keep is never changed after.
    private static Dictionary<string, double> SlotValues(ScopedTarget device, int index, SampleReads? shared)
    {
        SampleReadKey key = new SampleReadKey(device.ReferenceId, SampleReadPart.SlotAll, index, 0);
        if (shared != null && shared.TryTake(key, out SampleRead kept))
        {
            return (Dictionary<string, double>)kept.Other!;
        }

        Dictionary<string, double> values = SlotLogicTypes.ReadAll(device, index);
        shared?.Keep(key, new SampleRead(0.0, values));
        return values;
    }

    private static LogicReadings SlotLogic(ScopedTarget device, int index, IReadOnlyList<SentName> names,
        SampleReads? shared)
    {
        Dictionary<string, double> values = new Dictionary<string, double>(names.Count, System.StringComparer.Ordinal);
        Dictionary<string, ErrorView> errors = new Dictionary<string, ErrorView>(System.StringComparer.Ordinal);
        foreach (SentName name in names)
        {
            LogicSlotType type;
            try
            {
                type = SlotLogicTypes.Parse(name.Token);
            }
            catch (ApiException refused)
            {
                errors[name.Key] = new ErrorView(refused.Code, refused.Message);
                continue;
            }

            SampleReadKey key = new SampleReadKey(device.ReferenceId, SampleReadPart.SlotLogic, index, (int)type);
            SampleRead read;
            if (shared == null || !shared.TryTake(key, out read))
            {
                read = ReadSlot(device, type, index);
                shared?.Keep(key, read);
            }

            Put(values, errors, name.Key, read);
        }

        return new LogicReadings(values, errors);
    }

    private static SampleRead ReadSlot(ScopedTarget device, LogicSlotType type, int index)
    {
        try
        {
            return new SampleRead(SlotLogicTypes.Read(device, type, index), null);
        }
        catch (ApiException refused)
        {
            return new SampleRead(0.0, new ErrorView(refused.Code, refused.Message));
        }
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

    // What inspect atmosphere lists first for the id when it is the id's own: a thing's internal atmosphere, a pipe's
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
                $"Network {network.ReferenceId} has no atmosphere yet (inspect atmosphere reports it as null).");

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

        // The lookups inspect atmosphere makes, in its order.
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
