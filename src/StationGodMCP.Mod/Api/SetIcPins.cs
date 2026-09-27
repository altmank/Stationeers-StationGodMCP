#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// set_ic_pins: an IC Housing's device pins d0..d5, set the way the housing's screws set them with a screwdriver
/// (Logicable._TryGetNextLogicDevice). Writes.
///
/// The pins are CircuitHousing.Devices. The chip resolves a pin afresh on every instruction through
/// CircuitHousing.GetLogicableFromIndex, which gives nothing for a device that is not on the housing's data network
/// (InputNetwork1.DataDeviceList) and, when the housing has no data network at all, gives the pinned device whatever it
/// is. There is no pin cache to clear. CircuitHousing._DeviceIDs is only the buffer a save is loaded through
/// (DeserializeSave, then OnFinishedLoad resolves it into Devices). On a host, the screwdriver raises one
/// NetworkUpdateFlags bit per screw so that CircuitHousing.BuildUpdate sends that pin's reference id to clients.
/// Every pin is checked before any is written, so a refused call changes nothing.
/// </summary>
internal static class SetIcPinsApi
{
    // The NetworkUpdateFlags bits the screwdriver raises for screws 0..5 (CircuitHousing.BuildUpdate).
    private static readonly ushort[] PinUpdateFlags = { 1024, 2048, 4096, 8192, 16384, 32768 };

    internal static SetIcPinsView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        IcTarget ic = Devices.RequireCircuitHolder(scope, args);
        CircuitHousing? housing = ic.Housing;
        if (housing == null)
        {
            throw ApiErrors.Refused("not_ic_housing",
                $"Circuit holder {ic.HolderId} ({ic.HolderThing.PrefabName}) is a {ic.Kind.Name}; only an IC " +
                "Housing's pins can be set.");
        }

        bool allowOffNetwork = args.OptionalBool("allow_off_network") ?? false;
        JObject? requested = args.OptionalObject("pins");
        if (requested == null || requested.Count == 0)
        {
            throw ApiErrors.InvalidArgument(
                "Argument 'pins' must be an object mapping at least one pin name (d0..d5) to a device reference ID " +
                "string, or to null to clear that pin.");
        }

        PinCheck check = new PinCheck(scope, housing, allowOffNetwork);
        SortedDictionary<int, Device?> planned = check.PlanAll(requested, Math.Min(housing.Devices.Length,
            PinUpdateFlags.Length));
        List<PinChangeView> changes = Write(housing, planned);
        return new SetIcPinsView(scope.Id, new ThingId(housing.ReferenceId), housing.InputNetwork1 != null, changes,
            IcPins.Describe(ic));
    }

    private static List<PinChangeView> Write(CircuitHousing housing, SortedDictionary<int, Device?> planned)
    {
        List<PinChangeView> changes = new List<PinChangeView>(planned.Count);
        foreach (KeyValuePair<int, Device?> entry in planned)
        {
            ILogicable? previous = housing.Devices[entry.Key];
            housing.Devices[entry.Key] = entry.Value;
            if (NetworkManager.IsServer)
            {
                housing.NetworkUpdateFlags |= PinUpdateFlags[entry.Key];
            }

            ThingId? before = previous == null ? null : new ThingId(previous.ReferenceId);
            ThingId? after = entry.Value == null ? null : new ThingId(entry.Value.ReferenceId);
            changes.Add(new PinChangeView(entry.Key, before, after, !ReferenceEquals(previous, entry.Value)));
        }

        return changes;
    }
}

/// <summary>The checks a pin's new device must pass, all made before any pin is written.</summary>
internal sealed class PinCheck
{
    private readonly DeviceScope _scope;
    private readonly CircuitHousing _housing;
    private readonly bool _allowOffNetwork;
    private readonly Dictionary<long, ScopedTarget>? _visible;

    internal PinCheck(DeviceScope scope, CircuitHousing housing, bool allowOffNetwork)
    {
        _scope = scope;
        _housing = housing;
        _allowOffNetwork = allowOffNetwork;
        _visible = scope.IsWorld ? null : scope.DeviceMap();
    }

    internal SortedDictionary<int, Device?> PlanAll(JObject requested, int pinCount)
    {
        SortedDictionary<int, Device?> planned = new SortedDictionary<int, Device?>();
        foreach (JProperty pin in requested.Properties())
        {
            int index = ParsePinName(pin.Name, pinCount);
            if (planned.ContainsKey(index))
            {
                throw ApiErrors.InvalidArgument($"Pin d{index} is listed more than once.");
            }

            planned[index] = Resolve(index, pin.Value);
        }

        return planned;
    }

    private static int ParsePinName(string name, int pinCount)
    {
        string text = name.Trim().ToLowerInvariant();
        if (text.Length == 2 && text[0] == 'd' && text[1] >= '0' && text[1] < '0' + pinCount)
        {
            return text[1] - '0';
        }

        throw ApiErrors.InvalidArgument($"'{name}' is not a pin name; use d0 to d{pinCount - 1}.");
    }

    private Device? Resolve(int index, JToken token)
    {
        if (token.Type == JTokenType.Null)
        {
            return null;
        }

        if (!ThingId.TryRead(token, out ThingId id) || id.Value <= 0)
        {
            throw ApiErrors.InvalidArgument(
                $"Pin d{index} must be a device reference ID encoded as a string, or null to clear the pin.");
        }

        Device device = RequireDevice(index, id);
        if (_visible != null && !_visible.ContainsKey(id.Value))
        {
            throw ApiErrors.Refused("device_not_found",
                $"Pin d{index}: device {id} is not visible {_scope.Where}.");
        }

        if (!IsReadable(device))
        {
            throw ApiErrors.Refused("logic_not_readable",
                $"Pin d{index}: device {id} ({device.DisplayName}) has no readable logic, so a screwdriver would " +
                "skip it.");
        }

        CableNetwork network = _housing.InputNetwork1;
        if (!_allowOffNetwork && network != null && !network.DataDeviceList.Contains(device))
        {
            throw ApiErrors.Refused("not_on_data_network",
                $"Pin d{index}: device {id} ({device.DisplayName}) is not on IC Housing {_housing.ReferenceId}'s " +
                "data network, so the chip would not reach it through the pin. Connect it to the housing's data " +
                "network, or pass allow_off_network to store it anyway.");
        }

        return device;
    }

    // Referencable.Find is the lookup the housing itself uses to resolve a pin from a network update
    // (CircuitHousing.ProcessUpdate). The chip's network check and the save load (OnFinishedLoad: Thing.Find<Device>)
    // both only keep Devices.
    private Device RequireDevice(int index, ThingId id)
    {
        IReferencable found = Referencable.Find<IReferencable>(id.Value);
        if (found == null)
        {
            throw ApiErrors.Refused(ApiErrors.ThingNotFoundCode, $"Pin d{index}: nothing has reference ID {id}.");
        }

        if (!(found is Device device))
        {
            throw ApiErrors.Refused("not_logic_device",
                $"Pin d{index}: {id} is a {found.GetType().Name}, not a logic device a pin can hold.");
        }

        if (ReferenceEquals(device, _housing))
        {
            throw ApiErrors.InvalidArgument(
                $"Pin d{index}: {id} is the IC Housing itself, which the chip already reaches as db.");
        }

        return device;
    }

    private static bool IsReadable(Device device)
    {
        try
        {
            return device.IsLogicReadable();
        }
        catch (Exception)
        {
            // Device.IsLogicReadable on a device mid-deconstruction; a screwdriver would skip it too.
            return false;
        }
    }
}
