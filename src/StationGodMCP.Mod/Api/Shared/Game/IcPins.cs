#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A circuit holder's pins d0..dN as stored (CircuitHousing.Devices), with the alias the chip gave each
/// (CircuitHousing._DeviceLabels, written by the chip's alias instruction through CircuitHousing.SetDeviceLabel) and
/// whether the chip reaches the device through it right now (CircuitHousing.GetLogicableFromIndex: an IC Housing only
/// reaches devices on its own data network).
/// </summary>
internal static class IcPins
{
    internal static List<IcPinView> Describe(IcTarget ic)
    {
        List<IcPinView> pins = new List<IcPinView>();
        CircuitHousing? housing = ic.Housing;
        if (housing == null)
        {
            // A worn holder (suit, tablet...) resolves its own pins; IcTarget.GetPins already goes through the holder.
            IList<ILogicable?> worn = ic.GetPins();
            for (int index = 0; index < worn.Count; index++)
            {
                pins.Add(new IcPinView(index, DeviceView(worn[index]), null, worn[index] != null));
            }

            return pins;
        }

        string[]? labels = GameMembers.HousingDeviceLabels.GetValue(housing) as string[];
        ILogicable[] devices = housing.Devices ?? Array.Empty<ILogicable>();
        for (int index = 0; index < devices.Length; index++)
        {
            bool reachable = devices[index] != null && Reaches(housing, index);
            string? label = labels != null && index < labels.Length ? labels[index] : null;
            pins.Add(new IcPinView(index, DeviceView(devices[index]), label, reachable));
        }

        return pins;
    }

    private static bool Reaches(CircuitHousing housing, int index)
    {
        try
        {
            return housing.GetLogicableFromIndex(index) != null;
        }
        catch (Exception)
        {
            // CircuitHousing.GetLogicableFromIndex on a pin whose device is half torn down; the chip reaches nothing.
            return false;
        }
    }

    private static ThingView? DeviceView(ILogicable? device)
    {
        if (device == null)
        {
            return null;
        }

        ThingId id = new ThingId(device.ReferenceId);
        try
        {
            Thing thing = device.GetAsThing;
            return new ThingView(id, thing != null ? thing.PrefabName : null, device.DisplayName);
        }
        catch (Exception)
        {
            // ILogicable.DisplayName on a device deconstructed since it was pinned; report its id alone.
            return new ThingView(id, null, null);
        }
    }
}
